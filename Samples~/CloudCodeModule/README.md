# Integrity Cloud Code module (example)

A generic skeleton of the server half of the integrity/licence system: the UGS Cloud Code module
the clients (`IntegrityGuard` / `LanLicence`) report to. The endpoint names, parameters and return
shapes match the client; the bodies are outlined, not a real implementation.

## Layout
- `Cloud/IntegrityModule.cs` — the four endpoints: `IntegrityChallenge`, `ValidateIntegrity`,
  `VerifyPass`, `IssueLanLicence`, with the return shapes the client expects and `// TODO` outlines
  for the project-specific validation.
- `Cloud/ChallengeDigest.cs` — the (non-secret, documented) server-side FNV digest + salt parsing
  the module uses to recompute what a clean client would report.
- `Cloud/PassKey.cs`, `Cloud/LicenceKey.cs` — key *holders*, split `partial` so the module compiles
  before any key exists. The real bytes come from a generated partial written from an environment
  variable and are never committed.
- `Cloud/ModuleSetup.cs`, `Cloud/IntegrityModule.asmdef`, `IntegrityModule.ccmu` — module wiring.
- `Client/` — the generated client binding (a thin `[CloudModuleClient]` partial the Cloud Code
  source generator fills from the module).

## Where the real strength is
The security does **not** depend on this logic being hidden. It depends on:
- the **signing keys** (PassKey / LicenceKey) staying secret — in environment variables, never in the
  repo, with only the licence **public** key compiled into the client's native core;
- the server comparing reports against **expected per-build baselines** it trusts (which you generate
  and deploy per release, not ship in source).

Fill in the `// TODO` bodies privately for your game, set the key environment variables, generate the
baselines, and deploy the module to your UGS environment.
