using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MemphisGameDev.Hardening.Editor
{
    /// <summary>
    /// Ship hygiene (#8): after a Standalone build, keep reverse-engineering aids out of what gets
    /// distributed.
    /// <list type="bullet">
    /// <item>FAILS the build if debug symbols (.pdb/.map/.dSYM/symbols) leak into the shipped payload (the
    /// exe folder and its _Data), which would hand a reverser function names and line tables.</item>
    /// <item>Handles the do-not-ship folders Unity writes INSIDE the build folder: IL2CPP's
    /// <c>*_BackUpThisFolder_ButDontShipItWithYourGame</c> (<c>GameAssembly.pdb</c> + the entire
    /// <c>il2cppOutput/</c> generated C++) and Burst's <c>*_BurstDebugInformation_DoNotShip</c>. By default a
    /// loud warning (devs keep them for crash symbolication); toggle <b>Tools ▸ Builds ▸ Delete IL2CPP Backup
    /// Folder After Build</b> to delete them. Distribute via <see cref="ShipPackager"/>, never by zipping the
    /// folder.</item>
    /// <item>Warns when managed stripping is below High (less IL/metadata is stripped, so more is recoverable).
    /// Raising it needs a link.xml and a runtime test, so this only advises.</item>
    /// </list>
    /// </summary>
    public class ShipHygieneBuildStep : IPostprocessBuildWithReport
    {
        public int callbackOrder => 2000; // after the integrity postprocessor (1000)

        private const string DeleteBackupPref = "ShopSim.ShipHygiene.DeleteBackup";
        private const string DeleteBackupMenu = "Tools/Builds/Delete IL2CPP Backup Folder After Build";

        // Symbol/debug artefacts that must never sit in the distributed game folder.
        private static readonly string[] SymbolExtensions = { ".pdb", ".map", ".dsym", ".sym" };
        private static readonly string[] SymbolNames = { "symbols.json" };

        internal static bool IsSymbolFile(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            return SymbolExtensions.Contains(Path.GetExtension(name)) || SymbolNames.Contains(name);
        }

        /// <summary>Unity's own do-not-ship marker: "ButDontShipItWithYourGame" (IL2CPP), "_DoNotShip" (Burst).</summary>
        internal static bool IsDoNotShip(string path) =>
            path.IndexOf("DontShip", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            path.IndexOf("DoNotShip", System.StringComparison.OrdinalIgnoreCase) >= 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.result != BuildResult.Succeeded) return;
            if (report.summary.platform != BuildTarget.StandaloneWindows64 &&
                report.summary.platform != BuildTarget.StandaloneWindows) return;

            string exePath = report.summary.outputPath;
            string exeDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;
            exeDir = Path.GetFullPath(exeDir); // outputPath may use '/'; the prefix checks below need '\'

            ShipPackager.RecordBuild(report);

            var doNotShipDirs = Directory.GetDirectories(exeDir).Where(d => IsDoNotShip(Path.GetFileName(d))).ToList();

            // 1. Symbols in the shipped payload (everything under exeDir except do-not-ship folders) fail the build.
            var leaks = new List<string>();
            foreach (string file in Directory.EnumerateFiles(exeDir, "*", SearchOption.AllDirectories))
            {
                if (doNotShipDirs.Any(d => file.StartsWith(d + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase))) continue;
                if (IsSymbolFile(file)) leaks.Add(file.Substring(exeDir.Length + 1));
            }
            if (leaks.Count > 0)
            {
                throw new BuildFailedException(
                    "[ShipHygiene] Debug symbols are in the shipped payload and would ship to players: " +
                    string.Join(", ", leaks) + ". Remove them (or exclude them from the build) before distributing.");
            }

            // 2. Do-not-ship folders (IL2CPP pdb + generated C++, Burst debug info): never distribute them.
            foreach (string dir in doNotShipDirs)
            {
                if (EditorPrefs.GetBool(DeleteBackupPref, false))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                        Debug.Log("[ShipHygiene] Deleted " + Path.GetFileName(dir) + " so it can't be shipped by mistake.");
                    }
                    catch (IOException e)
                    {
                        Debug.LogWarning("[ShipHygiene] Could not delete " + Path.GetFileName(dir) + ": " + e.Message);
                    }
                }
                else
                {
                    Debug.LogWarning("[ShipHygiene] Do NOT distribute '" + Path.GetFileName(dir) +
                        "' — it sits INSIDE the build folder and holds symbols/generated source (the IL2CPP one has " +
                        "GameAssembly.pdb and the full il2cppOutput C++). Don't zip the folder: use Tools ▸ Builds ▸ " +
                        "Package Build for Distribution, or enable Delete IL2CPP Backup Folder After Build.");
                }
            }

            // 3. Stripping advisory (never forced: High needs a link.xml and a runtime test on this project).
            var level = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone);
            if (level != ManagedStrippingLevel.High)
            {
                Debug.LogWarning("[ShipHygiene] Managed stripping is " + level + "; at High, less IL and metadata " +
                    "ships (harder to reverse). Raising it needs a link.xml to preserve reflection-used types " +
                    "(Obfuz, Netcode, Newtonsoft, Vivox) and a runtime test of the built player first.");
            }
        }

        [MenuItem(DeleteBackupMenu, false, 120)]
        private static void ToggleDeleteBackup() =>
            EditorPrefs.SetBool(DeleteBackupPref, !EditorPrefs.GetBool(DeleteBackupPref, false));

        [MenuItem(DeleteBackupMenu, true)]
        private static bool ToggleDeleteBackupValidate()
        {
            Menu.SetChecked(DeleteBackupMenu, EditorPrefs.GetBool(DeleteBackupPref, false));
            return true;
        }
    }
}
