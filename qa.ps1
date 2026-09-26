#!/usr/bin/env pwsh
# Runs the qa command-line tool from this repository, building what is out of date first (e.g. after git pull):
#   - the native library (qe) when its C++ sources or CMake files changed: build.ps1 -NoManaged (a few minutes the
#     first time; later only what changed), which also runs the native tests
#   - the .NET CLI when a .cs/.csproj file changed
#   - the copy of the native library next to qa whenever it differs from the freshly built one
# Examples, from the repository folder:
#   .\qa history import ERIC-B
#   .\qa backtest run --strategy ma-cross --param fast=20 --param slow=100 --synthetic 50x2520
# To type just "qa" from any folder, add this line to your PowerShell profile (notepad $PROFILE):
#   function qa { & 'C:\path\to\QuantativeC\qa.ps1' @args }
# qa runs with the repository as its working folder, so data\, config\ and research\ are found from anywhere;
# relative paths you pass (e.g. --store) are therefore relative to the repository.

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src/QuantAnalyst.Cli'
$output = Join-Path $project 'bin/Debug/net10.0'
$exe = Join-Path $output ($IsWindows ? 'qa.exe' : 'qa')
$staging = Join-Path $repo 'artifacts/native'
$stamp = Join-Path $staging 'qa-native-built.stamp'
$libraryNames = @('qe.dll', 'libqe.so', 'libqe.dylib')

function Get-NewestSource([string[]]$roots, [string[]]$include) {
    Get-ChildItem -Path $roots -Recurse -File -Include $include -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|build|out|artifacts|vcpkg_installed)[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-StagedLibrary {
    Get-ChildItem -Path $staging -Recurse -File -Include $libraryNames -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Test-NativeStale {
    $staged = Get-StagedLibrary
    if (-not $staged) { return $true }
    # The stamp is written after every successful native build: CMake copies the library only when it changed,
    # so the library's own time can stay older than sources that were touched without changing it.
    $built = $staged.LastWriteTimeUtc
    if ((Test-Path $stamp) -and (Get-Item -Force $stamp).LastWriteTimeUtc -gt $built) { $built = (Get-Item -Force $stamp).LastWriteTimeUtc }
    $sources = @(Get-NewestSource @((Join-Path $repo 'native')) @('*.cpp', '*.cc', '*.c', '*.h', '*.hpp', 'CMakeLists.txt'))
    $sources += @('CMakeLists.txt', 'CMakePresets.json', 'vcpkg.json') | ForEach-Object { Join-Path $repo $_ } |
        Where-Object { Test-Path $_ } | ForEach-Object { Get-Item -Force $_ }
    return $null -ne ($sources | Where-Object { $_ -and $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1)
}

function Test-ManagedStale {
    if (-not (Test-Path $exe)) { return $true }
    $built = (Get-Item $exe).LastWriteTimeUtc
    $newer = Get-ChildItem -Path (Join-Path $repo 'src') -Recurse -File -Include '*.cs', '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $built } |
        Select-Object -First 1
    return $null -ne $newer
}

# qa loads the native library from runtimes/<rid>/native next to it. Keep that copy identical to the staged build
# (MSBuild copies it only when the staged file is newer, which misses a rebuilt-but-older or swapped library).
function Sync-NativeCopy {
    $staged = Get-StagedLibrary
    if (-not $staged) { return }
    $target = Join-Path $output ('runtimes/' + $staged.Directory.Name + '/native/' + $staged.Name)
    if ((Test-Path $target) -and (Get-FileHash $target).Hash -eq (Get-FileHash $staged.FullName).Hash) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -Force $staged.FullName $target
    [Console]::Error.WriteLine("qa: updated the native library next to qa ($($staged.Name)).")
}

if ($env:QA_SKIP_NATIVE_BUILD -ne '1' -and (Test-NativeStale)) {
    $preset = $IsWindows ? 'msvc-dev' : 'dev'
    [Console]::Error.WriteLine("qa: the native library is missing or older than its sources; building it (build.ps1 -NoManaged -Preset $preset) ...")
    Push-Location $repo # build.ps1 changes the location; keep the caller's
    try {
        & (Join-Path $repo 'build.ps1') -NoManaged -Preset $preset *> $null
        New-Item -ItemType File -Force -Path $stamp | Out-Null
    }
    catch {
        [Console]::Error.WriteLine("qa: native build failed: $($_.Exception.Message)")
        [Console]::Error.WriteLine("qa: run .\build.ps1 -NoManaged to see the full output (docs/setup.md). Set QA_SKIP_NATIVE_BUILD=1 to run without rebuilding.")
        exit 1
    }
    finally {
        Pop-Location
    }
}

if (Test-ManagedStale) {
    [Console]::Error.WriteLine('qa: building src/QuantAnalyst.Cli ...')
    $log = dotnet build $project --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failed = $LASTEXITCODE
        $log | Out-Host
        [Console]::Error.WriteLine("qa: build failed (exit $failed).")
        exit $failed
    }
}

Sync-NativeCopy

Push-Location $repo
try {
    & $exe @args
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $code
