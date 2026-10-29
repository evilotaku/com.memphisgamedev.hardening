using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// Build-time automation for the obfuscated native core (shopsim_core.dll).
    ///
    /// The DLL is produced by native/shopsim_core/build.ps1 (xollvm toolchain) and is git-ignored,
    /// so it only exists on a machine that has run that script. This step:
    ///   • Tools &gt; Builds &gt; Require Native Core — toggles the OBFUZ_REQUIRE_NATIVE_CORE define on
    ///     Standalone, which drops the managed economy fallback from the shipped player (see
    ///     ShopSimCore). Off by default.
    ///   • Tools &gt; Builds &gt; Build Native Core Now — runs build.ps1 on demand (one click instead of
    ///     remembering the full command line).
    ///   • Tools &gt; Builds &gt; Auto-build Native Core on Player Build — when on, runs build.ps1 as part
    ///     of every Standalone player build so the DLL is always freshly obfuscated. Off by default
    ///     (a native rebuild adds clang+obfuscation time and needs the toolchain present).
    ///   • A guard that FAILS a Standalone build when Require Native Core is on but the DLL is missing
    ///     or older than shopsim_core.cpp — so a "require native" player can never ship without a
    ///     current protected core.
    ///
    /// Toolchain location is resolved from (in order): the EditorPref set via the menu, the XOLLVM_BIN
    /// environment variable, then common defaults. The path is per-machine and never committed.
    /// </summary>
    public class NativeCoreBuildStep : IPreprocessBuildWithReport
    {
        private const string Define = "OBFUZ_REQUIRE_NATIVE_CORE";

        private const string RequireMenu   = "Tools/Builds/Require Native Core (drop managed fallback)";
        private const string AutoBuildMenu = "Tools/Builds/Auto-build Native Core on Player Build";
        private const string BuildNowMenu  = "Tools/Builds/Build Native Core Now";
        private const string SetPathMenu   = "Tools/Builds/Set xollvm Toolchain Folder\u2026";

        private const string BinPref   = "ShopSim.Xollvm.Bin";
        private const string SeedPref  = "ShopSim.Xollvm.Seed";
        private const string AutoPref  = "ShopSim.Xollvm.AutoBuild";

        // Run before Obfuz (10000) and the integrity postprocessor so the DLL is present/fresh first.
        public int callbackOrder => 0;

        // ---- paths ----
        private static string ProjectRoot => HardeningSettings.ProjectRoot;
        private static string ScriptDir   => HardeningSettings.Current.NativeSourceDir;
        private static string ScriptPath  => Path.Combine(ScriptDir, "build.ps1");
        private static string CppPath     => Path.Combine(ScriptDir, "shopsim_core.cpp");
        private static string WatermarkHeaderPath => WatermarkBuildStep.NativeHeaderPath;
        private static string LicenceHeaderPath => LanLicenceKeys.NativeHeaderPath;
        private static string DllRel      => HardeningSettings.Current.NativePluginAssetPath;
        private static string DllPath     => Path.Combine(ProjectRoot, DllRel);

        private static int  Seed      => EditorPrefs.GetInt(SeedPref, 1337);
        private static bool AutoBuild => EditorPrefs.GetBool(AutoPref, false);

        // ================= menu: Require Native Core (scripting define) =================

        private static bool RequireEnabled
        {
            get => StandaloneDefines().Contains(Define);
            set
            {
                var defines = StandaloneDefines().Where(d => d != Define).ToList();
                if (value) defines.Add(Define);
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, defines.ToArray());
            }
        }

        private static string[] StandaloneDefines()
        {
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone, out string[] defines);
            return defines;
        }

        [MenuItem(RequireMenu, false, 100)]
        private static void ToggleRequire()
        {
            RequireEnabled = !RequireEnabled;
            Debug.Log("[NativeCore] Require Native Core is now " +
                      (RequireEnabled ? "ON — Standalone players drop the managed economy fallback."
                                      : "OFF — Standalone players keep the managed fallback."));
        }

        [MenuItem(RequireMenu, true)]
        private static bool ToggleRequireValidate() { Menu.SetChecked(RequireMenu, RequireEnabled); return true; }

        // ================= menu: auto-build toggle =================

        [MenuItem(AutoBuildMenu, false, 101)]
        private static void ToggleAutoBuild()
        {
            EditorPrefs.SetBool(AutoPref, !AutoBuild);
            Debug.Log("[NativeCore] Auto-build on player build is now " + (AutoBuild ? "ON" : "OFF") + ".");
        }

        [MenuItem(AutoBuildMenu, true)]
        private static bool ToggleAutoBuildValidate() { Menu.SetChecked(AutoBuildMenu, AutoBuild); return true; }

        // ================= menu: set toolchain folder =================

        [MenuItem(SetPathMenu, false, 102)]
        private static void SetToolchainFolder()
        {
            string start = EditorPrefs.GetString(BinPref, "");
            string picked = EditorUtility.OpenFolderPanel("Select the xollvm toolchain bin folder (contains clang++.exe, opt.exe)",
                string.IsNullOrEmpty(start) ? "" : start, "");
            if (string.IsNullOrEmpty(picked)) return;
            if (!IsValidToolchain(picked))
            {
                EditorUtility.DisplayDialog("xollvm toolchain",
                    "That folder does not contain clang++.exe and opt.exe.\n\nPick the 'bin' folder of the xollvm toolchain.", "OK");
                return;
            }
            EditorPrefs.SetString(BinPref, picked);
            Debug.Log("[NativeCore] xollvm toolchain set to " + picked);
        }

        // ================= menu: build now =================

        [MenuItem(BuildNowMenu, false, 103)]
        private static void BuildNow()
        {
            if (!TryResolveToolchain(out string bin, out string why))
            {
                EditorUtility.DisplayDialog("Build Native Core", "Could not find the xollvm toolchain.\n\n" + why +
                    "\n\nUse Tools > Builds > Set xollvm Toolchain Folder.", "OK");
                return;
            }
            try
            {
                string log = RunBuildScript(bin, Seed);
                Debug.Log("[NativeCore] build.ps1 succeeded:\n" + log);
                EditorUtility.DisplayDialog("Build Native Core", "shopsim_core.dll built successfully.\n\n" + Tail(log, 6), "OK");
            }
            catch (Exception e)
            {
                Debug.LogError("[NativeCore] build.ps1 failed:\n" + e.Message);
                EditorUtility.DisplayDialog("Build Native Core", "Build FAILED:\n\n" + e.Message, "OK");
            }
        }

        // ================= build hook =================

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platformGroup != BuildTargetGroup.Standalone) return;

            if (AutoBuild)
            {
                if (!TryResolveToolchain(out string bin, out string why))
                    throw new BuildFailedException("[NativeCore] Auto-build Native Core is ON but the xollvm toolchain was not found. " +
                        why + " Set it via Tools > Builds > Set xollvm Toolchain Folder, or turn auto-build off.");
                Debug.Log("[NativeCore] Auto-building shopsim_core.dll with xollvm before the player build\u2026");
                string log = RunBuildScript(bin, Seed); // throws BuildFailedException on failure
                Debug.Log("[NativeCore] native core rebuilt:\n" + Tail(log, 4));
            }

            if (RequireEnabled)
            {
                if (!File.Exists(DllPath))
                    throw new BuildFailedException("[NativeCore] " + Define + " is set but " + DllRel + " is missing. " +
                        "Build it (Tools > Builds > Build Native Core Now) or enable Auto-build Native Core — a 'require native' " +
                        "player must not ship without the protected core.");

                if (File.Exists(CppPath) &&
                    File.GetLastWriteTimeUtc(DllPath) < File.GetLastWriteTimeUtc(CppPath))
                    throw new BuildFailedException("[NativeCore] " + DllRel + " is OLDER than shopsim_core.cpp. Rebuild the native " +
                        "core (Tools > Builds > Build Native Core Now) so the shipped DLL matches the current source.");

                if (WatermarkBuildStep.EnsureNativeHeader() ||
                    (File.Exists(WatermarkHeaderPath) && File.GetLastWriteTimeUtc(DllPath) < File.GetLastWriteTimeUtc(WatermarkHeaderPath)))
                    throw new BuildFailedException("[NativeCore] " + DllRel + " doesn't carry this version's watermark. Rebuild the native " +
                        "core (Tools > Builds > Build Native Core Now), or turn on Auto-build Native Core.");

                if (LanLicenceKeys.EnsureNativeHeader() ||
                    (File.Exists(LicenceHeaderPath) && File.GetLastWriteTimeUtc(DllPath) < File.GetLastWriteTimeUtc(LicenceHeaderPath)))
                    throw new BuildFailedException("[NativeCore] " + DllRel + " doesn't carry the current LAN licence key. Rebuild the native " +
                        "core (Tools > Builds > Build Native Core Now), or turn on Auto-build Native Core.");
            }

            // A release player refuses LAN play without a valid licence, and can only verify one with the key compiled in.
            if ((report.summary.options & BuildOptions.Development) == 0 && LanLicenceKeys.Key() == null)
                throw new BuildFailedException("[NativeCore] Release builds need " + LanLicenceKeys.KeyVar + " (the LAN licence key): without it " +
                    "no player could ever host or join a LAN game. See LanLicenceKeys for how to create it.");
        }

        // ================= toolchain resolution + process =================

        private static bool TryResolveToolchain(out string bin, out string why)
        {
            string pref = EditorPrefs.GetString(BinPref, "");
            string env  = Environment.GetEnvironmentVariable("XOLLVM_BIN");
            foreach (var cand in new[] { pref, env, @"D:\Apps\xollvm\bin", @"C:\tools\xollvm\bin" })
            {
                if (!string.IsNullOrEmpty(cand) && IsValidToolchain(cand)) { bin = cand; why = null; return true; }
            }
            bin = null;
            why = "Looked at the configured path, the XOLLVM_BIN env var, and the common defaults; none contained clang++.exe and opt.exe.";
            return false;
        }

        private static bool IsValidToolchain(string bin) =>
            !string.IsNullOrEmpty(bin) &&
            File.Exists(Path.Combine(bin, "clang++.exe")) &&
            File.Exists(Path.Combine(bin, "opt.exe"));

        /// <summary>Runs build.ps1; returns combined output, throws BuildFailedException on non-zero exit.</summary>
        private static string RunBuildScript(string bin, int seed)
        {
            if (!File.Exists(ScriptPath))
                throw new BuildFailedException("[NativeCore] build.ps1 not found at " + ScriptPath);
            // The release watermark and licence key headers (git-ignored) must match the current secrets and version.
            WatermarkBuildStep.EnsureNativeHeader();
            LanLicenceKeys.EnsureNativeHeader();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\" " +
                            $"-ToolchainBin \"{bin}\" -Seed {seed}",
                WorkingDirectory = ScriptDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            string stdout, stderr; int exit;
            using (var p = Process.Start(psi))
            {
                // Read both streams concurrently to avoid a full-buffer deadlock.
                Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                Task<string> errTask = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                stdout = outTask.Result;
                stderr = errTask.Result;
                exit = p.ExitCode;
            }

            string combined = (stdout ?? "") + (string.IsNullOrEmpty(stderr) ? "" : "\n--- stderr ---\n" + stderr);
            if (exit != 0)
                throw new BuildFailedException("[NativeCore] build.ps1 exited " + exit + " — the player build was stopped.\n" + combined);

            AssetDatabase.ImportAsset(DllRel, ImportAssetOptions.ForceUpdate);
            return combined;
        }

        private static string Tail(string text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
    }
}
