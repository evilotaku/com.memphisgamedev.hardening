namespace MarketShopandRetailSystem.Multiplayer
{
    /// <summary>
    /// The native-core operations the hardening runtime (<see cref="IntegrityGuard"/>, <see cref="LanLicence"/>) needs:
    /// whole-file and challenge-salted digests, in-memory code (".text") digests, the tamper bitmask, the device id and
    /// licence verification. Letting the hardening code depend on this seam instead of the concrete native wrapper is
    /// what lets it move into a reusable package: the game registers its own implementation (here, one forwarding to the
    /// obfuscated <c>shopsim_core</c> wrapper) with <see cref="NativeCore"/>.
    /// </summary>
    public interface INativeCore
    {
        /// <summary>FNV-1a-64 over the first <paramref name="length"/> bytes of <paramref name="data"/>.</summary>
        ulong HashRegion(byte[] data, int length);

        /// <summary>Digest answering a server challenge: <see cref="HashRegion"/> mixed with the challenge <paramref name="salt"/>.</summary>
        ulong ChallengeDigest(byte[] data, int length, byte[] salt);

        /// <summary>In-memory ".text" digest of a loaded module answering <paramref name="salt"/>; false if it isn't loaded.</summary>
        bool TextDigest(string moduleName, byte[] salt, out ulong digest);

        /// <summary>The runtime tamper-scan bitmask (0 = clean); absent in the Editor, where the native core isn't loaded.</summary>
        int TamperScan();

        /// <summary>A stable per-device id for binding a LAN licence, or null if the device can't be identified.</summary>
        string DeviceId();

        /// <summary>Verifies a LAN licence for this build; 0 when valid. Returns the licensed player id and expiry.</summary>
        int LicenceCheck(string token, string buildId, bool bindDevice, out string playerId, out long expires);
    }

    /// <summary>
    /// Holds the one <see cref="INativeCore"/> the game registered. The game sets <see cref="Current"/> at startup (see
    /// <c>ShopSimCoreNative</c>) before any integrity or licence check runs.
    /// </summary>
    public static class NativeCore
    {
        /// <summary>The game's native-core implementation, or null before it registers one.</summary>
        public static INativeCore Current { get; set; }
    }
}
