#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [ValidateRange(10, 28800)][int]$DurationSeconds = 300,
    [ValidateRange(1, 10)][int]$IntervalSeconds = 1,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Scenario = 'unspecified'
)

$ErrorActionPreference = 'Stop'

function Get-ExactProcess {
    param([int]$Id)

    try {
        return Get-Process -Id $Id -ErrorAction Stop
    }
    catch {
        throw "ProcessId $Id was not found."
    }
}

function Get-ProcessIdentity {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][int]$Id
    )

    try {
        return [pscustomobject]@{
            ProcessName  = [string]$Process.ProcessName
            StartTimeUtc = $Process.StartTime.ToUniversalTime()
        }
    }
    catch {
        throw "ProcessId $Id could not provide identity details."
    }
}

function Confirm-ExactProcessIdentity {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][int]$Id,
        [Parameter(Mandatory)]$ExpectedIdentity
    )

    $currentIdentity = Get-ProcessIdentity -Process $Process -Id $Id
    if ($currentIdentity.StartTimeUtc -ne $ExpectedIdentity.StartTimeUtc -or
        $currentIdentity.ProcessName -cne $ExpectedIdentity.ProcessName) {
        throw "ProcessId $Id no longer refers to the original process."
    }

    return $currentIdentity
}

function Get-MetricSummary {
    param(
        [Parameter(Mandatory)][System.Collections.IList]$Samples,
        [Parameter(Mandatory)][string]$PropertyName
    )

    $values = @($Samples | ForEach-Object { [double]($_.$PropertyName) })
    return [pscustomobject]@{
        Initial = $values[0]
        Final   = $values[$values.Count - 1]
        Minimum = ($values | Measure-Object -Minimum).Minimum
        Maximum = ($values | Measure-Object -Maximum).Maximum
        Average = [math]::Round(($values | Measure-Object -Average).Average, 4)
    }
}

function Get-PrivateBytesSlope {
    param([Parameter(Mandatory)][System.Collections.IList]$Samples)

    if ($Samples.Count -lt 2) {
        throw 'At least two samples are required to calculate a slope.'
    }

    $minutes = @($Samples | ForEach-Object { [double]$_.ElapsedSeconds / 60.0 })
    $bytes = @($Samples | ForEach-Object { [double]$_.PrivateBytes })
    $xMean = ($minutes | Measure-Object -Average).Average
    $yMean = ($bytes | Measure-Object -Average).Average
    $numerator = 0.0
    $denominator = 0.0

    for ($index = 0; $index -lt $Samples.Count; $index++) {
        $centeredMinutes = $minutes[$index] - $xMean
        $numerator += $centeredMinutes * ($bytes[$index] - $yMean)
        $denominator += $centeredMinutes * $centeredMinutes
    }

    if ($denominator -le 0) {
        throw 'Samples must span more than one elapsed timestamp.'
    }

    return [math]::Round(($numerator / $denominator), 4)
}

