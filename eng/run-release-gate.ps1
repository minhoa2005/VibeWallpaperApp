#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$BaselineDirectory = 'artifacts\performance\plan1-4k60-one-monitor',
    [switch]$BaselineValidationOnly,
    [switch]$Exploratory
)
$ErrorActionPreference = 'Stop'

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Label
    )

    Write-Host "==> $Label"
    $outputLines = New-Object 'System.Collections.Generic.List[string]'
    & $FilePath @Arguments 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $outputLines.Add($line) | Out-Null
        Write-Host $line
    }

    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = $outputLines
    }
}

function Get-TestSummary {
    param([Parameter(Mandatory)][System.Collections.Generic.List[string]]$OutputLines)

    $summaryLine = $OutputLines |
        Where-Object { $_ -match 'Failed:\s*\d+,\s*Passed:\s*\d+,\s*Skipped:\s*\d+,\s*Total:\s*\d+' } |
        Select-Object -Last 1

    if ($null -eq $summaryLine) {
        return $null
    }

    $match = [regex]::Match(
        $summaryLine,
        'Failed:\s*(?<Failed>\d+),\s*Passed:\s*(?<Passed>\d+),\s*Skipped:\s*(?<Skipped>\d+),\s*Total:\s*(?<Total>\d+)')

    if (-not $match.Success) {
        return $null
    }

    return [pscustomobject]@{
        Failed  = [int]$match.Groups['Failed'].Value
        Passed  = [int]$match.Groups['Passed'].Value
        Skipped = [int]$match.Groups['Skipped'].Value
        Total   = [int]$match.Groups['Total'].Value
    }
}

