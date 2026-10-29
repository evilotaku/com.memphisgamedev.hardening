# Seam adapters (example)

Generic templates for the two seams the hardening runtime depends on. Copy them into your game and
adapt — they're a starting point, not shipping code.

| File | Shows |
| --- | --- |
| `ExampleNativeCore.cs` | An `INativeCore` that P/Invokes a native plug-in (`hardening_core`, the NativeCore sample) and falls back to a managed FNV implementation when the plug-in is absent, so it works in the Editor too. Self-registers into `NativeCore.Current` at startup. |
| `ExampleSession.cs` | A `MonoBehaviour` implementing `IHardenedSession`, registering `HardenedSession.Current`, and driving `IntegrityGuard` / `LanLicence`. In a real game these members live on your networking/session object. |

## Integration steps
1. Implement `INativeCore` over your native core (or start with the managed fallback) and set
   `NativeCore.Current` from a `[RuntimeInitializeOnLoadMethod]` — see `ExampleNativeCore.cs`.
2. Implement `IHardenedSession` on your session object; set `HardenedSession.Current` when it comes
   up and clear it when it goes away — see `ExampleSession.cs`.
3. Drive the guard from your session: `IntegrityGuard.Start/Stop`, `IntegrityGuard.OnChallenge`
   from your host-challenge message, and `LanLicence.RefreshIfDue` / `LanLicence.Ready` /
   `LanLicence.CheckPeer` for LAN play.
4. Create a **HardeningSettings** asset (Assets ▸ Create ▸ Hardening ▸ Settings) with your binary
   names and paths; the package's Editor side generates the matching runtime config.

> Security note: the managed fallback does no tamper resistance and treats licences as valid — it's
> for the Editor and development. Shipping protection comes from the native core behind `INativeCore`
> plus server-side verification; the strength is in that core and your signing keys, not this glue.
