using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.CloudCode;
using Unity.Services.RemoteConfig;
using UnityEngine;
using UnityEngine.Scripting;

namespace MemphisGameDev.Hardening
{
    /// <summary>
    /// Server-driven binary integrity check. It hashes the shipped binaries (via the obfuscated native core
    /// <see cref="INativeCore"/>) and reports the digests to the Cloud Code IntegrityModule, which compares
    /// them against the clean build and can order the client to disconnect.
    ///
    /// In an online session the host gates and polices clients with it (see ShopSimNetwork.Integrity.cs):
    /// the host challenges each client with a nonce, the module answers the client's report with a signed
    /// pass bound to that nonce, and the host verifies the pass before admitting the client and again every
    /// cycle after. Checks run as soon as a host challenges and then at a randomized cadence set by Remote
    /// Config.
    ///
    /// This is defense-in-depth / bar-raising: client attestation can never be perfect. Everything here is
    /// best-effort — Remote Config, Cloud Code, the services packages, file IO and the native core can all
    /// be absent or throw (notably in the Editor), and the guard simply backs off and keeps the game running.
    /// </summary>
    public static class IntegrityGuard
    {
        private struct UserAttributes { }
        private struct AppAttributes { }

        [Serializable]
        [Preserve]
        public class Result
        {
            [Preserve] public bool ok = true;
            [Preserve] public string action = "";  // "disconnect" acts; anything else is advisory
            [Preserve] public string reason = "";
            [Preserve] public string pass = "";    // signed by the module when a host nonce was sent
        }

        [Serializable]
        [Preserve]
        public class ChallengeResult
        {
            [Preserve] public string challenge = "";
        }

        [Serializable]
        [Preserve]
        public class PassCheck
        {
            [Preserve] public bool valid;
            [Preserve] public string verdict = "";  // ok, open, unk, missing, bad
            [Preserve] public string playerId = "";
            [Preserve] public string reason = "";
        }

        /// <summary>Check schedule and host policy, from Remote Config (defaults when it is unreachable).</summary>
        public class Config
        {
            public bool enabled = true;
            public int intervalSec = 150;
            public int jitterSec = 90;
            // Extra targets beyond the built-in binaries; the built-ins (HardeningRuntimeConfig) are always hashed.
            public string targets = "";
            public int epoch;
            /// <summary>Seconds a new client has to pass its first check before the host kicks it.</summary>
            public int admitTimeoutSec = 60;
            /// <summary>Slack on top of interval + jitter before a missing periodic pass counts as ignored.</summary>
            public int graceSec = 120;
            /// <summary>Admit clients whose build has no published digests (old or dev builds).</summary>
            public bool allowUnknownBuild;
            /// <summary>Report clients that fail or ignore checks to UGS Moderation.</summary>
            public bool reportToModeration = true;

            /// <summary>Longest a client may go between passes once admitted.</summary>
            public float PeriodicDeadlineSec
            {
                get { return Math.Max(30, intervalSec) + Math.Max(0, jitterSec) + Math.Max(0, graceSec); }
            }
        }

        public static Config Settings { get; private set; } = new Config();

        private static CancellationTokenSource _cts;
        private static bool _running;
        // The newest unanswered host challenge, and a signal that wakes the loop to answer it right away.
        private static string _nonce = "";
        private static TaskCompletionSource<bool> _wake;

        /// <summary>Begins the check loop (idempotent). No-op when not signed in to UGS (LAN play).</summary>
        public static void Start()
        {
            if (_running) return;
            if (!SignedIn) return;
            _running = true;
            _nonce = "";
            _wake = new TaskCompletionSource<bool>();
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }

        /// <summary>Stops the check loop.</summary>
        public static void Stop()
        {
            _running = false;
            _nonce = "";
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts = null;
        }

        /// <summary>
        /// The host sent a challenge. The next report answers it; <paramref name="immediate"/> (the admission
        /// challenge) runs that report now instead of at the next scheduled check.
        /// </summary>
        public static void OnChallenge(string nonce, bool immediate)
        {
            _nonce = nonce ?? "";
            if (!_running) Start();
            if (immediate) _wake?.TrySetResult(true);
        }

        public static bool SignedIn
        {
            get
            {
                try { return AuthenticationService.Instance.IsSignedIn; }
                catch { return false; } // services not initialized
            }
        }

