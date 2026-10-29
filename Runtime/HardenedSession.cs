using System.Threading.Tasks;

namespace MarketShopandRetailSystem.Multiplayer
{
    /// <summary>
    /// The integration surface the hardening runtime (<see cref="IntegrityGuard"/>, <see cref="LanLicence"/>) uses to
    /// talk to the game's networking, instead of referencing the concrete session directly. Letting the hardening code
    /// depend on this seam rather than on <c>ShopSimNetwork</c> is what lets it move into a reusable package: another
    /// game implements this interface on its own session type and registers it with <see cref="HardenedSession"/>.
    /// </summary>
    public interface IHardenedSession
    {
        /// <summary>Hands a server-issued integrity pass to the host, which is what lets the host act on the check.</summary>
        void SendIntegrityPass(string nonce, string pass);

        /// <summary>Leaves the current session (used when an integrity check fails with "disconnect").</summary>
        void Leave();

        /// <summary>
        /// A per-process identity suffix (e.g. from <c>-mpProfile</c>) that keeps several copies on one machine apart,
        /// or null for the default. The LAN licence is stored per profile so the copies don't share one.
        /// </summary>
        string ProfileId { get; }

        /// <summary>Starts Unity Services and signs in if needed, so Cloud Code calls can be made.</summary>
        Task EnsureSignedInAsync();
    }

    /// <summary>
    /// Holds the one <see cref="IHardenedSession"/> the game registered. The game sets <see cref="Current"/> once its
    /// session object exists (see <c>ShopSimNetwork.Awake</c>) and clears it when the object goes away.
    /// </summary>
    public static class HardenedSession
    {
        /// <summary>The game's current integration, or null before the session object exists.</summary>
        public static IHardenedSession Current { get; set; }
    }
}
