using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MemphisGameDev.Hardening.Editor
{
    /// <summary>
    /// After a Standalone build, computes FNV-1a-64 of the shipped binaries (GameAssembly.dll +
    /// shopsim_core.dll), writes a local manifest, and — unless in LocalOnly mode — publishes the
    /// expected digests to Remote Config or Cloud Save for the Cloud Code validator to read.
    /// Never fails the build.
    /// </summary>
    public class IntegrityBuildPostprocessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000; // run late, after other post-build steps

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.result != BuildResult.Succeeded) return;
            if (report.summary.platform != BuildTarget.StandaloneWindows64 &&
                report.summary.platform != BuildTarget.StandaloneWindows) return;

            Dictionary<string, string> targets = IntegrityHashing.ResolveBuildTargets(report);
            if (targets.Count == 0)
            {
                Debug.LogWarning("[Integrity] No hashable binaries found in the build (IL2CPP produces " +
                                 "GameAssembly.dll; Mono builds have none). Skipping.");
                return;
            }
            if (!targets.ContainsKey(HardeningSettings.Current.gameAssemblyName))
                Debug.LogWarning("[Integrity] GameAssembly.dll not found — is this a Mono build? Only the native core will be hashed.");

            string buildId = PlayerSettings.bundleVersion;
            IntegrityManifest manifest = IntegrityHashing.BuildManifest(buildId, targets);

            var settings = IntegrityUploadSettings.Find();
            bool writeLocal = settings == null || settings.writeLocalManifest;
            if (writeLocal)
            {
                string outDir = Path.GetDirectoryName(report.summary.outputPath);
                string file = Path.Combine(outDir, $"integrity-manifest-{buildId}.json");
                File.WriteAllText(file, manifest.ToJson());
                Debug.Log($"[Integrity] Wrote {file}\n{manifest.ToJson()}");
            }

            if (settings == null)
            {
                Debug.Log("[Integrity] No IntegrityUploadSettings asset found — local manifest only. " +
                          "Create one via Assets ▸ Create ▸ Hardening ▸ Integrity Upload Settings to enable upload.");
                return;
            }
            if (settings.mode == IntegrityStoreMode.LocalOnly) return;

            if (settings.mode == IntegrityStoreMode.CSharpModule)
            {
                IntegrityModuleWriter.Write(settings, manifest);
                IntegrityModuleDeployer.Deploy(settings); // no-op unless settings.autoDeployModule is on
            }
            else
                IntegrityUploader.Publish(settings, manifest);
        }

        // --- Convenience: hash an arbitrary file to eyeball a digest (matches the client/server). ---
        [MenuItem("Tools/Builds/Integrity/Hash A File…")]
        private static void HashAFile()
        {
            string path = EditorUtility.OpenFilePanel("Pick a binary to hash (FNV-1a-64)", "", "dll,exe,*");
            if (string.IsNullOrEmpty(path)) return;
            ulong h = IntegrityHashing.HashFile(path);
            Debug.Log($"[Integrity] FNV-1a-64({Path.GetFileName(path)}) = {h}  (0x{h:x})");
            EditorGUIUtility.systemCopyBuffer = h.ToString();
        }
    }
}
