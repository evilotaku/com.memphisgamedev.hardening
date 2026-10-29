using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// Ship hygiene (#8): builds the distributable zip from an ALLOWLIST instead of zipping the build folder.
    /// Unity writes <c>*_BackUpThisFolder_ButDontShipItWithYourGame</c> (GameAssembly.pdb + il2cppOutput C++)
    /// and Burst's <c>*_DoNotShip</c> folders INSIDE the build folder, so "zip the folder" leaks them.
    /// <para>Refuses to package when the build is a development build, was made with the F8/F9 test menus
    /// (<c>SHOPSIM_TEST_MENUS</c>), or is Mono (plain IL ships), when symbols
    /// or do-not-ship files are inside the allowlisted payload, or when the shipped binaries don't match the
    /// expected digests for this version in <c>ExpectedDigests.cs</c> (an enforcing host would kick every
    /// player running it).</para>
    /// Output: <c>Builds/Ship/&lt;Product&gt;-&lt;version&gt;-&lt;stamp&gt;.zip</c> with a top-level <c>&lt;Product&gt;/</c> folder.
    /// </summary>
    public static class ShipPackager
    {
        private const string MenuPath = "Tools/Builds/Package Build for Distribution...";
        private static readonly string RecordPath = Path.Combine("Library", "ShipHygiene", "builds.txt");

        private static string ExpectedDigestsPath => HardeningSettings.Current.expectedDigestsPath;

        // Whole-file digests the server checks; the "!text" ones derive from the same files.
        private static IReadOnlyList<string> DigestTargets => HardeningSettings.Current.DigestTargets;

        /// <summary>
        /// Remembers how a build was made, keyed by exe path: dev flag, version, test-menus flag (tab-separated).
        /// Called post-build.
        /// </summary>
        internal static void RecordBuild(BuildReport report)
        {
            string exe = Path.GetFullPath(report.summary.outputPath);
            bool dev = (report.summary.options & BuildOptions.Development) != 0;
            var lines = ReadRecords().Where(l => !l.StartsWith(exe + "\t", StringComparison.OrdinalIgnoreCase)).ToList();
            lines.Add(exe + "\t" + (dev ? "1" : "0") + "\t" + PlayerSettings.bundleVersion + "\t" +
                      (TestMenusBuildOption.Enabled ? "1" : "0"));
            Directory.CreateDirectory(Path.GetDirectoryName(RecordPath));
            File.WriteAllLines(RecordPath, lines);
        }

        [MenuItem(MenuPath, false, 121)]
        private static void PackageFromMenu()
        {
            string exe = EditorUtility.OpenFilePanel("Pick the built game exe", "Builds", "exe");
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                string zip = Package(exe);
                EditorUtility.RevealInFinder(zip);
            }
            catch (BuildFailedException e)
            {
                Debug.LogError(e.Message);
                EditorUtility.DisplayDialog("Not packaged", e.Message, "OK");
            }
        }

        /// <summary>Packages a built Windows player. Returns the zip path; throws BuildFailedException on refusal.</summary>
        public static string Package(string exePath)
        {
            exePath = Path.GetFullPath(exePath);
            string exeDir = Path.GetDirectoryName(exePath);
            string product = Path.GetFileNameWithoutExtension(exePath);
            string dataName = product + "_Data";
            string dataDir = Path.Combine(exeDir, dataName);
            if (!File.Exists(exePath) || !Directory.Exists(dataDir))
                throw Refuse("'" + exePath + "' is not a Windows player build (no " + dataName + " folder).");

            // 1. Release IL2CPP only.
            string record = ReadRecords().FirstOrDefault(l => l.StartsWith(exePath + "\t", StringComparison.OrdinalIgnoreCase));
            if (record == null)
                throw Refuse("No build record for this exe. Rebuild it from this Editor so the build type is known.");
            string[] parts = record.Split('\t');
            if (parts.Length < 4)
                throw Refuse("This build's record predates the test-menus check. Rebuild it from this Editor.");
            if (parts[1] == "1")
                throw Refuse("This is a development build (it has the F8/F9 debug menus and test drivers). Ship release builds only.");
            if (parts[3] == "1")
                throw Refuse("This build includes the F8/F9 debug menus (" + TestMenusBuildOption.Define + "). Turn off " +
                    "Tools > Builds > Include Test Menus and rebuild.");
            string version = parts[2];
            if (!File.Exists(Path.Combine(exeDir, "GameAssembly.dll")) ||
                File.Exists(Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll")))
                throw Refuse("This is a Mono build (plain IL in " + dataName + "/Managed). Ship IL2CPP builds only.");

            // 2. Allowlist: the exe, its _Data, root DLLs, the crash handler and D3D12/. Everything else stays out.
            var include = new List<string>();
            var skipped = new List<string>();
            foreach (string file in Directory.GetFiles(exeDir))
            {
                string name = Path.GetFileName(file);
                bool keep = name.Equals(Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase);
                (keep ? include : skipped).Add(file);
            }
            foreach (string dir in Directory.GetDirectories(exeDir))
            {
                string name = Path.GetFileName(dir);
                if (name.Equals(dataName, StringComparison.OrdinalIgnoreCase) || name.Equals("D3D12", StringComparison.OrdinalIgnoreCase))
                    include.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
                else
                    skipped.Add(dir);
            }

            var leaks = include.Where(f => ShipHygieneBuildStep.IsSymbolFile(f) || ShipHygieneBuildStep.IsDoNotShip(f))
                .Select(f => Rel(exeDir, f)).ToList();
            if (leaks.Count > 0)
                throw Refuse("Symbols or do-not-ship files are inside the payload: " + string.Join(", ", leaks));

            // 3. The shipped binaries must be the ones the server expects for this version.
            var expected = ReadExpectedDigests(version);
            if (expected.Count == 0)
                throw Refuse("ExpectedDigests.cs has no digests for version '" + version + "'.");
            var cfg = HardeningSettings.Current;
            var paths = new Dictionary<string, string>
            {
                [cfg.gameAssemblyName] = Path.Combine(exeDir, cfg.gameAssemblyName),
                [cfg.nativeCoreName] = cfg.NativeCorePath(dataDir),
                [cfg.il2cppMetadataName] = cfg.MetadataPath(dataDir),
            };
            foreach (string target in DigestTargets)
            {
                if (!File.Exists(paths[target])) throw Refuse(target + " is missing from the build.");
                string actual = IntegrityHashing.HashFile(paths[target]).ToString();
                if (!expected.TryGetValue(target, out string want) || want != actual)
                    throw Refuse(target + " doesn't match ExpectedDigests.cs for version " + version +
                        " (a later build overwrote them?). Rebuild, then deploy the module and commit the digests, before shipping.");
            }

            // 4. Zip under a top-level <Product>/ folder.
            string outDir = Path.GetFullPath(Path.Combine("Builds", "Ship"));
            Directory.CreateDirectory(outDir);
            string zipPath = Path.Combine(outDir, product + "-" + version + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
            {
                foreach (string file in include)
                {
                    var entry = zip.CreateEntry(product + "/" + Rel(exeDir, file).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Optimal);
                    using (var src = File.OpenRead(file))
                    using (var dst = entry.Open())
                        src.CopyTo(dst);
                }
            }

            Debug.Log("[ShipHygiene] Packaged " + include.Count + " files → " + zipPath +
                (skipped.Count > 0 ? ". Left out: " + string.Join(", ", skipped.Select(s => Rel(exeDir, s))) : "") +
                ". Make sure the IntegrityModule with these digests is deployed.");
            return zipPath;
        }

        /// <summary>Whole-file digests for one build id, read from the Map block of ExpectedDigests.cs.</summary>
        private static Dictionary<string, string> ReadExpectedDigests(string buildId)
        {
            var result = new Dictionary<string, string>();
            if (!File.Exists(ExpectedDigestsPath)) return result;
            string text = File.ReadAllText(ExpectedDigestsPath);
            int chunks = text.IndexOf("Chunks", StringComparison.Ordinal);
            if (chunks > 0) text = text.Substring(0, chunks); // Map only; Chunks has the same keys
            var block = Regex.Match(text, "\\[\"" + Regex.Escape(buildId) + "\"\\]\\s*=\\s*new Dictionary<string, string>\\s*\\{(.*?)\\}",
                RegexOptions.Singleline);
            if (!block.Success) return result;
            foreach (Match m in Regex.Matches(block.Groups[1].Value, "\\[\"([^\"]+)\"\\]\\s*=\\s*\"(\\d+)\""))
                result[m.Groups[1].Value] = m.Groups[2].Value;
            return result;
        }

        private static IEnumerable<string> ReadRecords() =>
            File.Exists(RecordPath) ? File.ReadAllLines(RecordPath) : Array.Empty<string>();

        private static string Rel(string root, string path) => path.Substring(root.Length + 1);

        private static BuildFailedException Refuse(string why) => new BuildFailedException("[ShipHygiene] Not packaged: " + why);
    }
}