        /// <summary>Fetches the schedule and policy from Remote Config, keeping the previous values on failure.</summary>
        public static async Task RefreshConfigAsync()
        {
            var c = new Config();
            try
            {
                RuntimeConfig cfg = await RemoteConfigService.Instance
                    .FetchConfigsAsync(new UserAttributes(), new AppAttributes());
                c.enabled = cfg.GetBool("integrity_enabled", c.enabled);
                c.intervalSec = cfg.GetInt("integrity_interval_sec", c.intervalSec);
                c.jitterSec = cfg.GetInt("integrity_jitter_sec", c.jitterSec);
                c.targets = cfg.GetString("integrity_targets", c.targets);
                c.epoch = cfg.GetInt("integrity_epoch", c.epoch);
                c.admitTimeoutSec = cfg.GetInt("integrity_admit_timeout_sec", c.admitTimeoutSec);
                c.graceSec = cfg.GetInt("integrity_grace_sec", c.graceSec);
                c.allowUnknownBuild = cfg.GetBool("integrity_allow_unknown_build", c.allowUnknownBuild);
                c.reportToModeration = cfg.GetBool("integrity_report_moderation", c.reportToModeration);
                Settings = c;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IntegrityGuard] Remote Config unavailable: " + e.Message);
            }
        }

        /// <summary>Host side: asks the module whether a client's pass is genuine. Null if Cloud Code is unreachable.</summary>
        public static async Task<PassCheck> VerifyPassAsync(string pass, string nonce)
        {
            var args = new Dictionary<string, object> { { "pass", pass ?? "" }, { "nonce", nonce ?? "" } };
            try { return await CloudCodeService.Instance.CallModuleEndpointAsync<PassCheck>("IntegrityModule", "VerifyPass", args); }
            catch (Exception e) { Debug.LogWarning("[IntegrityGuard] VerifyPass unavailable: " + e.Message); return null; }
        }

        private static async Task RunAsync(CancellationToken ct)
        {
            var rng = new System.Random();
            // Stagger the first unprompted check so clients don't all report at once; a host challenge
            // cuts the wait short.
            if (!await Wait(10 + rng.Next(30), ct)) return;

            while (_running && !ct.IsCancellationRequested)
            {
                await RefreshConfigAsync();
                Config cfg = Settings;
                // A host that enforces checks still expects answers when Remote Config disables them for
                // this client, so a pending challenge is always answered.
                if (cfg.enabled || _nonce.Length > 0)
                {
                    try { await RunOneCheck(cfg, ct); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception e) { Debug.LogWarning("[IntegrityGuard] check error: " + e.Message); }
                }

                int wait = Math.Max(30, cfg.intervalSec) + rng.Next(Math.Max(0, cfg.jitterSec) + 1);
                if (!await Wait(wait, ct)) break;
            }
        }

        private static async Task RunOneCheck(Config cfg, CancellationToken ct)
        {
            string nonce = _nonce;
            _nonce = "";

            Result r;
            try
            {
                Dictionary<string, object> args = await EvidenceAsync(cfg.targets, ct);
                if (ct.IsCancellationRequested) return;
                args["epoch"] = cfg.epoch;
                args["nonce"] = nonce;
                r = await CloudCodeService.Instance.CallModuleEndpointAsync<Result>("IntegrityModule", "ValidateIntegrity", args);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IntegrityGuard] Cloud Code unavailable: " + e.Message);
                if (_nonce.Length == 0) _nonce = nonce; // answer the challenge on the next attempt
                return;
            }
            if (ct.IsCancellationRequested || r == null) return;

            // Continuations resume on Unity's context, so Unity and Netcode APIs are safe from here.
            IHardenedSession session = HardenedSession.Current;
            // The pass goes to the host even when the check failed: it is what lets the host act on it.
            if (session != null && nonce.Length > 0 && !string.IsNullOrEmpty(r.pass)) session.SendIntegrityPass(nonce, r.pass);

