# MemphisGameDev Hardening

A build-hardening pipeline for shipping Unity games: ship packaging from an allowlist, build hygiene,
server-checked binary integrity, a per-build watermark, LAN licensing, and test-menu/demo build guards.
Game-specific values are configured per project through a single `HardeningSettings` asset, and the
runtime sits behind two small seams a game implements, so the package carries no game-specific logic.

## Install

Add it to your project's `Packages/manifest.json` (pin to a tag):

```json
"com.memphisgamedev.hardening": "https://github.com/evilotaku/com.memphisgamedev.hardening.git#v0.2.6"
```

It depends on the UGS Authentication, Cloud Code and Remote Config packages (declared in `package.json`,
resolved automatically) and on Newtonsoft Json.

## Tutorial: adding it to an existing project

The package ships the **client runtime** (`IntegrityGuard`, `LanLicence`) and the **Editor build
pipeline** (packaging, hygiene, integrity hashing, watermark, native-core build step). It carries no
game-specific logic: you supply three project-specific pieces behind its seams —

1. a **native core** (a C++ plug-in behind `INativeCore`),
2. a **session adapter** (`IHardenedSession` on your networking object),
3. a **Cloud Code module** (the server the clients report to),

plus one `HardeningSettings` asset and a few secrets in environment variables.

You can adopt it incrementally: with the **managed fallback** core and the integrity loop alone you get
server-checked integrity in the Editor and dev builds on day one; the native core, LAN licensing and the
watermark harden a real release. Steps 1–5 stand up the client; 6–9 stand up the server and release
hardening.

**Prerequisites:** IL2CPP scripting backend, your project linked to a UGS project (Services window), and
the Authentication/Cloud Code/Remote Config packages (pulled in automatically by this package).

### 1. Install and import the samples
Add the git URL above to `Packages/manifest.json`. Then open **Window ▸ Package Manager ▸ MemphisGameDev
Hardening ▸ Samples** and **Import** all three — *Seam adapters*, *Integrity Cloud Code module*,
*Obfuscated native core*. They land in `Assets/Samples/MemphisGameDev Hardening/<version>/…` as editable
templates you own and fill in; they are not shipping code.

### 2. Create and configure the settings asset
**Assets ▸ Create ▸ Hardening ▸ Settings** (put it anywhere under `Assets`). `HardeningSettings.Current`
finds it automatically. The defaults are Shop-Sim-shaped — change them to match your project:

- `nativeCoreName`, `nativePluginSubpath`, `nativeSourceDir` → your native plug-in's DLL name, its
  `<Product>_Data`-relative folder, and its source folder.
- `watermarkClassNamespace` → the namespace of your game's `BuildWatermark` class (see step 8).
- the generated-source paths (`expectedDigestsPath`, `watermarkGeneratedPath`,
  `watermarkReleasesPath`, `runtimeConfigGeneratedPath`) → folders that exist in *your* `Assets`.
- `watermarkSecretVar`, `licenceKeyVar` → the env-var **names** you'll use (values are never stored here).

`gameAssemblyName` / `il2cppMetadataName` / their subpaths are the standard IL2CPP Windows layout; leave
them unless you target a different platform.

