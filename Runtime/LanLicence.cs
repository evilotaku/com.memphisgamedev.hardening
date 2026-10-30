using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Services.CloudCode;
using UnityEngine;
using UnityEngine.Scripting;

namespace MemphisGameDev.Hardening
{
    /// <summary>
    /// LAN play licence. Online sessions are policed by the integrity check (IntegrityGuard), but a LAN session has no
    /// UGS sign-in to check against, so a copied build could otherwise play LAN forever. A release player therefore only
    /// hosts or joins a LAN game while it holds a licence from the Cloud Code IntegrityModule (IssueLanLicence), which
    /// the module only issues after a clean integrity check. The licence is signed by the module, bound to this machine
    /// and this build, and lasts 14 days; it renews in the background whenever the game starts online with less than a
    /// week left, so a player who goes online now and then never notices it.
    ///
    /// Verification lives in the obfuscated native core (<see cref="INativeCore.LicenceCheck"/>): only the public key ships, and the
    /// core reads the clock and the device id itself. A LAN host also checks every client's licence before admitting it
    /// (ShopSimNetwork.Licence.cs).
    ///
    /// Enforced only in release players: never in the Editor (no native core), and in development builds only with
    /// -mpLanLicence on the command line, so tests and the two-peer smoke test are unaffected.
    /// </summary>
    public static class LanLicence
    {
        [Serializable]
        [Preserve]
        public class IssueResult
        {
            public bool ok;
            public string licence = "";
            public long expires;
            public string reason = "";
        }

        private const string FileName = "lan_licence.txt";
        private const long RenewWithinSec = 7 * 86400;

        private static string _token;
        private static bool _loaded;
        private static Task<bool> _refresh;

        /// <summary>Why the last refresh failed, for the menu; empty after a success.</summary>
        public static string LastError { get; private set; } = "";

        public static bool Enforced
        {
            get
            {
#if UNITY_EDITOR
                return false;
#else
                return !Debug.isDebugBuild || Array.IndexOf(Environment.GetCommandLineArgs(), "-mpLanLicence") >= 0;
#endif
            }
        }

        /// <summary>This machine's licence as issued, or null.</summary>
        public static string Token
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try { _token = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null; }
                    catch (Exception e) { Debug.LogWarning("[LanLicence] could not read the licence: " + e.Message); }
                }
                return _token;
            }
        }

        private static string FilePath
        {
            get
            {
                // -mpProfile copies on one machine are separate UGS players, so each keeps its own licence.
                string profile = HardenedSession.Current?.ProfileId;
                string name = profile != null ? "lan_licence_" + string.Join("_", profile.Split(Path.GetInvalidFileNameChars())) + ".txt" : FileName;
                return Path.Combine(Application.persistentDataPath, name);
            }
        }

        /// <summary>True when LAN play is allowed: not enforced, or this machine holds a valid licence for this build.</summary>
        public static bool Ready
        {
            get { return !Enforced || CheckOwn(out _) == 0; }
        }

        /// <summary>Checks this machine's own licence; 0 when valid (see <see cref="Describe"/>).</summary>
        public static int CheckOwn(out long expires)
        {
            return NativeCore.Current.LicenceCheck(Token, Application.version, true, out _, out expires);
        }

        /// <summary>Host side: checks a client's licence (any machine, this build); 0 when valid.</summary>
        public static int CheckPeer(string token, out string playerId)
        {
            return NativeCore.Current.LicenceCheck(token, Application.version, false, out playerId, out _);
        }

        /// <summary>A player-facing reason for a failed check.</summary>
        public static string Describe(int code)
        {
            switch (code)
            {
                case -4: return "Your LAN play licence has expired. Connect to the internet once to renew it.";
                case -5: return "Your LAN play licence is for another version of the game. Connect to the internet once to renew it.";
                case -6: return "Your LAN play licence belongs to another computer. Connect to the internet once to get one for this one.";
                case -7: return "Your computer's clock is wrong. Fix the date and time, then try again.";
                default: return "LAN play needs a one-time online check of your copy of the game. Connect to the internet and try again.";
            }
        }

        /// <summary>Renews the licence in the background when it is missing, invalid or within a week of expiring.</summary>
        public static void RefreshIfDue()
        {
            if (!Enforced) return;
            if (CheckOwn(out long expires) == 0 && expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > RenewWithinSec) return;
            _ = RefreshAsync();
        }

        /// <summary>Asks the IntegrityModule for a new licence (one request at a time). True when one was stored.</summary>
        public static Task<bool> RefreshAsync()
        {
            if (_refresh == null || _refresh.IsCompleted) _refresh = DoRefreshAsync();
            return _refresh;
        }

        private static async Task<bool> DoRefreshAsync()
        {
            try
            {
                IHardenedSession session = HardenedSession.Current;
                if (session == null) throw new InvalidOperationException("No session registered for sign-in.");
                await session.EnsureSignedInAsync();
                string device = NativeCore.Current.DeviceId();
                if (device == null)
                {
                    LastError = "This computer could not be identified for LAN play.";
                    return false;
                }
                Dictionary<string, object> args = await IntegrityGuard.EvidenceAsync(IntegrityGuard.Settings.targets, CancellationToken.None);
                args["device"] = device;
                IssueResult r = await CloudCodeService.Instance.CallModuleEndpointAsync<IssueResult>("IntegrityModule", "IssueLanLicence", args);
                if (r == null || !r.ok || string.IsNullOrEmpty(r.licence))
                {
                    string reason = r != null ? r.reason : "";
                    Debug.LogWarning("[LanLicence] licence refused: " + reason);
                    LastError = reason == "unknown-build"
                        ? "This version of the game can't be verified. Please update it."
                        : "Your game files failed verification, so LAN play is unavailable. Reinstall the game.";
                    return false;
                }
                _token = r.licence;
                _loaded = true;
                File.WriteAllText(FilePath, r.licence);
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LanLicence] licence refresh failed: " + e.Message);
                // Usually offline: say what is wrong with the licence this machine already has.
                LastError = Describe(CheckOwn(out _));
                return false;
            }
        }
    }
}
