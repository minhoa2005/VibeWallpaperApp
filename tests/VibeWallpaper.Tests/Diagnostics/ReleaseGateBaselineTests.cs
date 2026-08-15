using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VibeWallpaper.Tests.Diagnostics;

public sealed class ReleaseGateBaselineTests
{
    private static readonly string[] CsvColumns =
    [
        "TimestampUtc", "ElapsedSeconds", "PrivateBytes", "WorkingSetBytes", "HandleCount",
        "ThreadCount", "CpuPercent", "GpuVideoDecodePercent", "Gpu3DPercent",
        "GpuCountersAvailable", "GpuSampleAgeMilliseconds",
    ];

    [Fact]
    public async Task BaselineValidationOnly_AcceptsFiveMinuteSamplerArtifact()
    {
        var directory = CreateArtifact(sampleCount: 301, durationSeconds: 300, elapsedSpanSeconds: 300, scenario: "4k60-one-monitor");
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
            Assert.Contains("Verified Plan 1 baseline artifact", result.CombinedOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_AcceptsMissingLeadingGpuRowWhenCoverageMeetsFloor()
    {
        var gpuRowsAvailable = Enumerable.Range(0, 301)
            .Select(static index => index > 0)
            .ToArray();
        var directory = CreateArtifact(
            sampleCount: 301,
            durationSeconds: 300,
            elapsedSpanSeconds: 300,
            scenario: "4k60-one-monitor",
            gpuSampleCount: 300,
            gpuRowsAvailable: gpuRowsAvailable);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
            Assert.Contains("Verified Plan 1 baseline artifact", result.CombinedOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 300, 0, "4k60-one-monitor", "ObservedDurationSeconds")]
    [InlineData(301, 299, 300, "4k60-one-monitor", "DurationSecondsRequested")]
    [InlineData(301, 300, 300, "wrong-scenario", "Scenario")]
    [InlineData(300, 300, 300, "4k60-one-monitor", "SampleCount")]
    public async Task BaselineValidationOnly_RejectsInsufficientArtifact(
        int summarySampleCount,
        int durationSeconds,
        int elapsedSpanSeconds,
        string scenario,
        string expectedFailure)
    {
        var csvSampleCount = summarySampleCount == 300 ? 301 : summarySampleCount;
        var directory = CreateArtifact(csvSampleCount, durationSeconds, elapsedSpanSeconds, scenario, summarySampleCount);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expectedFailure, result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsMissingSamplerColumn()
    {
        var directory = CreateArtifact(301, 300, 300, "4k60-one-monitor", omittedColumn: "Gpu3DPercent");
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Gpu3DPercent", result.CombinedOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsAdequateSummaryWithShortCsvSpan()
    {
        var directory = CreateArtifact(301, 300, 299, "4k60-one-monitor", observedDurationSeconds: 300);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("CSV elapsed capture span", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsExcessiveProcessSampleGap()
    {
        var elapsedSeconds = Enumerable.Range(0, 301)
            .Select(static index => index <= 150 ? (double)index : index + 2d)
            .ToArray();
        var directory = CreateArtifact(
            301,
            300,
            302,
            "4k60-one-monitor",
            elapsedSeconds: elapsedSeconds,
            maximumProcessSampleGapSeconds: 3);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("MaximumProcessSampleGapSeconds", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsUnavailableGpuEvidence()
    {
        var directory = CreateArtifact(301, 300, 300, "4k60-one-monitor", gpuCountersAvailable: false, gpuSampleCount: 0);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("GpuCountersAvailable", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsInsufficientGpuCoverage()
    {
        var directory = CreateArtifact(301, 300, 300, "4k60-one-monitor", gpuSampleCount: 299);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("GpuSampleCount", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsMismatchedProcessAndGpuRanges()
    {
        var directory = CreateArtifact(301, 300, 300, "4k60-one-monitor", gpuSampleAgeMilliseconds: 2_000);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("GpuSampleAgeMilliseconds", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_RejectsNonOneSecondIntervalForReleaseEvidence()
    {
        var directory = CreateArtifact(
            151,
            300,
            300,
            "4k60-one-monitor",
            intervalSeconds: 2,
            maximumProcessSampleGapSeconds: 4,
            gpuSampleAgeMilliseconds: 3_000);
        try
        {
            var result = await RunValidationAsync(directory);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("IntervalSeconds", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_ExploratoryPreservesNonOneSecondIntervalArtifact()
    {
        var directory = CreateArtifact(
            151,
            300,
            300,
            "4k60-one-monitor",
            intervalSeconds: 2,
            maximumProcessSampleGapSeconds: 4,
            gpuSampleAgeMilliseconds: 3_000);
        try
        {
            var result = await RunValidationAsync(directory, exploratory: true);
            Assert.True(result.ExitCode == 0, result.CombinedOutput);
            Assert.Contains("Verified Plan 1 baseline artifact", result.CombinedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("Release gate passed.", result.CombinedOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BaselineValidationOnly_ReportsMissingBaselineExplicitly()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"vibe-plan1-missing-{Guid.NewGuid():N}");

        var result = await RunValidationAsync(missingDirectory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("manual-evidence blocker", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("baseline directory", result.CombinedOutput, StringComparison.Ordinal);
        Assert.Contains("is missing", result.CombinedOutput, StringComparison.Ordinal);
    }

    private static string CreateArtifact(
        int sampleCount,
        int durationSeconds,
        int elapsedSpanSeconds,
        string scenario,
        int? summarySampleCount = null,
        string? omittedColumn = null,
        double[]? elapsedSeconds = null,
        double? observedDurationSeconds = null,
        double? maximumProcessSampleGapSeconds = null,
        bool gpuCountersAvailable = true,
        int? gpuSampleCount = null,
        int gpuSampleAgeMilliseconds = 0,
        int intervalSeconds = 1,
        bool[]? gpuRowsAvailable = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vibe-plan1-baseline-{Guid.NewGuid():N}")).FullName;
        var columns = CsvColumns.Where(column => column != omittedColumn).ToArray();
        var csv = new StringBuilder().AppendLine(string.Join(',', columns));
        var started = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < sampleCount; index++)
        {
            var elapsed = elapsedSeconds is not null
                ? elapsedSeconds[index]
                : sampleCount <= 1 ? 0 : elapsedSpanSeconds * index / (double)(sampleCount - 1);
            var values = new Dictionary<string, string>
            {
                ["TimestampUtc"] = started.AddSeconds(elapsed).ToString("o", CultureInfo.InvariantCulture),
                ["ElapsedSeconds"] = elapsed.ToString("0.####", CultureInfo.InvariantCulture),
                ["PrivateBytes"] = "1000000", ["WorkingSetBytes"] = "900000", ["HandleCount"] = "20",
                ["ThreadCount"] = "5", ["CpuPercent"] = "1.5", ["GpuVideoDecodePercent"] = "2.5", ["Gpu3DPercent"] = "3.5",
                ["GpuCountersAvailable"] = (gpuRowsAvailable?[index] ?? gpuCountersAvailable).ToString(CultureInfo.InvariantCulture),
                ["GpuSampleAgeMilliseconds"] = (gpuRowsAvailable?[index] ?? gpuCountersAvailable)
                    ? gpuSampleAgeMilliseconds.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
            };
            csv.AppendLine(string.Join(',', columns.Select(column => values[column])));
        }

        File.WriteAllText(Path.Combine(directory, "samples.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
        {
            Scenario = scenario,
            SampleCount = summarySampleCount ?? sampleCount,
            DurationSecondsRequested = durationSeconds,
            IntervalSeconds = intervalSeconds,
            ObservedDurationSeconds = observedDurationSeconds ?? elapsedSpanSeconds,
            MaximumProcessSampleGapSeconds = maximumProcessSampleGapSeconds ?? 1,
            ProcessSampleCount = summarySampleCount ?? sampleCount,
            GpuSampleCount = gpuSampleCount ?? sampleCount,
            GpuCountersAvailable = gpuCountersAvailable,
            StartedAtUtc = started,
            CompletedAtUtc = started.AddSeconds(elapsedSpanSeconds),
        }));
        return directory;
    }

    private static async Task<(int ExitCode, string CombinedOutput)> RunValidationAsync(string directory, bool exploratory = false)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "eng", "run-release-gate.ps1"));
        startInfo.ArgumentList.Add("-BaselineDirectory");
        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("-BaselineValidationOnly");
        if (exploratory)
        {
            startInfo.ArgumentList.Add("-Exploratory");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, output + Environment.NewLine + error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeWallpaper.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }
}
