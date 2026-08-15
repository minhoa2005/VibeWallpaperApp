#requires -Version 5.1
[CmdletBinding()]
param([string]$Artifact = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($Artifact)) { $Artifact = Join-Path $repo 'artifacts\portable\VibeWallpaper' }
$root = (Resolve-Path -LiteralPath $Artifact).Path
$rootRequired = @(
    'VibeWallpaper.App.exe',
    'VibeWallpaper.App.dll',
    'VibeWallpaper.App.pri',
    'App.xbf',
    'MainWindow.xbf',
    'VibeWallpaper.Engine.dll',
    'WebView2Loader.dll'
)
$recursiveRequired = @(
    'libvlc.dll',
    'libvlccore.dll'
)
foreach ($name in $rootRequired) {
    $path = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing portable root dependency: $name"
    }
}
foreach ($name in $recursiveRequired) {
    $match = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $name | Select-Object -First 1
    if ($null -eq $match) { throw "Missing portable dependency: $name" }
}
if (Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'msedgewebview2.exe') { throw 'The Evergreen WebView2 browser must not be bundled.' }
Write-Host "Portable artifact verified: $root"
