#!/usr/bin/env pwsh
# Starts the QuantAnalyst Windows app (docs/guide.md, "The Windows app"), building what is out of date first:
#   - the native library and the qa command-line tool, through .\qa.ps1 (which also prints the status once)
#   - the app itself when one of its sources changed (a .cs, .xaml or .csproj file under src\)
#   - the copy of the native library next to the app
# Then it opens the window and returns; the window keeps running on its own.
#
#   .\qa-app.ps1              build if needed and open the app
#   .\qa-app.ps1 -Shortcut    also put a "QuantAnalyst" shortcut on your desktop (double-click it from then on)
#   .\qa-app.ps1 -NoLaunch    build only
param(
    [switch]$Shortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$project = Join-Path $repo 'src/QuantAnalyst.Desktop'
$output = Join-Path $project 'bin/Debug/net10.0-windows'
$app = Join-Path $output ($IsWindows ? 'QuantAnalyst.exe' : 'QuantAnalyst.dll')

function New-DesktopShortcut {
    if (-not $IsWindows) { throw 'The desktop shortcut is for Windows.' }
    $pwsh = (Get-Process -Id $PID).Path
    $link = Join-Path ([Environment]::GetFolderPath('Desktop')) 'QuantAnalyst.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($link)
    $lnk.TargetPath = $pwsh
    $lnk.Arguments = "-NoProfile -File `"$PSCommandPath`""
    $lnk.WorkingDirectory = $repo
    $lnk.Description = 'QuantAnalyst: builds what changed, then opens the app'
    if (Test-Path $app) { $lnk.IconLocation = "$app,0" }
    $lnk.Save()
    Write-Host "Shortcut created: $link"
}

function Test-AppStale {
    if (-not (Test-Path $app)) { return $true }
    $built = (Get-Item $app).LastWriteTimeUtc
    $newer = Get-ChildItem -Path (Join-Path $repo 'src') -Recurse -File -Include '*.cs', '*.xaml', '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $built } |
        Select-Object -First 1
    return $null -ne $newer
}

# The app loads the native library from runtimes/<rid>/native next to it: keep that copy identical to the staged
# build, like qa.ps1 does for qa.
function Sync-NativeCopy {
    $staged = Get-ChildItem -Path (Join-Path $repo 'artifacts/native') -Recurse -File -Include 'qe.dll', 'libqe.so', 'libqe.dylib' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $staged) { return }
    $target = Join-Path $output ('runtimes/' + $staged.Directory.Name + '/native/' + $staged.Name)
    if ((Test-Path $target) -and (Get-FileHash $target).Hash -eq (Get-FileHash $staged.FullName).Hash) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -Force $staged.FullName $target
    [Console]::Error.WriteLine("qa-app: updated the native library next to the app ($($staged.Name)).")
}

# 1. Native library and qa (rebuilt by qa.ps1 when their sources changed); the status is printed on the way.
& (Join-Path $repo 'qa.ps1') status
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine("qa-app: stopped, qa could not be built or run (exit $LASTEXITCODE).")
    exit $LASTEXITCODE
}

# 2. The app.
if (Test-AppStale) {
    [Console]::Error.WriteLine('qa-app: building the app (src/QuantAnalyst.Desktop) ...')
    $log = dotnet build $project --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failed = $LASTEXITCODE
        $log | Out-Host
        [Console]::Error.WriteLine("qa-app: build failed (exit $failed).")
        exit $failed
    }
}

# 3. The native library next to it.
Sync-NativeCopy

if ($Shortcut) { New-DesktopShortcut }
if ($NoLaunch) { exit 0 }
if (-not $IsWindows) {
    [Console]::Error.WriteLine('qa-app: the app is a Windows program; it was built but not started.')
    exit 0
}

Start-Process -FilePath $app -ArgumentList "`"$repo`"" -WorkingDirectory $repo
Write-Host 'QuantAnalyst is starting in its own window.'