### 3. Implement the native-core seam (`INativeCore`)
Start from the *Obfuscated native core* sample (`hardening_core.cpp` + `build.ps1`): it exports the ABI
(`hc_hash_region`, `hc_challenge_digest`, `hc_text_digest`, `hc_tamper_scan`, `hc_device_id`,
`hc_licence_check`). The two digests are real; the tamper scan, device id and licence verify are stubs —
fill them in privately and obfuscate the build (that's the whole point of a native core). Compile the DLL
into the folder your settings point at.

Then copy `ExampleNativeCore.cs` from the *Seam adapters* sample: it P/Invokes that ABI, falls back to a
managed FNV implementation when the DLL is absent (so the Editor and no-core dev builds still run), and
self-registers with `NativeCore.Current` via `[RuntimeInitializeOnLoadMethod]`. Rename `Lib`/the exports
to match your core. **No native core yet?** Ship just the managed fallback for now — integrity still works;
tamper scan returns clean and licences validate locally (fine for Editor/dev, not for release).

### 4. Implement the session seam (`IHardenedSession`)
Implement it on your real networking/session object (the thing that joins and leaves online sessions),
using `ExampleSession.cs` as the template:

- In `Awake`: `HardenedSession.Current = this;` then `IntegrityGuard.Start();` and
  `LanLicence.RefreshIfDue();`. Clear `Current` and call `IntegrityGuard.Stop()` in `OnDestroy`.
- `EnsureSignedInAsync()` → initialize Unity Services and sign in (anonymous or your own auth).
- `SendIntegrityPass(nonce, pass)` → send the server-issued pass to the **host** over your transport.
- `Leave()` → tear down the session (used when a check returns `action = "disconnect"`).
- `ProfileId` → a per-process suffix (e.g. from a `-profile` arg) so copies on one machine keep separate
  licences, or `null`.

Host side: when you challenge a client, have that client call `IntegrityGuard.OnChallenge(nonce, immediate)`
(wire it to your network message), and verify the returned pass on the host with
`IntegrityGuard.VerifyPassAsync(pass, nonce)`.

### 5. (Optional) tune via Remote Config
The guard reads these keys from Remote Config each cycle (defaults in parentheses apply if absent):
`integrity_enabled` (on), `integrity_interval_sec` (150), `integrity_jitter_sec` (90), `integrity_epoch`
(0), `integrity_targets`, `integrity_admit_timeout_sec` (60), `integrity_grace_sec` (120),
`integrity_allow_unknown_build` (off), `integrity_report_moderation` (on). Add them in your UGS
environment to change the schedule/policy without a client patch.

### 6. Deploy the Cloud Code module
Copy the *Integrity Cloud Code module* sample into your `Assets`. Fill in the four endpoints'
`// TODO` bodies privately — `IntegrityChallenge`, `ValidateIntegrity` (compare the client's report to your
per-build baselines), `VerifyPass`, `IssueLanLicence` (sign with your licence key). Set the server-side key
env vars (`PassKey`, `LicenceKey`) in your deploy environment, then deploy with
**Tools ▸ Builds ▸ Integrity ▸ Deploy Module Now**. The module's security rests on the **keys** and the
**baselines** staying private — not on the code being hidden.

### 7. Set the secrets (environment variables)
Keep every secret in environment variables, never in the repo:

- Client/build machine: the watermark secret (`HARDENING_WATERMARK_SECRET` by default) and, for signing
  licences at build time, the licence key (`HARDENING_LICENCE_KEY`). Create the watermark secret with
  **Tools ▸ Builds ▸ Watermark ▸ Create Secret**.
- Server (UGS) environment: the Cloud Code module's pass and licence signing keys. Only the licence
  **public** key is compiled into the native core.

### 8. Add the watermark partial
The watermark build step writes `BuildWatermark.Generated.cs` as a `partial` of your game's
`BuildWatermark` class. Provide the other half: a `BuildWatermark.cs` in `Assets` whose namespace matches
`watermarkClassNamespace`, declaring the partial method the generated file fills:

```csharp
namespace YourGame            // == HardeningSettings.watermarkClassNamespace
{
    internal static partial class BuildWatermark
    {
        static partial void Load(ref byte[] marker);
    }
}
```

Git-ignore the generated file (`BuildWatermark.Generated.cs`) and the generated runtime-config
(`HardeningRuntimeConfig.Generated.cs`) — both are per-build.

### 9. Turn on the build pipeline and release
Under **Tools ▸ Builds**: enable *Require Native Core* and *Auto-build Native Core on Player Build* (set the
xollvm toolchain folder first), *Delete IL2CPP Backup Folder After Build*, and leave *Include Test Menus*
off for releases. After a player build, regenerate baselines with
**Tools ▸ Builds ▸ Integrity ▸ Regenerate Expected Digests Source** and redeploy the module, then produce
the distributable with **Tools ▸ Builds ▸ Package Build for Distribution…** (it refuses development,
test-menu or symbol-leaking builds).

### 10. (Optional) obfuscate the hardening assembly
If you use [Obfuz](https://github.com/focus-creative-games/obfuz): add `MemphisGameDev.Hardening` to
*Assemblies To Obfuscate*, and add a symbol-obfus rule keeping the member names of the four `[Serializable]`
Cloud Code response DTOs (`IntegrityGuard.Result`/`ChallengeResult`/`PassCheck`, `LanLicence.IssueResult`) —
otherwise Newtonsoft can't match the server's JSON:

```xml
<obfuz>
  <assembly name="MemphisGameDev.Hardening">
    <type name="*" hasCustomAttributes="System.SerializableAttribute" obName="false" applyToMembers="all"/>
  </assembly>
</obfuz>
```

You do **not** need a `link.xml` entry for the package: those DTO fields carry `[Preserve]`, so High managed
stripping keeps them on their own.

### 11. Verify
Make a build, go online with two instances. The client should pass the integrity handshake and stay
connected; a release player should fetch and renew its LAN licence. If deserialization fails in a built
player only (`Could not find member '…'`), a DTO member was stripped or renamed — revisit steps 10/8.

## Runtime (`MemphisGameDev.Hardening`)

- **`IntegrityGuard`** — server-driven binary integrity checks: hashes the shipped binaries, answers
  Cloud Code challenges, and gates/polices online clients.
- **`LanLicence`** — LAN-play licensing, verified in the native core (public key only).
- **Seams the game implements and registers:**
  - **`INativeCore`** / `NativeCore.Current` — the native-core operations (digests, tamper scan, device
    id, licence verify). A game registers a P/Invoke implementation over its own obfuscated core.
  - **`IHardenedSession`** / `HardenedSession.Current` — the hook into the game's networking/session.
  - **`HardeningRuntimeConfig`** — the binary names/subpaths the runtime needs in a built player; the
    Editor side generates its registration from the `HardeningSettings` asset.

## Editor (`MemphisGameDev.Hardening.Editor`)

- **`HardeningSettings`** — the per-project configuration asset (binary names, IL2CPP sub-paths, native
  source dir, environment-variable names, generated-source paths). Create one from
  **Assets ▸ Create ▸ Hardening ▸ Settings**; `HardeningSettings.Current` falls back to safe defaults.
- **Ship packaging & hygiene** (`ShipPackager`, `ShipHygieneBuildStep`) — builds the distributable from an
  allowlist and refuses development, test-menu or symbol-leaking builds.
- **Integrity** (`IntegrityHashing`, `IntegrityBuildPostprocessor`, module writer/deployer/uploader) —
  FNV-1a-64 whole-file and per-chunk digests, and the Cloud Code module tooling.
- **Watermark** (`WatermarkBuildStep`) — per-build HMAC watermark compiled into the game and native core.
- **Native core & guards** (`NativeCoreBuildStep`, `LanLicenceKeys`, `TestMenusBuildOption`).

Editor commands live under **Tools ▸ Builds ▸ …** (integrity under **Tools ▸ Builds ▸ Integrity**).

## Samples

Import from the Package Manager. All three are generic templates, not shipping code:

- **Seam adapters** — `INativeCore` / `IHardenedSession` examples.
- **Integrity Cloud Code module** — a skeleton of the server endpoints.
- **Obfuscated native core** — the C++ ABI and an xollvm build script.

## License

MIT — see `LICENSE.md`.
