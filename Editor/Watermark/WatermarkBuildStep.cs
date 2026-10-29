using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MemphisGameDev.Hardening.Editor
{
    /// <summary>
    /// Our own build watermark (Obfuz 4.0.0 ships its watermark pass disabled).
    ///
    /// Each release carries a 64-byte marker = HMAC-SHA256(secret, "shopsim-watermark|&lt;version&gt;|0" and "|1"),
    /// with the secret from SHOPSIM_WATERMARK_SECRET (process, then the Windows user environment) and the version
    /// from Player Settings. Only someone with the secret can compute a marker, so finding one in a copy proves
    /// it came from that release, and nobody can plant it in their own build.
    ///
    ///   • On Editor load and before a build: writes the marker into the git-ignored
    ///     Scripts/Core/BuildWatermark.Generated.cs as a byte-array literal (Obfuz leaves array literals alone:
    ///     ShopSimConstEncryptRules.xml turns array encryption off). A build that finds it out of date regenerates
    ///     it and stops, because the player must compile the new file.
    ///   • A second, unrelated marker ("|native" in the label) goes into the native core through the git-ignored
    ///     native/shopsim_core/shopsim_core_watermark.h, written before every native build (NativeCoreBuildStep).
    ///   • After a build: searches the output for both markers. A release build missing either fails; a verified
    ///     version is recorded in WatermarkReleases.txt (versions only, nothing secret).
    ///   • Tools &gt; Builds &gt; Watermark: create the secret, or check any build folder for every recorded release.
    ///
    /// Keep the secret stable and backed up: rotating it makes older releases unverifiable.
    /// </summary>
    [InitializeOnLoad]
    public class WatermarkBuildStep : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        /// <summary>Name of the environment variable holding the watermark secret (from <see cref="HardeningSettings"/>).</summary>
        public static string SecretVar => HardeningSettings.Current.watermarkSecretVar;
        public static string GeneratedPath => HardeningSettings.Current.watermarkGeneratedPath;
        public static string ReleasesPath => HardeningSettings.Current.watermarkReleasesPath;

        private const string CreateSecretMenu = "Tools/Builds/Watermark/Create Secret";
        private const string CheckMenu = "Tools/Builds/Watermark/Check a Build Folder…";
        private const int MarkerLength = 64;

        public int callbackOrder => 100;

        static WatermarkBuildStep()
        {
            EditorApplication.delayCall += () => EnsureGenerated();
        }

        public static string Secret()
        {
            string value = Environment.GetEnvironmentVariable(SecretVar);
            if (string.IsNullOrWhiteSpace(value) && Application.platform == RuntimePlatform.WindowsEditor)
            {
                value = Environment.GetEnvironmentVariable(SecretVar, EnvironmentVariableTarget.User);
            }
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>The marker compiled into the game code (IL2CPP metadata).</summary>
        public static byte[] Marker(string secret, string version)
        {
            return Derive(secret, "shopsim-watermark|" + version);
        }

        /// <summary>The second, unrelated marker compiled into the native core (shopsim_core.dll).</summary>
        public static byte[] NativeMarker(string secret, string version)
        {
            return Derive(secret, "shopsim-watermark|" + version + "|native");
        }

        private static byte[] Derive(string secret, string label)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                byte[] a = hmac.ComputeHash(Encoding.UTF8.GetBytes(label + "|0"));
                byte[] b = hmac.ComputeHash(Encoding.UTF8.GetBytes(label + "|1"));
                return a.Concat(b).ToArray();
            }
        }

        // ------------------------------------------------------------------ generated source

        /// <summary>Brings BuildWatermark.Generated.cs in line with the secret and version. True when it changed.</summary>
        public static bool EnsureGenerated()
        {
            EnsureNativeHeader();
            string secret = Secret();
            string desired = secret == null ? null : Source(Marker(secret, PlayerSettings.bundleVersion), PlayerSettings.bundleVersion);
            string current = File.Exists(GeneratedPath) ? File.ReadAllText(GeneratedPath) : null;
            if (desired == current) return false;
            if (desired == null)
            {
                AssetDatabase.DeleteAsset(GeneratedPath);
                Debug.Log("[Watermark] No " + SecretVar + "; removed the generated marker.");
            }
            else
            {
                File.WriteAllText(GeneratedPath, desired);
                AssetDatabase.ImportAsset(GeneratedPath);
                Debug.Log("[Watermark] Marker generated for version " + PlayerSettings.bundleVersion + ".");
            }
            return true;
        }

        /// <summary>
        /// Brings native/shopsim_core/shopsim_core_watermark.h in line with the secret and version (deleted without a
        /// secret). Called before every native core build, which compiles it in. True when it changed.
        /// </summary>
        public static bool EnsureNativeHeader()
        {
            string secret = Secret();
            string version = PlayerSettings.bundleVersion;
            string desired = null;
            if (secret != null)
            {
                byte[] marker = NativeMarker(secret, version);
                var sb = new StringBuilder();
                sb.Append("// Generated by WatermarkBuildStep for version ").Append(version).Append(". Git-ignored: never commit.\n");
                sb.Append("#define SSC_WATERMARK_BYTES \\\n");
                for (int i = 0; i < marker.Length; i += 16)
                {
                    sb.Append("    ");
                    for (int j = i; j < Math.Min(i + 16, marker.Length); j++)
                    {
                        sb.Append("0x").Append(marker[j].ToString("x2"));
                        if (j < marker.Length - 1) sb.Append(", ");
                    }
                    sb.Append(i + 16 < marker.Length ? " \\\n" : "\n");
                }
                desired = sb.ToString();
            }
            string current = File.Exists(NativeHeaderPath) ? File.ReadAllText(NativeHeaderPath) : null;
            if (desired == current) return false;
            if (desired == null) File.Delete(NativeHeaderPath);
            else File.WriteAllText(NativeHeaderPath, desired);
            return true;
        }

        /// <summary>The native core's generated watermark header; also used by <c>NativeCoreBuildStep</c>.</summary>
        public static string NativeHeaderPath => HardeningSettings.Current.NativeSourcePath("shopsim_core_watermark.h");

        private static string Source(byte[] marker, string version)
        {
            var sb = new StringBuilder();
            sb.Append("// <auto-generated> by WatermarkBuildStep for version ").Append(version).Append(". Git-ignored: never commit.\n");
            sb.Append("namespace MemphisGameDev.Hardening\n{\n");
            sb.Append("    internal static partial class BuildWatermark\n    {\n");
            sb.Append("        static partial void Load(ref byte[] marker)\n        {\n");
            sb.Append("            marker = new byte[]\n            {\n");
            for (int i = 0; i < marker.Length; i += 16)
            {
                sb.Append("                ");
                for (int j = i; j < Math.Min(i + 16, marker.Length); j++) sb.Append("0x").Append(marker[j].ToString("x2")).Append(", ");
                sb.Length -= 1;
                sb.Append('\n');
            }
            sb.Append("            };\n        }\n    }\n}\n");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ build

        public void OnPreprocessBuild(BuildReport report)
        {
            bool development = (report.summary.options & BuildOptions.Development) != 0;
            if (Secret() == null)
            {
                if (!development) throw new BuildFailedException("[Watermark] " + SecretVar + " is not set. Run Tools > Builds > Watermark > Create Secret, or set it on this build machine. Release builds must be watermarked.");
                Debug.LogWarning("[Watermark] " + SecretVar + " is not set; this development build is not watermarked.");
            }
            if (EnsureGenerated())
            {
                throw new BuildFailedException("[Watermark] The marker was out of date for version " + PlayerSettings.bundleVersion
                    + " and has been regenerated. Wait for the scripts to recompile, then build again.");
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            string secret = Secret();
            if (secret == null || report.summary.result == BuildResult.Failed) return;
            string version = PlayerSettings.bundleVersion;
            string folder = Path.GetDirectoryName(report.summary.outputPath);
            var hits = FindInFolder(folder, new[] { Marker(secret, version), NativeMarker(secret, version) }, LikelyFiles).ToList();
            string code = hits.Where(h => h.marker == 0).Select(h => Path.GetFileName(h.file)).FirstOrDefault();
            string native = hits.Where(h => h.marker == 1).Select(h => Path.GetFileName(h.file)).FirstOrDefault();
            bool hasNativeCore = Directory.EnumerateFiles(folder, HardeningSettings.Current.nativeCoreName, SearchOption.AllDirectories).Any();

            var missing = new List<string>();
            if (code == null) missing.Add("the game-code marker");
            if (hasNativeCore && native == null) missing.Add("the native core marker");
            if (missing.Count > 0)
            {
                string message = "[Watermark] Version " + version + " build is missing " + string.Join(" and ", missing) + " (" + folder + ").";
                if ((report.summary.options & BuildOptions.Development) == 0) throw new BuildFailedException(message);
                Debug.LogWarning(message);
                return;
            }
            Record(version);
            Debug.Log("[Watermark] Verified: version " + version + " markers found in " + code + (native != null ? " and " + native : "") + ".");
        }

        // Where the markers end up: IL2CPP metadata or code (or the Mono assembly), and the native core.
        private static bool LikelyFiles(string path)
        {
            string name = Path.GetFileName(path);
            var cfg = HardeningSettings.Current;
            return name == cfg.il2cppMetadataName || name == cfg.gameAssemblyName || name == cfg.monoAssemblyName || name == cfg.nativeCoreName;
        }

        private static void Record(string version)
        {
            var lines = File.Exists(ReleasesPath) ? File.ReadAllLines(ReleasesPath).ToList() : new List<string> { "# Watermarked releases (version<TAB>first verified). Versions only; the marker needs " + SecretVar + "." };
            if (lines.Any(l => l.Split('\t')[0] == version)) return;
            lines.Add(version + "\t" + DateTime.Now.ToString("yyyy-MM-dd"));
            File.WriteAllText(ReleasesPath, string.Join("\n", lines) + "\n");
            AssetDatabase.ImportAsset(ReleasesPath);
        }

        public static List<string> RecordedVersions()
        {
            var versions = File.Exists(ReleasesPath)
                ? File.ReadAllLines(ReleasesPath).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l => l.Split('\t')[0]).ToList()
                : new List<string>();
            if (!versions.Contains(PlayerSettings.bundleVersion)) versions.Add(PlayerSettings.bundleVersion);
            return versions;
        }

        // ------------------------------------------------------------------ search

        /// <summary>Every (file, marker index) where a marker occurs, scanning files that pass the filter.</summary>
        public static IEnumerable<(string file, int marker)> FindInFolder(string folder, IReadOnlyList<byte[]> markers, Func<string, bool> filter = null)
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (file.Contains("_BackUpThisFolder_ButDontShipItWithYourGame")) continue;
                if (filter != null && !filter(file)) continue;
                foreach (int index in FindInFile(file, markers)) yield return (file, index);
            }
        }

        private static IEnumerable<int> FindInFile(string path, IReadOnlyList<byte[]> markers)
        {
            const int chunk = 1 << 22;
            int overlap = MarkerLength - 1;
            var buffer = new byte[chunk + overlap];
            var found = new HashSet<int>();
            using (var stream = File.OpenRead(path))
            {
                int carry = 0, read;
                while ((read = stream.Read(buffer, carry, chunk)) > 0)
                {
                    int total = carry + read;
                    var window = new ReadOnlySpan<byte>(buffer, 0, total);
                    for (int i = 0; i < markers.Count; i++)
                    {
                        if (!found.Contains(i) && window.IndexOf(markers[i]) >= 0) found.Add(i);
                    }
                    if (found.Count == markers.Count) break;
                    carry = Math.Min(overlap, total);
                    Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                }
            }
            return found;
        }

        /// <summary>Checks a build folder (or a copy of the game) for the marker of every recorded release.</summary>
        public static string CheckFolder(string folder)
        {
            string secret = Secret();
            if (secret == null) return SecretVar + " is not set, so no marker can be computed.";
            var versions = RecordedVersions();
            // Two markers per release: even index = game code, odd = native core.
            var markers = versions.SelectMany(v => new[] { Marker(secret, v), NativeMarker(secret, v) }).ToList();
            var hits = FindInFolder(folder, markers).ToList();
            if (hits.Count == 0) return "No watermark from releases " + string.Join(", ", versions) + " found in " + folder + ".";
            return string.Join("\n", hits.Select(h => "Release " + versions[h.marker / 2] + (h.marker % 2 == 0 ? " game-code" : " native core")
                + " watermark found in " + h.file));
        }

        // ------------------------------------------------------------------ menu

        /// <summary>Creates the secret in the user environment if there isn't one. False when one already exists.</summary>
        public static bool CreateSecret()
        {
            if (Secret() != null) return false;
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string value = Convert.ToBase64String(bytes);
            Environment.SetEnvironmentVariable(SecretVar, value);
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                Environment.SetEnvironmentVariable(SecretVar, value, EnvironmentVariableTarget.User);
            }
            EnsureGenerated();
            return true;
        }

        [MenuItem(CreateSecretMenu)]
        private static void CreateSecretFromMenu()
        {
            if (!CreateSecret())
            {
                EditorUtility.DisplayDialog("Watermark secret", SecretVar + " is already set. Keep it: changing it makes older releases unverifiable.", "OK");
                return;
            }
            EditorUtility.DisplayDialog("Watermark secret", "Created " + SecretVar + " in your user environment. Back it up and copy it to every release build machine.", "OK");
        }

        [MenuItem(CheckMenu)]
        private static void CheckFromMenu()
        {
            string folder = EditorUtility.OpenFolderPanel("Build folder to check", "Builds", "");
            if (string.IsNullOrEmpty(folder)) return;
            string result = CheckFolder(folder);
            Debug.Log("[Watermark] " + result);
            EditorUtility.DisplayDialog("Watermark check", result, "OK");
        }
    }
}
