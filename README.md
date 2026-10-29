# MemphisGameDev Hardening

Build-hardening pipeline for shipping Unity games, being extracted from Multiplayer Shop Sim into a reusable
package. **Embedded** under `Packages/` for now; it will move to its own git repository once the extraction is
complete.

## What's here (Editor)

- **HardeningSettings** — per-project configuration asset (binary names, IL2CPP sub-paths, native source dir,
  environment-variable names, generated-source paths). Everything else reads from it, so another game only edits the
  asset. Create one from **Assets ▸ Create ▸ Hardening ▸ Settings**; `HardeningSettings.Current` falls back to safe
  defaults if none exists.
- **Ship packaging & hygiene** (`ShipPackager`, `ShipHygieneBuildStep`) — builds the distributable zip from an
  allowlist and refuses to package development, test-menu or symbol-leaking builds.
- **Integrity hashing** (`IntegrityHashing`) — FNV-1a-64 whole-file and per-chunk digests of the shipped binaries.
- **Watermark** (`WatermarkBuildStep`) — per-build HMAC watermark compiled into the game and native core.
- **Build guards** (`TestMenusBuildOption`) — scripting-define toggle for the in-build test menus.

## Still in the game (to be extracted next)

The runtime integrity/licence code (`IntegrityGuard`, `LanLicence`, the `IHardenedSession` seam) and the native-core
wrapper, plus the Cloud Code module and native sources (which will become `Samples~`). See the project's
`hardening-package` notes for the staged plan.

## Namespaces

Files keep their original `MarketShopandRetailSystem.Multiplayer*` namespaces during extraction to avoid churn in the
game; they will be renamed to a package namespace when the package moves to its own repository.
