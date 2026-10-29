# MemphisGameDev Hardening

A build-hardening pipeline for shipping Unity games: ship packaging from an allowlist, build hygiene,
server-checked binary integrity, a per-build watermark, LAN licensing, and test-menu/demo build guards.
Game-specific values are configured per project through a single `HardeningSettings` asset, and the
runtime sits behind two small seams a game implements, so the package carries no game-specific logic.

## Install

Add it to your project's `Packages/manifest.json` (pin to a tag):

```json
"com.memphisgamedev.hardening": "https://github.com/evilotaku/com.memphisgamedev.hardening.git#v0.2.0"
```

It depends on the UGS Authentication, Cloud Code and Remote Config packages (declared in `package.json`,
resolved automatically) and on Newtonsoft Json.

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