            if (!r.ok && r.action == "disconnect")
            {
                Debug.LogWarning("[IntegrityGuard] integrity check failed (" + r.reason + ") — leaving session.");
                session?.Leave();
            }
        }

        /// <summary>The build id reported to the module. The Editor has no shipped binaries, so it reports as an unknown build instead of failing.</summary>
        public static string ReportedBuildId
        {
            get { return Application.isEditor ? Application.version + "-editor" : Application.version; }
        }

        /// <summary>
        /// What a check reports to the module: the build id, a fresh module challenge, and every target's digest
        /// answering that challenge's salt (so the answer changes every time and can't be replayed). Throws when
        /// Cloud Code is unreachable.
        /// </summary>
        public static async Task<Dictionary<string, object>> EvidenceAsync(string targets, CancellationToken ct)
        {
            ChallengeResult c = await CloudCodeService.Instance.CallModuleEndpointAsync<ChallengeResult>(
                "IntegrityModule", "IntegrityChallenge", new Dictionary<string, object>());
            string challenge = c != null && c.challenge != null ? c.challenge : "";
            byte[] salt = ChallengeSalt(challenge);
            string hashes = await HashTargetsAsync(targets, salt, ct);
            // Runtime evidence (#4), gathered on the main thread so the per-thread hardware-breakpoint
            // probe sees the game thread: in-memory .text digests (server-verified against the same chunk
            // tables) and the native tamper bitmask. Absent in the Editor, where the native core isn't loaded.
            hashes = AppendTextDigests(hashes, salt);
            return new Dictionary<string, object>
            {
                { "buildId", ReportedBuildId },
                { "hashes", hashes },
                { "challenge", challenge },
                { "tamper", NativeCore.Current.TamperScan() },
            };
        }

        /// <summary>In-memory ".text" (code) digest of each loaded crown-jewel module that reports one,
        /// as extra "<c>name!text</c>=digest" targets the server judges from its baselines. A live code
        /// patch changes the digest; a module that isn't loaded is simply left out (not a mismatch).</summary>
        private static string AppendTextDigests(string hashes, byte[] salt)
        {
            if (salt == null) return hashes; // no salt means no challenge to answer; skip
            var sb = new StringBuilder(hashes);
            foreach (string module in HardeningRuntimeConfig.Current.TextTargets)
            {
                if (!NativeCore.Current.TextDigest(module, salt, out ulong digest)) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(module).Append("!text=").Append(digest);
            }
            return sb.ToString();
        }

        /// <summary>The salt inside a module challenge (base64url "c1|salt|playerId|issued", then the signature), or null.</summary>
        private static byte[] ChallengeSalt(string challenge)
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
            catch (FormatException)
            {
                return null;
            }
        }

        private static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }

        /// <summary>
        /// "target=digest" pairs for every shipped binary that exists: the built-in targets plus any extra ones in
        /// <paramref name="targets"/> (Remote Config's list). Always the built-in ones: the server expects every digest
        /// a build published, and an older Remote Config list would otherwise leave one out ("missing"). With a
        /// <paramref name="salt"/> each digest answers that challenge (<see cref="INativeCore.ChallengeDigest"/>), else it is the
        /// whole-file digest. Hashed off the main thread so a large binary never hitches a frame; the native core
        /// P/Invokes are thread-safe (pure functions).
        /// </summary>
        public static Task<string> HashTargetsAsync(string targets, byte[] salt, CancellationToken ct)
        {
            var paths = new List<KeyValuePair<string, string>>();
            var names = new List<string>(HardeningRuntimeConfig.Current.BuiltInTargets);
            foreach (string raw in (targets ?? string.Empty).Split(','))
            {
                if (raw.Trim().Length > 0 && !names.Contains(raw.Trim())) names.Add(raw.Trim());
            }
            foreach (string target in names)
            {
                string path = ResolveTarget(target);
                if (path != null && File.Exists(path)) paths.Add(new KeyValuePair<string, string>(target, path));
            }
            INativeCore core = NativeCore.Current;
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var t in paths)
                {
                    byte[] bytes = File.ReadAllBytes(t.Value);
                    if (sb.Length > 0) sb.Append(',');
                    ulong digest = salt != null
                        ? core.ChallengeDigest(bytes, bytes.Length, salt)
                        : core.HashRegion(bytes, bytes.Length);
                    sb.Append(t.Key).Append('=').Append(digest);
                }
                return sb.ToString();
            }, ct);
        }

        /// <summary>Resolves a target binary name to its on-disk path in a Standalone build, from the runtime config.</summary>
        private static string ResolveTarget(string target)
        {
            if (Application.isEditor) return null; // the shipped binaries don't exist in the Editor
            HardeningRuntimeConfig cfg = HardeningRuntimeConfig.Current;
            string exeDir = Directory.GetParent(Application.dataPath)?.FullName;
            if (target == cfg.gameAssemblyName)
                return exeDir != null ? Path.Combine(exeDir, cfg.gameAssemblyName) : null;
            if (target == cfg.nativeCoreName)
                return Under(Application.dataPath, cfg.nativePluginSubpath, cfg.nativeCoreName);
            if (target == cfg.il2cppMetadataName)
                // IL2CPP's names, strings and array data (including the build watermark): editing it changes
                // the game without touching the managed assembly.
                return Under(Application.dataPath, cfg.il2cppMetadataSubpath, cfg.il2cppMetadataName);
            return null;
        }

        /// <summary>Joins a base dir, a "/"- or "\"-separated subpath and a file into a platform path.</summary>
        private static string Under(string baseDir, string subpath, string file)
        {
            var parts = new List<string> { baseDir };
            parts.AddRange(subpath.Split('/', '\\'));
            parts.Add(file);
            return Path.Combine(parts.ToArray());
        }

        /// <summary>Waits <paramref name="seconds"/> or until a host challenge asks for a check now.</summary>
        private static async Task<bool> Wait(int seconds, CancellationToken ct)
        {
            TaskCompletionSource<bool> wake = _wake;
            try
            {
                Task delay = Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                if (wake != null) await Task.WhenAny(delay, wake.Task);
                else await delay;
            }
            catch (OperationCanceledException) { return false; }
            if (wake != null && wake.Task.IsCompleted) _wake = new TaskCompletionSource<bool>();
            return !ct.IsCancellationRequested;
        }
    }
}