function New-MeasurementSummary {
    param(
        [Parameter(Mandatory)][System.Collections.IList]$Samples,
        [Parameter(Mandatory)][System.Collections.IList]$GpuSamples,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$ProcessName,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][int]$DurationSeconds,
        [Parameter(Mandatory)][int]$IntervalSeconds
    )

    $observedDurationSeconds = [double]$Samples[$Samples.Count - 1].ElapsedSeconds
    $maximumProcessSampleGapSeconds = 0.0
    for ($index = 1; $index -lt $Samples.Count; $index++) {
        $gap = [double]$Samples[$index].ElapsedSeconds - [double]$Samples[$index - 1].ElapsedSeconds
        if ($gap -gt $maximumProcessSampleGapSeconds) {
            $maximumProcessSampleGapSeconds = $gap
        }
    }

    $availableGpuSamples = @($GpuSamples | Where-Object { $_.CountersAvailable })
    $gpuCountersAvailable = $availableGpuSamples.Count -gt 0

    return [pscustomobject]@{
        Scenario                         = $Scenario
        ProcessId                        = $ProcessId
        ProcessName                      = $ProcessName
        SampleCount                      = $Samples.Count
        DurationSecondsRequested         = $DurationSeconds
        IntervalSeconds                  = $IntervalSeconds
        ObservedDurationSeconds          = [math]::Round($observedDurationSeconds, 4)
        MaximumProcessSampleGapSeconds   = [math]::Round($maximumProcessSampleGapSeconds, 4)
        ProcessSampleCount               = $Samples.Count
        GpuSampleCount                   = $availableGpuSamples.Count
        GpuCountersAvailable             = $gpuCountersAvailable
        StartedAtUtc                     = $Samples[0].TimestampUtc
        CompletedAtUtc                   = $Samples[$Samples.Count - 1].TimestampUtc
        PrivateBytesSlopeBytesPerMinute  = Get-PrivateBytesSlope -Samples $Samples
        PrivateBytes                     = Get-MetricSummary -Samples $Samples -PropertyName 'PrivateBytes'
        WorkingSetBytes                  = Get-MetricSummary -Samples $Samples -PropertyName 'WorkingSetBytes'
        HandleCount                      = Get-MetricSummary -Samples $Samples -PropertyName 'HandleCount'
        ThreadCount                      = Get-MetricSummary -Samples $Samples -PropertyName 'ThreadCount'
        CpuPercent                       = Get-MetricSummary -Samples $Samples -PropertyName 'CpuPercent'
        GpuVideoDecodePercent            = Get-MetricSummary -Samples $Samples -PropertyName 'GpuVideoDecodePercent'
        Gpu3DPercent                     = Get-MetricSummary -Samples $Samples -PropertyName 'Gpu3DPercent'
    }
}

