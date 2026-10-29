using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;
using RuntimePlatform = UnityEngine.RuntimePlatform;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// CSharpModule mode, opt-in: publishes the IntegrityModule to UGS HEADLESS via the `ugs` CLI, so it
    /// works with the service account (never the Editor's interactive session, which throws a
    /// token-exchange/403 in batch or when signed out).
    ///
    /// The CLI can only deploy a COMPILED module (.ccm), and the in-project .ccmu's .ccm is built by the
    /// Cloud Code package's internal zip step — which runs only during an Editor deploy and is NOT refreshed
    /// by a normal recompile. So we reproduce that step: copy the freshly compiled module assembly
    /// (Library/ScriptAssemblies/&lt;asmdef&gt;.dll) over the one in the package's staging folder
    /// (Library/CloudModules/&lt;asmdef&gt;/) — reusing the known-good dependency set the package assembled —
    /// re-zip it to the .ccm, then `ugs deploy &lt;.ccm&gt;`.
    ///
    /// Caveat (by design, per the chosen approach): this reuses the dependency DLLs from the last Editor
    /// deploy. Digest-only rebuilds are safe; if the module gains/loses assembly references, deploy once from
    /// the Deployment window to refresh the staging set. If the staging folder is missing (fresh clone), we
    /// log that and skip.
    ///
    /// Because the digest recompile finishes AFTER the post-build callback, <see cref="Deploy"/> only QUEUES
    /// the deploy; it actually runs on the next assembly reload once compilation has settled, so the .ccm is
    /// built from the up-to-date module DLL. Never fails the build; all errors are logged and swallowed.
    /// Auth: a prior `ugs login`, or the service-account env vars named on the settings asset (bridged to the
    /// CLI's UGS_CLI_SERVICE_KEY_ID / UGS_CLI_SERVICE_SECRET_KEY for the child process only).
    /// </summary>
    [InitializeOnLoad]
    public static class IntegrityModuleDeployer
    {
        const string PendingKey = "ShopSim.Integrity.DeployPending"; // SessionState: survives domain reloads, clears on Editor restart

        static IntegrityModuleDeployer()
        {
            AssemblyReloadEvents.afterAssemblyReload += RunQueuedDeployIfReady;
            EditorApplication.delayCall += RunQueuedDeployIfReady;
        }

        /// <summary>Post-build entry: queues a deploy to run once the module recompiles.</summary>
        public static void Deploy(IntegrityUploadSettings settings)
        {
            if (settings == null || !settings.autoDeployModule || settings.mode != IntegrityStoreMode.CSharpModule)
                return;
            SessionState.SetBool(PendingKey, true);
            Debug.Log("[Integrity] Auto-deploy queued — will publish the module after it recompiles with the new digests.");
        }

        [MenuItem("Tools/Builds/Integrity/Deploy Module Now")]
        private static void DeployNow()
        {
            var s = IntegrityUploadSettings.Find();
            if (s == null) { Debug.LogWarning("[Integrity] No IntegrityUploadSettings asset found."); return; }
            if (s.mode != IntegrityStoreMode.CSharpModule) { Debug.LogWarning("[Integrity] Settings mode is not CSharpModule; nothing to deploy."); return; }
            DoDeploy(s);
        }

        static void RunQueuedDeployIfReady()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return; // wait for the recompile to settle
            SessionState.SetBool(PendingKey, false); // one-shot: don't loop on later reloads
            var s = IntegrityUploadSettings.Find();
            if (s != null && s.autoDeployModule && s.mode == IntegrityStoreMode.CSharpModule)
                DoDeploy(s);
        }

        static void DoDeploy(IntegrityUploadSettings settings)
        {
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;

                string asmName = ResolveModuleAssemblyName(projectRoot, settings);
                if (asmName == null) return; // already logged

                // Re-zip the .ccm from the freshly compiled module DLL + the package's staged dependency set.
                string ccmRel = Path.Combine("Library", "CloudModules", asmName + ".ccm").Replace('\\', '/');
                if (!ReZipModule(projectRoot, asmName, ccmRel)) return; // already logged

                string projectId = CloudProjectSettings.projectId;
                if (string.IsNullOrEmpty(projectId))
                {
                    Debug.LogWarning("[Integrity] Deploy: project isn't linked to a Unity cloud project (no projectId). " +
                                     "Link it in Project Settings ▸ Services. Skipping.");
                    return;
                }
                string env = settings.environmentName;
                if (string.IsNullOrEmpty(env))
                {
                    Debug.LogWarning("[Integrity] Deploy: environmentName is empty on the settings asset. Skipping.");
                    return;
                }
                string ugs = ResolveUgs();
                if (ugs == null)
                {
                    Debug.LogWarning("[Integrity] Deploy: the `ugs` CLI was not found. Install it " +
                                     "(npm i -g @unity-services/cli) or set UGS_CLI_PATH. The .ccm is rebuilt at " +
                                     $"{ccmRel}; deploy it manually. Skipping.");
                    return;
                }

                var psi = BuildStartInfo(ugs, ccmRel, projectId, env, projectRoot);
                string keyId = settings.ResolveKeyId();
                string secret = settings.ResolveKeySecret();
                if (!string.IsNullOrEmpty(keyId) && !string.IsNullOrEmpty(secret))
                {
                    // CLI env-var names per `ugs login` help: id = UGS_CLI_SERVICE_KEY_ID, secret = UGS_CLI_SERVICE_SECRET_KEY.
                    psi.EnvironmentVariables["UGS_CLI_SERVICE_KEY_ID"] = keyId;
                    psi.EnvironmentVariables["UGS_CLI_SERVICE_SECRET_KEY"] = secret;
                }

                Debug.Log($"[Integrity] Deploy: publishing module '{asmName}' to project {projectId}, env '{env}'…");
                var (code, output) = Exec(psi, 300_000);
                if (code == 0)
                    Debug.Log("[Integrity] Deploy: module published.\n" + output);
                else
                    Debug.LogWarning($"[Integrity] Deploy FAILED (exit {code}). The build is fine — redeploy manually " +
                                     "(Services ▸ Deployment) or fix the cause below.\n" + output);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Integrity] Deploy threw (build unaffected): " + e);
            }
        }

        /// <summary>Reads the module's cloud asmdef name via the .ccmu (cloudAssemblyDefinitionGuid → asmdef "name").</summary>
        static string ResolveModuleAssemblyName(string projectRoot, IntegrityUploadSettings settings)
        {
            string cloudDir = Path.GetDirectoryName(Path.Combine(projectRoot, settings.moduleExpectedDigestsFile));
            string moduleDir = cloudDir == null ? null : Path.GetDirectoryName(cloudDir);
            if (moduleDir == null || !Directory.Exists(moduleDir))
            {
                Debug.LogWarning($"[Integrity] Deploy: module folder not found (from {settings.moduleExpectedDigestsFile}). Skipping.");
                return null;
            }
            string ccmu = Directory.GetFiles(moduleDir, "*.ccmu").FirstOrDefault();
            if (ccmu == null)
            {
                Debug.LogWarning($"[Integrity] Deploy: no .ccmu found in {moduleDir}. Skipping.");
                return null;
            }
            try
            {
                string asmGuid = (string)JObject.Parse(File.ReadAllText(ccmu))["cloudAssemblyDefinitionGuid"];
                string asmdefPath = AssetDatabase.GUIDToAssetPath(asmGuid);
                if (string.IsNullOrEmpty(asmdefPath))
                {
                    Debug.LogWarning("[Integrity] Deploy: couldn't resolve the cloud asmdef from the .ccmu. Skipping.");
                    return null;
                }
                string name = (string)JObject.Parse(File.ReadAllText(asmdefPath))["name"];
                if (string.IsNullOrEmpty(name))
                {
                    Debug.LogWarning("[Integrity] Deploy: cloud asmdef has no name. Skipping.");
                    return null;
                }
                return name;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Integrity] Deploy: failed reading the module asmdef: " + e.Message);
                return null;
            }
        }

        /// <summary>Copies the freshly compiled module DLL into the staging folder and re-zips the .ccm.</summary>
        static bool ReZipModule(string projectRoot, string asmName, string ccmRel)
        {
            string stagingDir = Path.Combine(projectRoot, "Library", "CloudModules", asmName);
            string compiledDll = Path.Combine(projectRoot, "Library", "ScriptAssemblies", asmName + ".dll");
            string ccmPath = Path.Combine(projectRoot, ccmRel);

            if (!Directory.Exists(stagingDir))
            {
                Debug.LogWarning($"[Integrity] Deploy: staging folder {stagingDir} is missing (the dependency set is " +
                                 "assembled by an Editor deploy). Deploy once via Services ▸ Deployment, then auto-deploy works. Skipping.");
                return false;
            }
            if (!File.Exists(compiledDll))
            {
                Debug.LogWarning($"[Integrity] Deploy: compiled module {compiledDll} not found (compilation may have failed). Skipping.");
                return false;
            }
            try
            {
                File.Copy(compiledDll, Path.Combine(stagingDir, asmName + ".dll"), overwrite: true);
                if (File.Exists(ccmPath)) File.Delete(ccmPath);
                ZipFile.CreateFromDirectory(stagingDir, ccmPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                Debug.Log($"[Integrity] Deploy: rebuilt {ccmRel} with the fresh module DLL ({new FileInfo(ccmPath).Length} bytes).");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Integrity] Deploy: failed to re-zip the .ccm: " + e.Message);
                return false;
            }
        }

        static string ResolveUgs()
        {
            string overridePath = Environment.GetEnvironmentVariable("UGS_CLI_PATH");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

            bool win = Application.platform == RuntimePlatform.WindowsEditor;
            string[] names = win ? new[] { "ugs.cmd", "ugs.exe", "ugs" } : new[] { "ugs" };

            if (win)
            {
                string appdata = Environment.GetEnvironmentVariable("APPDATA");
                if (!string.IsNullOrEmpty(appdata))
                    foreach (var n in names)
                    {
                        string p = Path.Combine(appdata, "npm", n);
                        if (File.Exists(p)) return p;
                    }
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var n in names)
                {
                    try { string p = Path.Combine(dir.Trim(), n); if (File.Exists(p)) return p; }
                    catch { /* malformed PATH entry */ }
                }
            }
            return null;
        }

        static ProcessStartInfo BuildStartInfo(string ugs, string ccmRel, string projectId, string env, string cwd)
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = cwd, // so the relative, space-free ccmRel resolves without quoting the cwd's spaces
            };
            // Service token for C# modules is "cloud-code-modules". Never --reconcile (would delete other
            // Cloud Code content). ccmRel is relative and space-free, so no inner quoting is needed.
            string args = $"deploy {ccmRel} -p {projectId} -e {env} -s cloud-code-modules";
            bool isCmd = ugs.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                         ugs.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            if (isCmd)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/d /s /c \"{ugs} {args}\"";
            }
            else
            {
                psi.FileName = ugs;
                psi.Arguments = args;
            }
            return psi;
        }

        static (int code, string output) Exec(ProcessStartInfo psi, int timeoutMs)
        {
            using (var p = new Process { StartInfo = psi })
            {
                var sb = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { /* already gone */ }
                    return (-1, sb.ToString().Trim() + $"\n[timed out after {timeoutMs / 1000}s]");
                }
                p.WaitForExit();
                return (p.ExitCode, sb.ToString().Trim());
            }
        }
    }
}
