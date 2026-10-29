<#
  Example build for hardening_core.dll using the xollvm (LLVM) obfuscation pass.

  Flow: compile the C++ to LLVM IR (keeping obfuscation annotations), run
  `opt -passes=obfuscation` over it, then compile the obfuscated IR to a DLL.

  This is a generic template. A shipping build should ALSO:
    * verify each requested obfuscation pass actually transformed the code (xollvm can silently
      skip an unknown pass, or drop one on a function another pass crowded out), and
    * generate any secret-derived headers (watermark, licence public key) from environment
      variables immediately before compiling, so no secret is ever committed.

  Prereq: the xollvm prebuilt toolchain (clang + opt with the obfuscator built in). Point
  -ToolchainBin at its bin folder.

  Example:
    ./build.ps1 -ToolchainBin "C:\tools\xollvm\bin" -Seed 1337
#>
param(
    [Parameter(Mandatory=$true)][string]$ToolchainBin,
    [int]$Seed = 1337,
    # Where Unity should pick up the DLL. Set to your project's plugin folder for your platform.
    [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $PSScriptRoot "Build" }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

$clang = Join-Path $ToolchainBin "clang++.exe"
$opt   = Join-Path $ToolchainBin "opt.exe"
foreach ($t in @($clang, $opt)) { if (-not (Test-Path $t)) { throw "Not found: $t" } }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$src   = Join-Path $PSScriptRoot "hardening_core.cpp"
$llRaw = Join-Path $PSScriptRoot "hardening_core.ll"
$llObf = Join-Path $PSScriptRoot "hardening_core.obf.ll"
$dll   = Join-Path $OutDir       "hardening_core.dll"

function Invoke-Step([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

# 1) C++ -> LLVM IR (annotations preserved).
Invoke-Step "compile to IR" { & $clang -O2 -S -emit-llvm -o $llRaw $src }

# 2) Obfuscate the IR. Choose the passes your toolchain supports; -obf-seed makes builds reproducible.
Invoke-Step "obfuscate IR" { & $opt -passes=obfuscation "-obf-seed=$Seed" -S -o $llObf $llRaw }

# 3) Obfuscated IR -> DLL.
Invoke-Step "link DLL" { & $clang -shared -o $dll $llObf }

Write-Host "Built $dll"
Write-Host "In Unity, set the plug-in's import settings to the matching Standalone platform."