function Start-GpuCounterJob {
    param(
        [Parameter(Mandatory)][int]$Id,
        [Parameter(Mandatory)][int]$SampleIntervalSeconds,
        [Parameter(Mandatory)][int]$MaxSamples
    )

    return Start-Job -ArgumentList $Id, $SampleIntervalSeconds, $MaxSamples -ScriptBlock {
        param(
            [int]$TargetProcessId,
            [int]$JobSampleIntervalSeconds,
            [int]$JobMaxSamples
        )

        try {
            Get-Counter '\GPU Engine(*)\Utilization Percentage' `
                -SampleInterval $JobSampleIntervalSeconds `
                -MaxSamples $JobMaxSamples `
                -ErrorAction Stop |
                ForEach-Object {
                    $counterSampleSet = $_
                    $videoDecodePercent = 0.0
                    $gpu3DPercent = 0.0
                    foreach ($counterSample in $counterSampleSet.CounterSamples) {
                        $instanceName = [string]$counterSample.InstanceName
                        if ($instanceName -notlike "*pid_$TargetProcessId*") {
                            continue
                        }

                        if ($instanceName -like '*engtype_VideoDecode*') {
                            $videoDecodePercent += [double]$counterSample.CookedValue
                            continue
                        }

                        if ($instanceName -like '*engtype_3D*') {
                            $gpu3DPercent += [double]$counterSample.CookedValue
                        }
                    }

                    [pscustomobject]@{
                        TimestampUtc       = ([DateTimeOffset]$counterSampleSet.Timestamp).UtcDateTime.ToString('o')
                        VideoDecodePercent = [math]::Round($videoDecodePercent, 2)
                        Gpu3DPercent       = [math]::Round($gpu3DPercent, 2)
                        CountersAvailable  = $true
                    }
                }
        }
        catch {
            [pscustomobject]@{
                TimestampUtc       = [DateTimeOffset]::UtcNow.UtcDateTime.ToString('o')
                VideoDecodePercent = 0.0
                Gpu3DPercent       = 0.0
                CountersAvailable  = $false
            }
        }
    }
}

function Receive-GpuCounterJob {
    param(
        $Job,
        [Parameter(Mandatory)][int]$IntervalSeconds
    )

    if ($null -eq $Job) {
        return @()
    }

    $graceSeconds = [math]::Max(5, 3 * $IntervalSeconds)
    Wait-Job -Job $Job -Timeout $graceSeconds | Out-Null
    $gpuSamples = @()
    if ($Job.State -eq 'Running') {
        $gpuSamples += @(Receive-Job -Job $Job -Keep -ErrorAction SilentlyContinue)
        Stop-Job -Job $Job | Out-Null
    }

    $gpuSamples += @(Receive-Job -Job $Job -ErrorAction SilentlyContinue)
    Remove-Job -Job $Job -Force | Out-Null
    return @(Select-UniqueGpuSamples -Samples $gpuSamples)
}

function Select-UniqueGpuSamples {
    param([object[]]$Samples)

    $seenTimestamps = @{}
    $uniqueSamples = New-Object 'System.Collections.Generic.List[object]'
    foreach ($sample in @($Samples)) {
        if ($null -eq $sample) {
            continue
        }

        $timestamp = [string]$sample.TimestampUtc
        if ([string]::IsNullOrWhiteSpace($timestamp)) {
            $uniqueSamples.Add($sample) | Out-Null
            continue
        }

        if ($seenTimestamps.ContainsKey($timestamp)) {
            continue
        }

        $seenTimestamps[$timestamp] = $true
        $uniqueSamples.Add($sample) | Out-Null
    }

    return $uniqueSamples.ToArray()
}

function Find-NearestGpuSample {
    param(
        [Parameter(Mandatory)][DateTimeOffset]$ProcessTimestamp,
        [Parameter(Mandatory)][System.Collections.IList]$GpuSamples,
        [Parameter(Mandatory)][double]$MaximumAgeSeconds
    )

    $nearestSample = $null
    $nearestAgeSeconds = [double]::MaxValue
    foreach ($gpuSample in $GpuSamples) {
        if (-not $gpuSample.CountersAvailable) {
            continue
        }

        $gpuTimestamp = [DateTimeOffset]::Parse([string]$gpuSample.TimestampUtc, [Globalization.CultureInfo]::InvariantCulture)
        $ageSeconds = [math]::Abs(($ProcessTimestamp - $gpuTimestamp).TotalSeconds)
        if ($ageSeconds -lt $nearestAgeSeconds) {
            $nearestAgeSeconds = $ageSeconds
            $nearestSample = $gpuSample
        }
    }

    if ($null -eq $nearestSample -or $nearestAgeSeconds -gt $MaximumAgeSeconds) {
        return $null
    }

    return [pscustomobject]@{
        Sample = $nearestSample
        AgeMilliseconds = [math]::Round($nearestAgeSeconds * 1000.0)
    }
}

function Merge-GpuSamples {
    param(
        [Parameter(Mandatory)][System.Collections.IList]$Samples,
        [Parameter(Mandatory)][System.Collections.IList]$GpuSamples,
        [Parameter(Mandatory)][int]$IntervalSeconds
    )

    $maximumAgeSeconds = 1.5 * $IntervalSeconds
    foreach ($sample in $Samples) {
        $processTimestamp = [DateTimeOffset]::Parse([string]$sample.TimestampUtc, [Globalization.CultureInfo]::InvariantCulture)
        $nearest = Find-NearestGpuSample -ProcessTimestamp $processTimestamp -GpuSamples $GpuSamples -MaximumAgeSeconds $maximumAgeSeconds
        if ($null -eq $nearest) {
            $sample.GpuVideoDecodePercent = 0.0
            $sample.Gpu3DPercent = 0.0
            $sample.GpuCountersAvailable = $false
            $sample.GpuSampleAgeMilliseconds = $null
            continue
        }

        $sample.GpuVideoDecodePercent = [double]$nearest.Sample.VideoDecodePercent
        $sample.Gpu3DPercent = [double]$nearest.Sample.Gpu3DPercent
        $sample.GpuCountersAvailable = $true
        $sample.GpuSampleAgeMilliseconds = [int]$nearest.AgeMilliseconds
    }
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$validatedProcess = Get-ExactProcess -Id $ProcessId
$validatedIdentity = Get-ProcessIdentity -Process $validatedProcess -Id $ProcessId
$samples = New-Object 'System.Collections.Generic.List[object]'
$previousTimestamp = $null
$previousCpuTotal = $null
$gpuSampleTarget = [math]::Max(1, [math]::Ceiling($DurationSeconds / [double]$IntervalSeconds) + 2)
$gpuJob = $null
$gpuSamples = @()
$stopwatch = $null

try {
    $gpuJob = Start-GpuCounterJob -Id $ProcessId -SampleIntervalSeconds $IntervalSeconds -MaxSamples $gpuSampleTarget
    $sampleIndex = 0
    while ($true) {
        if ($sampleIndex -gt 0) {
            if ($null -eq $stopwatch) {
                throw 'Measurement clock was not initialized.'
            }

            $deadlineSeconds = [double]$sampleIndex * $IntervalSeconds
            $sleepSeconds = $deadlineSeconds - $stopwatch.Elapsed.TotalSeconds
            if ($sleepSeconds -gt 0) {
                Start-Sleep -Milliseconds ([int][math]::Max(1, [math]::Round($sleepSeconds * 1000.0)))
            }
        }

        $sampleTimestamp = [DateTimeOffset]::UtcNow
        $process = Get-ExactProcess -Id $ProcessId
        Confirm-ExactProcessIdentity -Process $process -Id $ProcessId -ExpectedIdentity $validatedIdentity | Out-Null
        $currentCpuTotal = $process.TotalProcessorTime
        $cpuPercent = 0.0

        if ($samples.Count -gt 0) {
            $elapsedWindowSeconds = ($sampleTimestamp - $previousTimestamp).TotalSeconds
            if ($elapsedWindowSeconds -gt 0) {
                $cpuPercent =
                    (($currentCpuTotal - $previousCpuTotal).TotalMilliseconds /
                    ($elapsedWindowSeconds * 1000.0 * [Environment]::ProcessorCount)) * 100.0
            }
        }

        $elapsedSeconds = 0.0
        if ($null -eq $stopwatch) {
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        }
        else {
            $elapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        }

        $samples.Add([pscustomobject]@{
            TimestampUtc              = $sampleTimestamp.UtcDateTime.ToString('o')
            ElapsedSeconds            = [math]::Round($elapsedSeconds, 4)
            PrivateBytes              = [int64]$process.PrivateMemorySize64
            WorkingSetBytes           = [int64]$process.WorkingSet64
            HandleCount               = [int]$process.HandleCount
            ThreadCount               = [int]$process.Threads.Count
            CpuPercent                = [math]::Round($cpuPercent, 4)
            GpuVideoDecodePercent     = 0.0
            Gpu3DPercent              = 0.0
            GpuCountersAvailable      = $false
            GpuSampleAgeMilliseconds  = $null
        }) | Out-Null

        $previousTimestamp = $sampleTimestamp
        $previousCpuTotal = $currentCpuTotal

        if ($elapsedSeconds -ge $DurationSeconds) {
            break
        }

        $sampleIndex++
    }
}
finally {
    if ($null -ne $stopwatch) {
        $stopwatch.Stop()
    }
    $gpuSamples = @(Receive-GpuCounterJob -Job $gpuJob -IntervalSeconds $IntervalSeconds)
}

Merge-GpuSamples -Samples $samples -GpuSamples $gpuSamples -IntervalSeconds $IntervalSeconds

$samplesPath = Join-Path $resolvedOutputDirectory 'samples.csv'
$summaryPath = Join-Path $resolvedOutputDirectory 'summary.json'
$samples | Export-Csv -Path $samplesPath -NoTypeInformation

$summary = New-MeasurementSummary `
    -Samples $samples `
    -GpuSamples $gpuSamples `
    -ProcessId $ProcessId `
    -ProcessName $validatedIdentity.ProcessName `
    -Scenario $Scenario `
    -DurationSeconds $DurationSeconds `
    -IntervalSeconds $IntervalSeconds

$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $summaryPath

[pscustomobject]@{
    Scenario        = $Scenario
    ProcessId       = $ProcessId
    SampleCount     = $samples.Count
    SamplesPath     = $samplesPath
    SummaryPath     = $summaryPath
    OutputDirectory = $resolvedOutputDirectory
} | Format-List