function Assert-BaselineArtifact {
    param(
        [Parameter(Mandatory)][string]$DirectoryPath,
        [bool]$ExploratoryMode = $false
    )

    $resolvedDirectory = [System.IO.Path]::GetFullPath($DirectoryPath)
    $samplesPath = Join-Path $resolvedDirectory 'samples.csv'
    $summaryPath = Join-Path $resolvedDirectory 'summary.json'

    if (-not (Test-Path -LiteralPath $resolvedDirectory -PathType Container)) {
        throw "Plan 1 manual-evidence blocker: baseline directory '$resolvedDirectory' is missing. Run the five-minute baseline capture and preserve samples.csv plus summary.json before treating the release gate as passed."
    }

    if (-not (Test-Path -LiteralPath $samplesPath -PathType Leaf)) {
        throw "Plan 1 manual-evidence blocker: '$samplesPath' is missing. Baseline evidence is incomplete."
    }

    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        throw "Plan 1 manual-evidence blocker: '$summaryPath' is missing. Baseline evidence is incomplete."
    }

    $samplesItem = Get-Item -LiteralPath $samplesPath
    $summaryItem = Get-Item -LiteralPath $summaryPath

    if ($samplesItem.Length -le 0) {
        throw "Plan 1 manual-evidence blocker: '$samplesPath' is empty."
    }

    if ($summaryItem.Length -le 0) {
        throw "Plan 1 manual-evidence blocker: '$summaryPath' is empty."
    }

    $sampleRows = @(Import-Csv -LiteralPath $samplesPath)
    if ($sampleRows.Count -le 0) {
        throw "Plan 1 manual-evidence blocker: '$samplesPath' does not contain any samples."
    }

    $requiredColumns = @(
        'TimestampUtc',
        'ElapsedSeconds',
        'PrivateBytes',
        'WorkingSetBytes',
        'HandleCount',
        'ThreadCount',
        'CpuPercent',
        'GpuVideoDecodePercent',
        'Gpu3DPercent',
        'GpuCountersAvailable',
        'GpuSampleAgeMilliseconds'
    )
    $actualColumns = @($sampleRows[0].PSObject.Properties.Name)
    foreach ($requiredColumn in $requiredColumns) {
        if ($actualColumns -notcontains $requiredColumn) {
            throw "Plan 1 manual-evidence blocker: '$samplesPath' is missing required sampler column '$requiredColumn'."
        }
    }

    try {
        $summary = Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Plan 1 manual-evidence blocker: '$summaryPath' is not valid JSON."
    }

    if ($null -eq $summary.SampleCount -or [int]$summary.SampleCount -le 0) {
        throw "Plan 1 manual-evidence blocker: '$summaryPath' does not report a positive SampleCount."
    }

    $releaseGradeTiming = $true

    if ([int]$summary.SampleCount -ne $sampleRows.Count) {
        throw "Plan 1 manual-evidence blocker: summary SampleCount $($summary.SampleCount) does not agree with the $($sampleRows.Count) CSV samples."
    }

    if ($null -eq $summary.ProcessSampleCount -or [int]$summary.ProcessSampleCount -ne $sampleRows.Count) {
        throw "Plan 1 manual-evidence blocker: summary ProcessSampleCount $($summary.ProcessSampleCount) does not agree with the $($sampleRows.Count) CSV samples."
    }

    if ([string]$summary.Scenario -cne '4k60-one-monitor') {
        throw "Plan 1 manual-evidence blocker: summary Scenario must be '4k60-one-monitor', but was '$($summary.Scenario)'."
    }

    if ($null -eq $summary.DurationSecondsRequested -or [double]$summary.DurationSecondsRequested -lt 300) {
        throw "Plan 1 manual-evidence blocker: summary DurationSecondsRequested must be at least 300 seconds."
    }

    $durationSecondsRequested = [double]$summary.DurationSecondsRequested
    if ($null -eq $summary.IntervalSeconds -or [double]$summary.IntervalSeconds -le 0) {
        throw "Plan 1 manual-evidence blocker: summary IntervalSeconds must be positive."
    }

    $intervalSeconds = [double]$summary.IntervalSeconds
    if ($intervalSeconds -ne 1.0) {
        $releaseGradeTiming = $false
        if (-not $ExploratoryMode) {
            throw "Plan 1 manual-evidence blocker: summary IntervalSeconds must be exactly 1 second for release-grade baseline evidence."
        }
    }

    if ($null -eq $summary.ObservedDurationSeconds -or [double]$summary.ObservedDurationSeconds -lt $durationSecondsRequested) {
        throw "Plan 1 manual-evidence blocker: summary ObservedDurationSeconds must be at least DurationSecondsRequested."
    }

    $minimumProcessSamples = [math]::Floor($durationSecondsRequested / $intervalSeconds)
    if ([int]$summary.ProcessSampleCount -lt $minimumProcessSamples) {
        throw "Plan 1 manual-evidence blocker: summary ProcessSampleCount $($summary.ProcessSampleCount) is below the required floor $minimumProcessSamples."
    }

    $maximumAllowedGapSeconds = 2.0 * $intervalSeconds
    if ($null -eq $summary.MaximumProcessSampleGapSeconds -or [double]$summary.MaximumProcessSampleGapSeconds -gt $maximumAllowedGapSeconds) {
        throw "Plan 1 manual-evidence blocker: summary MaximumProcessSampleGapSeconds must be no greater than $maximumAllowedGapSeconds seconds."
    }

    $firstElapsed = [double]$sampleRows[0].ElapsedSeconds
    $lastElapsed = [double]$sampleRows[$sampleRows.Count - 1].ElapsedSeconds
    $elapsedCaptureSpan = $lastElapsed - $firstElapsed
    if ($elapsedCaptureSpan -lt $durationSecondsRequested) {
        throw "Plan 1 manual-evidence blocker: CSV elapsed capture span is $elapsedCaptureSpan seconds; the requested protocol requires at least $durationSecondsRequested seconds."
    }

    $maximumGpuSampleAgeMilliseconds = 1.5 * $intervalSeconds * 1000.0
    $releaseGradeGpuEvidence = $true
    if ($null -eq $summary.GpuCountersAvailable -or -not [bool]$summary.GpuCountersAvailable) {
        $releaseGradeGpuEvidence = $false
        if (-not $ExploratoryMode) {
            throw "Plan 1 manual-evidence blocker: summary GpuCountersAvailable must be true for hardware release evidence."
        }
    }
    elseif ($null -eq $summary.GpuSampleCount -or [int]$summary.GpuSampleCount -lt $minimumProcessSamples) {
        $releaseGradeGpuEvidence = $false
        if (-not $ExploratoryMode) {
            throw "Plan 1 manual-evidence blocker: summary GpuSampleCount must be at least $minimumProcessSamples for release-grade hardware evidence."
        }
    }

    foreach ($sampleRow in $sampleRows) {
        if ([string]$sampleRow.GpuCountersAvailable -cne 'True') {
            continue
        }

        if ([string]::IsNullOrWhiteSpace([string]$sampleRow.GpuSampleAgeMilliseconds)) {
            throw "Plan 1 manual-evidence blocker: CSV GpuSampleAgeMilliseconds is missing for an available GPU sample."
        }

        $gpuSampleAgeMilliseconds = [double]$sampleRow.GpuSampleAgeMilliseconds
        if ($gpuSampleAgeMilliseconds -gt $maximumGpuSampleAgeMilliseconds) {
            throw "Plan 1 manual-evidence blocker: CSV GpuSampleAgeMilliseconds $gpuSampleAgeMilliseconds exceeds the maximum merged age $maximumGpuSampleAgeMilliseconds."
        }
    }

    try {
        $startedAtUtc = [DateTimeOffset]::Parse([string]$summary.StartedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
        $completedAtUtc = [DateTimeOffset]::Parse([string]$summary.CompletedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "Plan 1 manual-evidence blocker: summary StartedAtUtc and CompletedAtUtc must be valid timestamps."
    }

    if (($completedAtUtc - $startedAtUtc).TotalSeconds -lt $durationSecondsRequested) {
        throw "Plan 1 manual-evidence blocker: summary capture span is shorter than the five-minute protocol."
    }

    Write-Host "Verified Plan 1 baseline artifact: $resolvedDirectory"
    return [pscustomobject]@{
        GpuCountersAvailable   = $null -ne $summary.GpuCountersAvailable -and [bool]$summary.GpuCountersAvailable
        ReleaseGradeTiming     = $releaseGradeTiming
        ReleaseGradeGpuEvidence = $releaseGradeGpuEvidence
    }
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    if ($BaselineValidationOnly) {
        Assert-BaselineArtifact -DirectoryPath $BaselineDirectory -ExploratoryMode:$Exploratory.IsPresent | Out-Null
        return
    }

    dotnet restore VibeWallpaper.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build VibeWallpaper.sln -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    $correctnessResult = Invoke-ExternalCommand `
        -FilePath 'dotnet' `
        -Arguments @(
            'test',
            'tests\VibeWallpaper.Tests\VibeWallpaper.Tests.csproj',
            '-c',
            'Release',
            '--no-restore',
            '--nologo'
        ) `
        -Label 'Correctness test suite'
    if ($correctnessResult.ExitCode -ne 0) {
        throw 'Release correctness tests failed.'
    }

    $libVlcResult = Invoke-ExternalCommand `
        -FilePath 'dotnet' `
        -Arguments @(
            'test',
            'tests\VibeWallpaper.Tests\VibeWallpaper.Tests.csproj',
            '-c',
            'Release',
            '--no-restore',
            '--nologo',
            '--',
            '--filter-trait',
            'Category=LibVLCIntegration'
        ) `
        -Label 'LibVLC integration suite'
    $libVlcSummary = Get-TestSummary -OutputLines $libVlcResult.Output

    if ($libVlcResult.ExitCode -ne 0) {
        throw 'LibVLC integration tests failed.'
    }

    if ($null -eq $libVlcSummary) {
        throw 'LibVLC integration suite did not emit a parseable summary; release evidence is incomplete.'
    }

    if ($libVlcSummary.Total -le 0) {
        throw 'LibVLC integration suite executed zero tests; missing LibVLC runtime or hardware evidence.'
    }

    if ($libVlcSummary.Skipped -gt 0) {
        throw "LibVLC integration suite skipped $($libVlcSummary.Skipped) test(s); missing LibVLC runtime or hardware evidence."
    }

    & (Join-Path $PSScriptRoot 'publish-portable.ps1') -Configuration Release
    & (Join-Path $PSScriptRoot 'verify-portable.ps1')
    $baselineEvidence = Assert-BaselineArtifact -DirectoryPath $BaselineDirectory -ExploratoryMode:$Exploratory.IsPresent
    if ($Exploratory -and (-not $baselineEvidence.ReleaseGradeGpuEvidence -or -not $baselineEvidence.ReleaseGradeTiming)) {
        Write-Host 'Release gate completed in exploratory mode; release-grade timing or GPU evidence was unavailable, so release did not pass.'
    }
    else {
        Write-Host 'Release gate passed.'
    }
}
finally { Pop-Location }
