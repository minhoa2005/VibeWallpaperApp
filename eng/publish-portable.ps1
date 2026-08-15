#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = Join-Path $repo 'artifacts\portable\VibeWallpaper'
$project = Join-Path $repo 'src\VibeWallpaper.App\VibeWallpaper.App.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$targetFramework = @(
    $projectXml.Project.PropertyGroup.TargetFramework |
        ForEach-Object { [string]$_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "TargetFramework was not found in $project"
}

if (Test-Path -LiteralPath $target) {
    $resolved = (Resolve-Path -LiteralPath $target).Path
    if ($resolved -ne [IO.Path]::GetFullPath($target)) { throw "Refusing to clear unexpected target: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:DebugType=None -o $target
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$projectDirectory = Split-Path -Parent $project
$compiledResourceDirectory = Join-Path $projectDirectory "bin\x64\$Configuration\$targetFramework\win-x64"
$pri = Join-Path $compiledResourceDirectory 'VibeWallpaper.App.pri'
if (-not (Test-Path -LiteralPath $pri -PathType Leaf)) {
    throw "Compiled WinUI resource index was not found: $pri"
}

$compiledXaml = @('App.xbf','MainWindow.xbf')
Copy-Item -LiteralPath $pri -Destination $target -Force
foreach ($name in $compiledXaml) {
    $source = Join-Path $compiledResourceDirectory $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Compiled WinUI resource was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination $target -Force
}
Write-Host "Published portable artifact to $target"
