# Native core (example)

A generic template for the native plug-in behind `INativeCore`. It exports the ABI the
**SeamAdapters** sample (`ExampleNativeCore.cs`) P/Invokes and implements only the non-secret parts.

- `hardening_core.cpp` — the exported ABI: `hc_abi_version`, `hc_hash_region`, `hc_challenge_digest`,
  `hc_text_digest`, `hc_tamper_scan`, `hc_device_id`, `hc_licence_check`. The two digests are real
  (documented FNV-1a-64); the tamper scan, device id and licence check are **stubs** with notes on
  what a shipping core does.
- `build.ps1` — compiles to LLVM IR, runs the xollvm obfuscation pass, and links the DLL.

## What a shipping core adds (and why it's not here)
The value of a native core is that its anti-tamper logic is **not readable** in the shipped binary —
that's what the LLVM obfuscation is for. Publishing a real core's source would hand an attacker the
exact techniques, so this template deliberately omits them:

- **Tamper scan** — debugger/hook/loaded-module checks, one bit per finding.
- **Device id** — derived from durable hardware identifiers.
- **Licence check** — ECDSA (P-256) verification with only the **public** key compiled in; the
  signing key stays server-side.
- **`.text` digest** — hashing a module's mapped code section via its PE/ELF headers.
- Any secret-derived markers (watermark), generated from environment variables at build time and
  never committed.

Fill these in privately for your game, keep the keys in environment variables, and obfuscate the
build. Rename the DLL and exports to suit, and point your `HardeningSettings` at them.
