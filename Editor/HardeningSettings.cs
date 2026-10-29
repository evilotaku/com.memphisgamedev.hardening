using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// Per-project configuration for the build-hardening pipeline: the names and locations of the binaries the
    /// integrity system hashes and the packager checks. Centralising them here is the first step toward lifting the
    /// hardening scripts into a reusable package — the scripts read these instead of hard-coding "shopsim_core.dll"
    /// and the IL2CPP layout, so another game only has to change the asset.
    /// <para>There is normally one asset (create it from Assets ▸ Create ▸ Hardening ▸ Settings, or it is made on
    /// first use). If none exists, <see cref="Current"/> returns an unsaved instance with these defaults, which are
    /// the values the pipeline used before, so builds are unaffected until the asset is customised.</para>
    /// <para>Not yet routed through here (kept as before this step): the <c>SHOPSIM_*</c> environment-variable names,
    /// the watermark, the native build step, the Cloud Code deployer, the build-mode defines and the menu paths.
    /// Those move in the next increment.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Hardening/Settings", fileName = "HardeningSettings")]
    public class HardeningSettings : ScriptableObject
    {
        [Header("Shipped binaries the integrity system covers")]
        [Tooltip("The IL2CPP managed code, next to the executable on a Windows build.")]
        public string gameAssemblyName = "GameAssembly.dll";

        [Tooltip("The obfuscated native core plug-in.")]
        public string nativeCoreName = "shopsim_core.dll";

        [Tooltip("The IL2CPP metadata (names, strings, array data).")]
        public string il2cppMetadataName = "global-metadata.dat";

        [Header("Where they sit under <Product>_Data (forward-slash separated)")]
        [Tooltip("Folder holding the native core plug-in, relative to <Product>_Data.")]
        public string nativePluginSubpath = "Plugins/x86_64";

        [Tooltip("Folder holding the IL2CPP metadata, relative to <Product>_Data.")]
        public string il2cppMetadataSubpath = "il2cpp_data/Metadata";

        [Tooltip("The Mono managed assembly (plain-IL builds), also a watermark carrier.")]
        public string monoAssemblyName = "Assembly-CSharp.dll";

        [Header("Native core source (relative to the project root)")]
        [Tooltip("Folder holding the native core's source and build script, relative to the project root.")]
        public string nativeSourceDir = "native/shopsim_core";

        [Header("Environment variables (names only; values are never stored here)")]
        [Tooltip("Holds the per-build watermark HMAC secret.")]
        public string watermarkSecretVar = "SHOPSIM_WATERMARK_SECRET";

        [Tooltip("Holds the LAN licence signing key (base64 P-256 X||Y||D).")]
        public string licenceKeyVar = "SHOPSIM_LICENCE_KEY";

        [Header("Generated sources")]
        [Tooltip("The expected-digests C# the packager reads and the server validates against.")]
        public string expectedDigestsPath = "Assets/CloudCode/IntegrityModule/Cloud/ExpectedDigests.cs";

        [Tooltip("The generated per-build watermark marker C#, written into the game's Assets.")]
        public string watermarkGeneratedPath = "Assets/Scripts/Multiplayer/Core/BuildWatermark.Generated.cs";

        [Tooltip("The log of watermarked releases (version<TAB>first verified), in the game's Assets.")]
        public string watermarkReleasesPath = "Assets/Scripts/Multiplayer/Editor/Watermark/WatermarkReleases.txt";

        [Tooltip("Generated C# (in the game's runtime assembly) that registers these binary names/subpaths into HardeningRuntimeConfig for the built player.")]
        public string runtimeConfigGeneratedPath = "Assets/Scripts/Multiplayer/Core/HardeningRuntimeConfig.Generated.cs";

        /// <summary>The three whole-file digest targets, in a stable order.</summary>
        public IReadOnlyList<string> DigestTargets => new[] { gameAssemblyName, nativeCoreName, il2cppMetadataName };

        /// <summary>Absolute path to the native core inside a completed build's <c>&lt;Product&gt;_Data</c>.</summary>
        public string NativeCorePath(string dataDir) => Under(dataDir, nativePluginSubpath, nativeCoreName);

        /// <summary>Absolute path to the IL2CPP metadata inside a completed build's <c>&lt;Product&gt;_Data</c>.</summary>
        public string MetadataPath(string dataDir) => Under(dataDir, il2cppMetadataSubpath, il2cppMetadataName);

        /// <summary>The project root (the folder that holds <c>Assets</c>).</summary>
        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        /// <summary>Absolute path to the native core source folder.</summary>
        public string NativeSourceDir
        {
            get
            {
                var parts = new List<string> { ProjectRoot };
                parts.AddRange(nativeSourceDir.Split('/', '\\'));
                return Path.Combine(parts.ToArray());
            }
        }

        /// <summary>Absolute path to a file inside the native core source folder.</summary>
        public string NativeSourcePath(string file) => Path.Combine(NativeSourceDir, file);

        /// <summary>The native core plug-in's asset path in the project, e.g. <c>Assets/Plugins/x86_64/…</c>.</summary>
        public string NativePluginAssetPath => "Assets/" + nativePluginSubpath.Replace('\\', '/') + "/" + nativeCoreName;

        private static string Under(string dataDir, string subpath, string file)
        {
            var parts = new List<string> { dataDir };
            parts.AddRange(subpath.Split('/', '\\'));
            parts.Add(file);
            return Path.Combine(parts.ToArray());
        }

        private static HardeningSettings cached;

        /// <summary>The project's settings asset, or an unsaved defaults instance if none has been created.</summary>
        public static HardeningSettings Current
        {
            get
            {
                if (cached != null) return cached;
                string guid = AssetDatabase.FindAssets("t:HardeningSettings").FirstOrDefault();
                if (!string.IsNullOrEmpty(guid))
                    cached = AssetDatabase.LoadAssetAtPath<HardeningSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (cached == null)
                {
                    cached = CreateInstance<HardeningSettings>();
                    cached.hideFlags = HideFlags.DontSave;
                }
                return cached;
            }
        }
    }
}
