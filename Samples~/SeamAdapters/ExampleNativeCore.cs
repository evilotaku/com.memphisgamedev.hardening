// Example INativeCore — a template for the native-core seam. It P/Invokes a native plug-in
// (here a placeholder "hardening_core" with "hc_" exports; see the NativeCore sample for the
// matching C++ ABI) and falls back to a managed implementation when the plug-in isn't present,
// so it also works in the Editor and in builds without the DLL.
//
// This is a generic example, not a shipping core: the managed path does no tamper resistance, and
// the native path is only as strong as the core you build behind it. Replace the exports and the
// DLL name with your own, and obfuscate the native build.

using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using MemphisGameDev.Hardening; // INativeCore, NativeCore (the package Runtime assembly)

namespace HardeningExample
{
    public sealed class ExampleNativeCore : INativeCore
    {
        private const string Lib = "hardening_core";

        // Return 0 on success; the managed fallback is used if the entry point is missing.
        [DllImport(Lib, EntryPoint = "hc_hash_region", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hc_hash_region(byte[] data, int len, ref ulong outHash);
        [DllImport(Lib, EntryPoint = "hc_challenge_digest", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hc_challenge_digest(byte[] data, int len, byte[] salt, int saltLen, ref ulong outHash);
        [DllImport(Lib, EntryPoint = "hc_text_digest", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hc_text_digest(byte[] moduleName, int nameLen, byte[] salt, int saltLen, ref ulong outHash);
        [DllImport(Lib, EntryPoint = "hc_tamper_scan", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hc_tamper_scan(ref uint outFlags);
        [DllImport(Lib, EntryPoint = "hc_device_id", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hc_device_id(byte[] output, int capacity);
        [DllImport(Lib, EntryPoint = "hc_licence_check", CallingConvention = CallingConvention.Cdecl)]
        private static extern int hc_licence_check(byte[] token, int tokenLen, byte[] buildId, int buildLen,
                                                   int bindDevice, byte[] playerOut, int playerCap, ref long expires);

        private static bool? _native;
        private static bool Native
        {
            get
            {
                if (_native == null)
                {
                    try { ulong h = 0; hc_hash_region(Array.Empty<byte>(), 0, ref h); _native = true; }
                    catch (DllNotFoundException) { _native = false; }
                    catch (EntryPointNotFoundException) { _native = false; }
                }
                return _native.Value;
            }
        }

        public ulong HashRegion(byte[] data, int length)
        {
            if (Native) { ulong h = 0; return hc_hash_region(data, length, ref h) == 0 ? h : 0UL; }
            return Fnv(data, length, null);
        }

        public ulong ChallengeDigest(byte[] data, int length, byte[] salt)
        {
            if (Native) { ulong h = 0; return hc_challenge_digest(data, length, salt, salt?.Length ?? 0, ref h) == 0 ? h : 0UL; }
            return Fnv(data, length, salt);
        }

        public bool TextDigest(string moduleName, byte[] salt, out ulong digest)
        {
            digest = 0;
            if (!Native) return false; // a managed process can't read another module's in-memory code
            byte[] name = Encoding.UTF8.GetBytes(moduleName ?? "");
            return hc_text_digest(name, name.Length, salt, salt?.Length ?? 0, ref digest) == 0;
        }

        public int TamperScan()
        {
            if (!Native) return 0; // managed fallback has no tamper scan; 0 = clean
            uint flags = 0;
            return hc_tamper_scan(ref flags) == 0 ? (int)flags : 0;
        }

        public string DeviceId()
        {
            if (Native)
            {
                var buf = new byte[128];
                int n = hc_device_id(buf, buf.Length);
                return n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : null;
            }
            return SystemInfo.deviceUniqueIdentifier;
        }

        public int LicenceCheck(string token, string buildId, bool bindDevice, out string playerId, out long expires)
        {
            playerId = "";
            expires = 0;
            if (!Native) return 0; // managed fallback treats licences as valid (Editor/dev)
            byte[] t = Encoding.UTF8.GetBytes(token ?? ""), b = Encoding.UTF8.GetBytes(buildId ?? "");
            var player = new byte[128];
            int code = hc_licence_check(t, t.Length, b, b.Length, bindDevice ? 1 : 0, player, player.Length, ref expires);
            if (code == 0) playerId = Encoding.UTF8.GetString(player).TrimEnd('\0');
            return code;
        }

        // FNV-1a-64, optionally folded with a challenge salt — the documented managed stand-in.
        private static ulong Fnv(byte[] data, int length, byte[] salt)
        {
            ulong h = 1469598103934665603UL;
            int n = Mathf.Clamp(length, 0, data?.Length ?? 0);
            for (int i = 0; i < n; i++) { h ^= data[i]; h *= 1099511628211UL; }
            if (salt != null) foreach (byte s in salt) { h ^= s; h *= 1099511628211UL; }
            return h;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => NativeCore.Current = new ExampleNativeCore();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterInEditor() => NativeCore.Current = new ExampleNativeCore();
#endif
    }
}
