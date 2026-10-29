namespace IntegrityModule.Cloud
{
    /// <summary>
    /// Holds the ECDSA (P-256) private key the module signs LAN licences with. The key material lives
    /// in a generated partial written from an environment variable, never committed. Split into a
    /// <c>partial</c> so the module compiles before the key exists; until then <see cref="Value"/> is
    /// empty and signing should refuse. Only the matching PUBLIC key is compiled into the native core, which
    /// verifies licences — the private key stays on the server.
    /// </summary>
    public static partial class LicenceKey
    {
        /// <summary>The base64 private key (e.g. X||Y||D), or empty before the generated partial exists.</summary>
        public static string Value()
        {
            string key = "";
            Load(ref key);
            return key ?? "";
        }

        static partial void Load(ref string key);
    }
}
