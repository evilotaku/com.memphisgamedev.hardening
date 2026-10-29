using System.Collections.Generic;

namespace MemphisGameDev.Hardening
{
    /// <summary>
    /// The runtime counterpart to the Editor-only <c>HardeningSettings</c> asset: the shipped binary names and the
    /// in-player subpaths <see cref="IntegrityGuard"/> needs to find and hash the binaries in a built player, where
    /// the settings asset isn't available. The defaults match the asset's defaults, so the guard works with no setup
    /// at all; a game whose binaries are named or laid out differently registers its own values through
    /// <see cref="Current"/>. The package's Editor side generates that registration from the settings asset, so the
    /// asset stays the single source of truth for both the build steps and the runtime.
    /// </summary>
    public sealed class HardeningRuntimeConfig
    {
        /// <summary>The IL2CPP managed code, next to the executable (e.g. <c>GameAssembly.dll</c>).</summary>
        public string gameAssemblyName = "GameAssembly.dll";

        /// <summary>The obfuscated native core plug-in (e.g. <c>shopsim_core.dll</c>).</summary>
        public string nativeCoreName = "shopsim_core.dll";

        /// <summary>The IL2CPP metadata blob (e.g. <c>global-metadata.dat</c>).</summary>
        public string il2cppMetadataName = "global-metadata.dat";

        /// <summary>Folder holding the native core plug-in, relative to <c>&lt;Product&gt;_Data</c>.</summary>
        public string nativePluginSubpath = "Plugins/x86_64";

        /// <summary>Folder holding the IL2CPP metadata, relative to <c>&lt;Product&gt;_Data</c>.</summary>
        public string il2cppMetadataSubpath = "il2cpp_data/Metadata";

        /// <summary>The three whole-file digest targets the server always expects, in a stable order.</summary>
        public IReadOnlyList<string> BuiltInTargets => new[] { gameAssemblyName, nativeCoreName, il2cppMetadataName };

        /// <summary>Modules whose in-memory <c>.text</c> is also hashed (the native core and the IL2CPP code).</summary>
        public IReadOnlyList<string> TextTargets => new[] { nativeCoreName, gameAssemblyName };

        /// <summary>The config the runtime uses. Defaults match the asset's defaults; the game overrides it at startup.</summary>
        public static HardeningRuntimeConfig Current { get; set; } = new HardeningRuntimeConfig();
    }
}
