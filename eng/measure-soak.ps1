#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1, 10000)][int]$RaceIterations = 100,
    [switch]$MeasureResources,
    [int]$ProcessId,
    [ValidateRange(10, 28800)][int]$DurationSeconds = 300,
    [ValidateRange(1, 10)][int]$IntervalSeconds = 1,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ($MeasureResources -and -not $PSBoundParameters.ContainsKey('ProcessId')) {
    throw 'ProcessId is mandatory when MeasureResources is specified.'
}

if (-not $MeasureResources -and $PSBoundParameters.ContainsKey('ProcessId')) {
    throw 'ProcessId may only be supplied when MeasureResources is specified.'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$resolvedOutputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [System.IO.Path]::GetFullPath((Join-Path $repo ("artifacts\measure-soak\{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))))
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$raceLogPath = Join-Path $resolvedOutputDirectory 'race-tests.log'
$raceSummaryPath = Join-Path $resolvedOutputDirectory 'race-summary.json'
$testProject = Join-Path $repo 'tests\VibeWallpaper.Tests\VibeWallpaper.Tests.csproj'
$raceStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

for ($i = 1; $i -le $RaceIterations; $i++) {
    Add-Content -Path $raceLogPath -Value ("[{0}] Race iteration {1} of {2}" -f (Get-Date -Format o), $i, $RaceIterations)
    & dotnet test $testProject --no-restore --nologo -- --filter-class '*Race*' 2>&1 | Tee-Object -FilePath $raceLogPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Race iteration $i failed. See $raceLogPath."
    }
}
$raceStopwatch.Stop()

$raceSummary = [pscustomobject]@{
    Iterations      = $RaceIterations
    DurationSeconds = [math]::Round($raceStopwatch.Elapsed.TotalSeconds, 2)
    LogPath         = $raceLogPath
}
$raceSummary | ConvertTo-Json -Depth 4 | Set-Content -Path $raceSummaryPath

$resourceDirectory = $null
if ($MeasureResources) {
    $resourceDirectory = Join-Path $resolvedOutputDirectory 'resources'
    & (Join-Path $PSScriptRoot 'measure-video-performance.ps1') `
        -ProcessId $ProcessId `
        -DurationSeconds $DurationSeconds `
        -IntervalSeconds $IntervalSeconds `
        -OutputDirectory $resourceDirectory `
        -Scenario 'measure-soak'
}

[pscustomobject]@{
    RaceLogPath          = $raceLogPath
    RaceSummaryPath      = $raceSummaryPath
    ResourceArtifactsPath = $resourceDirectory
    OutputDirectory      = $resolvedOutputDirectory
} | Format-List
