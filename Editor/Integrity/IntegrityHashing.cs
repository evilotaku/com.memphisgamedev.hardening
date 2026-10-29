using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// FNV-1a-64 over whole files, identical to the native core (ssc_hash_region), the runtime
    /// fallback (ShopSimCore.HashRegion) and the Cloud Code validator. Shared by the build
    /// postprocessor so expected digests are generated automatically from the shipped binaries.
    /// </summary>
    public static class IntegrityHashing
    {
        public const ulong FnvOffsetBasis = 14695981039346656037UL; // 0xcbf29ce484222325
        public const ulong FnvPrime = 1099511628211UL;              // 0x100000001b3

        public static ulong HashFile(string path) => HashBytes(File.ReadAllBytes(path));

        public static ulong HashBytes(byte[] data)
        {
            ulong h = FnvOffsetBasis;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= FnvPrime;
            }
            return h;
        }

        /// <summary>
        /// The per-chunk digests the server answers challenges from: "length:base64(8-byte little-endian FNV-1a-64
        /// of each 16 KB chunk)". Must match ChallengeDigest (Cloud Code) and ssc_challenge_digest.
        /// </summary>
        public static string ChunkTable(string path) => ChunkTableOfBytes(File.ReadAllBytes(path));

        public static string ChunkTableOfBytes(byte[] data)
        {
            const int chunk = 16384;
            int count = (data.Length + chunk - 1) / chunk;
            var table = new byte[count * 8];
            for (int c = 0; c < count; c++)
            {
                ulong h = FnvOffsetBasis;
                int end = Math.Min(data.Length, (c + 1) * chunk);
                for (int i = c * chunk; i < end; i++)
                {
                    h ^= data[i];
                    h *= FnvPrime;
                }
                for (int b = 0; b < 8; b++) table[c * 8 + b] = (byte)(h >> (8 * b));
            }
            return data.Length + ":" + Convert.ToBase64String(table);
        }

        /// <summary>
        /// Resolves the shipped binaries to hash from a completed Standalone build. Returns target name
        /// → absolute path for the files that exist (GameAssembly.dll only on IL2CPP builds).
        /// </summary>
        public static Dictionary<string, string> ResolveBuildTargets(BuildReport report)
        {
            var map = new Dictionary<string, string>();
            string exePath = report.summary.outputPath;
            string exeDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(exeDir)) return map;
            var cfg = HardeningSettings.Current;

            // The managed code sits next to the exe on IL2CPP Windows builds.
            string gameAssembly = Path.Combine(exeDir, cfg.gameAssemblyName);
            if (File.Exists(gameAssembly)) map[cfg.gameAssemblyName] = gameAssembly;

            // The native core ships under <Product>_Data/Plugins/x86_64/.
            string dataDir = Path.Combine(exeDir, Path.GetFileNameWithoutExtension(exePath) + "_Data");
            if (!Directory.Exists(dataDir)) dataDir = Path.Combine(exeDir, Application.productName + "_Data");
            string nativeCore = cfg.NativeCorePath(dataDir);
            if (File.Exists(nativeCore)) map[cfg.nativeCoreName] = nativeCore;

            // IL2CPP metadata: names, strings and array data, editable without touching the managed code.
            string metadata = cfg.MetadataPath(dataDir);
            if (File.Exists(metadata)) map[cfg.il2cppMetadataName] = metadata;

            return map;
        }

        public static IntegrityManifest BuildManifest(string buildId, Dictionary<string, string> targets)
        {
            var manifest = new IntegrityManifest { buildId = buildId };
            foreach (var kv in targets)
            {
                byte[] file = File.ReadAllBytes(kv.Value);
                manifest.hashes.Add(new IntegrityManifest.Entry
                {
                    target = kv.Key,
                    hash = HashBytes(file).ToString(),
                    chunks = ChunkTableOfBytes(file)
                });

                // Baseline the in-memory .text (code) for modules the native core hashes at runtime (#4).
                // Only valid when .text carries no base relocations — otherwise ASLR shifts the loaded bytes
                // away from the file's and the baseline would false-fail; in that case we publish nothing for
                // this module's .text (its file digest still covers tampering on disk) and log why.
                if (kv.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryExtractText(file, out byte[] text, out string why))
                        manifest.hashes.Add(new IntegrityManifest.Entry
                        {
                            target = kv.Key + "!text",
                            hash = HashBytes(text).ToString(),
                            chunks = ChunkTableOfBytes(text)
                        });
                    else
                        Debug.LogWarning($"[Integrity] No in-memory .text baseline for {kv.Key}: {why}");
                }
            }
            return manifest;
        }

        /// <summary>
        /// Extracts a PE's <c>.text</c> section bytes (exactly VirtualSize, the loaded code), but only when no
        /// base relocation falls inside <c>.text</c> — the condition under which the in-memory bytes equal the
        /// file's regardless of ASLR, so the runtime digest can be baselined from disk. Returns false with a
        /// reason when the module isn't a PE32+ image or its .text would be relocated.
        /// </summary>
        public static bool TryExtractText(byte[] pe, out byte[] text, out string why)
        {
            text = null; why = null;
            try
            {
                if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') { why = "not a PE image"; return false; }
                int e_lfanew = BitConverter.ToInt32(pe, 0x3c);
                if (e_lfanew <= 0 || e_lfanew + 24 > pe.Length ||
                    pe[e_lfanew] != 'P' || pe[e_lfanew + 1] != 'E') { why = "no PE header"; return false; }
                int coff = e_lfanew + 4;
                int nSec = BitConverter.ToUInt16(pe, coff + 2);
                int optSize = BitConverter.ToUInt16(pe, coff + 16);
                int opt = coff + 20;
                int magic = BitConverter.ToUInt16(pe, opt);
                if (magic != 0x20b) { why = "not PE32+ (x64)"; return false; }
                // Data directory 5 is the base relocation table (RVA, size); 112 bytes into the optional header.
                int relocRva = BitConverter.ToInt32(pe, opt + 112 + 5 * 8);
                int relocSize = BitConverter.ToInt32(pe, opt + 112 + 5 * 8 + 4);

                int secTable = opt + optSize;
                int textVa = 0, textVSize = 0, textRaw = 0;
                var sections = new List<(int va, int vsize, int raw, int rawSize)>(nSec);
                for (int i = 0; i < nSec; i++)
                {
                    int o = secTable + i * 40;
                    string name = System.Text.Encoding.ASCII.GetString(pe, o, 8).TrimEnd('\0');
                    int vsize = BitConverter.ToInt32(pe, o + 8);
                    int va = BitConverter.ToInt32(pe, o + 12);
                    int rawSz = BitConverter.ToInt32(pe, o + 16);
                    int raw = BitConverter.ToInt32(pe, o + 20);
                    sections.Add((va, vsize, raw, rawSz));
                    if (name == ".text") { textVa = va; textVSize = vsize; textRaw = raw; }
                }
                if (textVSize <= 0) { why = "no .text section"; return false; }

                int RvaToOff(int rva)
                {
                    foreach (var s in sections)
                        if (rva >= s.va && rva < s.va + Math.Max(s.vsize, s.rawSize)) return s.raw + (rva - s.va);
                    return -1;
                }

                // Any relocation inside .text makes the in-memory bytes ASLR-dependent: reject the baseline.
                if (relocSize > 0)
                {
                    int off = RvaToOff(relocRva), end = off + relocSize;
                    while (off > 0 && off + 8 <= end)
                    {
                        int page = BitConverter.ToInt32(pe, off);
                        int block = BitConverter.ToInt32(pe, off + 4);
                        if (block < 8) break;
                        int entries = (block - 8) / 2;
                        for (int i = 0; i < entries; i++)
                        {
                            ushort ent = BitConverter.ToUInt16(pe, off + 8 + i * 2);
                            if ((ent >> 12) == 0) continue; // IMAGE_REL_BASED_ABSOLUTE padding
                            int rva = page + (ent & 0xfff);
                            if (rva >= textVa && rva < textVa + textVSize)
                            {
                                why = "has base relocations in .text (not ASLR-stable)";
                                return false;
                            }
                        }
                        off += block;
                    }
                }

                int len = Math.Min(textVSize, pe.Length - textRaw);
                if (len <= 0) { why = ".text raw data out of range"; return false; }
                text = new byte[len];
                Array.Copy(pe, textRaw, text, 0, len);
                return true;
            }
            catch (Exception e)
            {
                why = e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Per-build expected digests. `hash` is FNV-1a-64 as a decimal string (matches the client); `chunks` is the
    /// table the module answers integrity challenges from (see IntegrityHashing.ChunkTable).
    /// </summary>
    [Serializable]
    public class IntegrityManifest
    {
        public string buildId;
        public List<Entry> hashes = new List<Entry>();

        [Serializable]
        public class Entry
        {
            public string target;
            public string hash;
            public string chunks;
        }

        public string ToJson() => JsonUtility.ToJson(this, true);
    }
}
