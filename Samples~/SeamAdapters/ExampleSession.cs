// Example IHardenedSession — a template for the session seam. A real game implements this on its
// actual networking/session object (the thing that joins and leaves online sessions) and drives
// IntegrityGuard / LanLicence from there. This stand-alone MonoBehaviour shows the wiring with no
// networking stack attached.

using System.Threading.Tasks;
using UnityEngine;
using MarketShopandRetailSystem.Multiplayer; // IHardenedSession, HardenedSession, IntegrityGuard, LanLicence

namespace HardeningExample
{
    public sealed class ExampleSession : MonoBehaviour, IHardenedSession
    {
        private void Awake()
        {
            // Register before anything drives the guard. Clear it in OnDestroy.
            HardenedSession.Current = this;

            IntegrityGuard.Start();    // begins the check loop once signed in (no-op otherwise)
            LanLicence.RefreshIfDue(); // renews the LAN licence when enforced (release players only)
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(HardenedSession.Current, this)) HardenedSession.Current = null;
            IntegrityGuard.Stop();
        }

        // When the host challenges this client, forward the challenge to IntegrityGuard so the next
        // report answers it. Call this from your networking message handler:
        //   public void OnHostChallenge(string nonce, bool immediate) => IntegrityGuard.OnChallenge(nonce, immediate);

        // --- IHardenedSession ---

        // Send the server-issued pass to the host over your transport, so the host can verify it.
        public void SendIntegrityPass(string nonce, string pass)
        {
            // e.g. NetworkSend(new IntegrityPassMessage { nonce = nonce, pass = pass });
            Debug.Log($"[Hardening example] would send pass for nonce {nonce} ({pass?.Length ?? 0} chars)");
        }

        // Leave the current session when a check fails with "disconnect".
        public void Leave()
        {
            // e.g. NetworkManager.Shutdown(); LoadMainMenu();
            Debug.Log("[Hardening example] would leave the session");
        }

        // A per-process identity suffix that keeps several copies on one machine apart (or null).
        public string ProfileId => null;

        // Initialize Unity Services and sign in so Cloud Code calls can be made.
        public async Task EnsureSignedInAsync()
        {
            await Unity.Services.Core.UnityServices.InitializeAsync();
            if (!Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
                await Unity.Services.Authentication.AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
    }
}
