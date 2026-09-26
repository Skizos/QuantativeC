#!/usr/bin/env pwsh
# Runs the qa command-line tool from this repository, building it first when the sources are newer than the
# built executable (e.g. after git pull). Examples, from the repository folder:
#   .\qa history import ERIC-B
#   .\qa backtest run --strategy ma-cross --param fast=20 --param slow=100 --synthetic 50x2520
# To type just "qa" from any folder, add this line to your PowerShell profile (notepad $PROFILE):
#   function qa { & 'C:\path\to\QuantativeC\qa.ps1' @args }
# qa runs with the repository as its working folder, so data\, config\ and research\ are found from anywhere;
# relative paths you pass (e.g. --store) are therefore relative to the repository.
# The native library comes from CMake (.\build.ps1 or docs/setup.md); this script does not rebuild it.

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src/QuantAnalyst.Cli'
$exe = Join-Path $project ('bin/Debug/net10.0/' + ($IsWindows ? 'qa.exe' : 'qa'))

function Test-Stale {
    if (-not (Test-Path $exe)) { return $true }
    $built = (Get-Item $exe).LastWriteTimeUtc
    $newer = Get-ChildItem -Path (Join-Path $repo 'src') -Recurse -File -Include '*.cs', '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $built } |
        Select-Object -First 1
    return $null -ne $newer
}

if (Test-Stale) {
    [Console]::Error.WriteLine('qa: building src/QuantAnalyst.Cli ...')
    $log = dotnet build $project --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failed = $LASTEXITCODE
        $log | Out-Host
        [Console]::Error.WriteLine("qa: build failed (exit $failed).")
        exit $failed
    }
}

Push-Location $repo
try {
    & $exe @args
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $code
