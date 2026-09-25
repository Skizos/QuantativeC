#Requires -Version 7.2
<#
.SYNOPSIS
    One-command build + test on Windows (CLAUDE.md "Commands: All").

.DESCRIPTION
    Enters a Visual Studio x64 developer environment when cl.exe is not on PATH, then runs:
      native:  cmake --preset <Preset>; cmake --build --preset <Preset>; ctest --preset <Preset>
      managed: dotnet build QuantAnalyst.sln; dotnet test --solution QuantAnalyst.sln
    plus the guardrail hook self-test (needs Git Bash) and, optionally, format checks/benchmarks.

.EXAMPLE
    ./build.ps1                      # msvc-dev
    ./build.ps1 -Preset msvc-release -Check
    ./build.ps1 -Bench
#>
[CmdletBinding()]
param(
    [ValidateSet('msvc-dev', 'msvc-release', 'dev', 'release')]
    [string]$Preset = 'msvc-dev',
    [switch]$NoNative,
    [switch]$NoManaged,
    [switch]$Check,
    [switch]$Bench
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Invoke-Step {
    param([string]$Name, [scriptblock]$Command)
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

function Enter-VsDevEnvironment {
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) { return }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'cl.exe is not on PATH and vswhere.exe was not found. Install Visual Studio 2022 17.10+ with "Desktop development with C++" (docs/setup.md).'
    }
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) { throw 'No Visual Studio installation with the MSVC x64 toolset was found.' }
    Import-Module (Join-Path $vsPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
    Set-Location -Path $PSScriptRoot
}

$useVcpkg = -not ($env:QE_USE_VCPKG -and $env:QE_USE_VCPKG -match '^(OFF|0|FALSE|NO)$')
if ($useVcpkg -and -not $env:VCPKG_ROOT) {
    throw 'VCPKG_ROOT is not set. Clone and bootstrap vcpkg (docs/setup.md), or set QE_USE_VCPKG=OFF to use preinstalled packages.'
}

if (Get-Command bash -ErrorAction SilentlyContinue) {
    Invoke-Step 'Guardrail self-test (.claude/hooks/block-live-trading.sh)' { bash tests/hooks/block-live-trading.test.sh | Out-Null }
}
else {
    Write-Warning 'bash (Git Bash) not found: skipping the guardrail hook self-test. Claude Code on Windows needs Git Bash anyway.'
}

if (-not $NoNative) {
    if ($IsWindows -and $Preset -like 'msvc-*') { Enter-VsDevEnvironment }
    Invoke-Step "Native: cmake --preset $Preset" { cmake --preset $Preset }
    Invoke-Step "Native: build $Preset" { cmake --build --preset $Preset }
    Invoke-Step "Native: ctest $Preset" { ctest --preset $Preset --output-on-failure }
}

if (-not $NoManaged) {
    Invoke-Step 'Managed: dotnet build' { dotnet build QuantAnalyst.sln }
    Invoke-Step 'Managed: dotnet test' { dotnet test --solution QuantAnalyst.sln }
}

if ($Check) {
    Invoke-Step 'Format: dotnet format' { dotnet format QuantAnalyst.sln --verify-no-changes -v q }
    $cppFiles = git ls-files -- '*.c' '*.cc' '*.cpp' '*.h' '*.hpp'
    if (Get-Command clang-format -ErrorAction SilentlyContinue) {
        Invoke-Step 'Format: clang-format' { clang-format --dry-run -Werror @cppFiles }
    }
    else {
        Write-Warning 'clang-format not found: skipping the C++ format check.'
    }
}

if ($Bench) {
    $releasePreset = if ($IsWindows) { 'msvc-release' } else { 'release' }
    if ($IsWindows) { Enter-VsDevEnvironment }
    Invoke-Step "Bench: configure $releasePreset" { cmake --preset $releasePreset }
    Invoke-Step "Bench: build $releasePreset" { cmake --build --preset $releasePreset }
    $machine = "local-$($env:COMPUTERNAME ?? (hostname))-win-x64".ToLowerInvariant()
    $exe = if ($IsWindows) { 'build/msvc-release/bin/qe_bench.exe' } else { 'build/release/bin/qe_bench' }
    Invoke-Step 'Bench: native' { & $exe --benchmark_repetitions=3 --benchmark_report_aggregates_only=true "--benchmark_out=bench/results/$machine-native.json" --benchmark_out_format=json }
    Invoke-Step 'Bench: managed' { dotnet run -c Release --project bench/QuantAnalyst.Bench -- --filter '*' --job short --exporters json markdown }
}

Write-Host "`n==> Done" -ForegroundColor Green
