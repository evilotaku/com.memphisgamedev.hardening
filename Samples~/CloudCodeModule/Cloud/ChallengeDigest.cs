using System;
using System.Text;

namespace IntegrityModule.Cloud
{
    /// <summary>
    /// Server-side digest helpers that mirror the client's native core. The digest is the documented
    /// FNV-1a-64 over the bytes, folded with the challenge salt — so the server can recompute the
    /// value a clean client would have reported and compare. (Nothing here is secret; the forgery
    /// resistance is that the attacker would need the real binary to hash to the expected value.)
    /// </summary>
    public static class ChallengeDigest
    {
        /// <summary>FNV-1a-64 over <paramref name="data"/>, folded with <paramref name="salt"/>.</summary>
        public static ulong Of(byte[] data, byte[] salt)
        {
            ulong h = 1469598103934665603UL;
            if (data != null) foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
            if (salt != null) foreach (byte s in salt) { h ^= s; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>The salt inside a challenge ("c1|salt|playerId|issued" before the '.'), or null.</summary>
        public static byte[] Salt(string challenge)
        {
            try
            {
                int dot = challenge.IndexOf('.');
                if (dot <= 0) return null;
                string[] f = Encoding.UTF8.GetString(FromBase64Url(challenge.Substring(0, dot))).Split('|');
                if (f.Length != 4 || f[0] != "c1") return null;
                byte[] salt = FromBase64Url(f[1]);
                return salt.Length > 0 && salt.Length <= 64 ? salt : null;
            }
            catch (FormatException) { return null; }
        }

        private static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
    }
}
