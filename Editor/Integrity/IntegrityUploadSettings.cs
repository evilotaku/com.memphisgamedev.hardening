using System;
using UnityEditor;
using UnityEngine;

namespace MemphisGameDev.Hardening.Editor
{
    public enum IntegrityStoreMode
    {
        LocalOnly,    // write the manifest file next to the build, upload nothing
        RemoteConfig, // upsert the expected-digest blob into a Remote Config key
        CloudSave,    // store it under a service-owned Cloud Save key
        CSharpModule  // bake the digests into the Cloud Code C# module source (ExpectedDigests.cs)
    }

    /// <summary>
    /// Build-time integrity upload settings. Non-secret only: the service-account credentials are read
    /// from environment variables at build time (UGS_SERVICE_KEY_ID / UGS_SERVICE_KEY_SECRET) so no
    /// secret ever lands in the asset or the repo. Create via Assets ▸ Create ▸ Hardening ▸ Integrity
    /// Upload Settings and keep a single instance anywhere under the project.
    /// </summary>
    [CreateAssetMenu(fileName = "IntegrityUploadSettings", menuName = "Hardening/Integrity Upload Settings")]
    public class IntegrityUploadSettings : ScriptableObject
    {
        [Tooltip("What to do with the computed digests after a build.")]
        public IntegrityStoreMode mode = IntegrityStoreMode.LocalOnly;

        [Tooltip("UGS environment name to publish to (e.g. 'production'). Resolved to an id at upload time.")]
        public string environmentName = "production";

        [Tooltip("Remote Config key that holds the expected-digest JSON blob (RemoteConfig mode).")]
        public string remoteConfigKey = "integrity_expected";

        [Tooltip("Cloud Save key that holds the expected-digest JSON blob (CloudSave mode).")]
        public string cloudSaveKey = "integrity_expected";

        [Tooltip("Also write a local manifest JSON next to the build output.")]
        public bool writeLocalManifest = true;

        [Tooltip("CSharpModule mode: after baking the digests, auto-publish the IntegrityModule via the " +
                 "`ugs` CLI (ugs deploy) at the end of the build. Headless — it does NOT use the Editor's " +
                 "interactive UGS session. Deploys to THIS project (the Editor's cloud link) and the " +
                 "'environmentName' above, both passed explicitly. Needs either a prior `ugs login` or the " +
                 "service-account env vars below. Never fails the build. Default off.")]
        public bool autoDeployModule = false;

        [Tooltip("CSharpModule mode: the module's generated ExpectedDigests.cs (regenerated each build), " +
                 "relative to the project root.")]
        public string moduleExpectedDigestsFile =
            "Assets/CloudCode/IntegrityModule/Cloud/ExpectedDigests.cs";

        [Tooltip("CSharpModule mode: the C# namespace of the module (must match ExpectedDigests.cs).")]
        public string moduleNamespace = "IntegrityModule.Cloud";

        [Tooltip("Env var names for the UGS service-account key (never store the secret in this asset).")]
        public string keyIdEnvVar = "UGS_SERVICE_KEY_ID";
        public string keySecretEnvVar = "UGS_SERVICE_KEY_SECRET";

        public string ResolveKeyId() => Environment.GetEnvironmentVariable(keyIdEnvVar);
        public string ResolveKeySecret() => Environment.GetEnvironmentVariable(keySecretEnvVar);

        /// <summary>Finds the single settings asset in the project, or null.</summary>
        public static IntegrityUploadSettings Find()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:IntegrityUploadSettings"))
            {
                var s = AssetDatabase.LoadAssetAtPath<IntegrityUploadSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (s != null) return s;
            }
            return null;
        }
    }
}
