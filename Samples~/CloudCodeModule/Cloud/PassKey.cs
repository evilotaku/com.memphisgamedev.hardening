namespace IntegrityModule.Cloud
{
    /// <summary>
    /// Holds the HMAC secret the module signs integrity passes with. The actual bytes live in a
    /// generated partial (e.g. PassKey.Generated.cs) written from an environment variable, so no
    /// secret is committed. Split into a <c>partial</c> so the module still compiles before the key
    /// exists; until then <see cref="Secret"/> is empty and signing should refuse.
    /// </summary>
    public static partial class PassKey
    {
        public static string Secret()
        {
            string secret = "";
            Load(ref secret);
            return secret ?? "";
        }

        // Implemented by the generated partial; no-op (empty secret) when the key file is absent.
        static partial void Load(ref string secret);
    }
}
