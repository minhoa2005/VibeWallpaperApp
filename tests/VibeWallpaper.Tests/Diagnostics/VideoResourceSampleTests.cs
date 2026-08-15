using System.Diagnostics;
using System.Text.Json;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;

namespace VibeWallpaper.Tests.Diagnostics;

public sealed class VideoResourceSampleTests
{
    [Fact]
    public void CalculateSlope_ReturnsBytesPerMinute()
    {
        var samples = new[]
        {
            new VideoResourceSample(TimeSpan.Zero, 100, 90, 10, 4, 0, 0, 0),
            new VideoResourceSample(TimeSpan.FromMinutes(2), 120, 95, 10, 4, 0, 0, 0),
        };

        Assert.Equal(10d, VideoResourceSample.CalculatePrivateBytesSlope(samples));
    }

    [Fact]
    public void CalculateSlope_RequiresAtLeastTwoSamples()
    {
        var samples = new[]
        {
            new VideoResourceSample(TimeSpan.Zero, 100, 90, 10, 4, 0, 0, 0),
        };

        var exception = Assert.Throws<ArgumentException>(() => VideoResourceSample.CalculatePrivateBytesSlope(samples));

        Assert.Contains("At least two", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CalculateSlope_RejectsDecreasingElapsedTimes()
    {
        var samples = new[]
        {
            new VideoResourceSample(TimeSpan.FromMinutes(2), 120, 95, 10, 4, 0, 0, 0),
            new VideoResourceSample(TimeSpan.FromMinutes(1), 140, 100, 11, 4, 0, 0, 0),
        };

        var exception = Assert.Throws<ArgumentException>(() => VideoResourceSample.CalculatePrivateBytesSlope(samples));

        Assert.Contains("non-decreasing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CalculateSlope_UsesLeastSquaresAcrossSamples()
    {
        var samples = new[]
        {
            new VideoResourceSample(TimeSpan.Zero, 100, 90, 10, 4, 0, 0, 0),
            new VideoResourceSample(TimeSpan.FromMinutes(1), 111, 95, 10, 4, 0, 0, 0),
            new VideoResourceSample(TimeSpan.FromMinutes(2), 119, 100, 10, 4, 0, 0, 0),
        };

        Assert.Equal(9.5d, VideoResourceSample.CalculatePrivateBytesSlope(samples));
    }

    [Fact]
    public async Task MeasureVideoPerformanceScript_WritesExpectedSummarySchema()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell), $"Expected PowerShell host at {powershell}.");

        var outputDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"vibe-video-performance-{Guid.NewGuid():N}")).FullName;

        try
        {
            var scriptPath = Path.Combine(FindRepositoryRoot(), "eng", "measure-video-performance.ps1");
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
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-DurationSeconds");
            startInfo.ArgumentList.Add("10");
            startInfo.ArgumentList.Add("-IntervalSeconds");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-OutputDirectory");
            startInfo.ArgumentList.Add(outputDirectory);
            startInfo.ArgumentList.Add("-Scenario");
            startInfo.ArgumentList.Add("unit-test");

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);

            var standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(
                process.ExitCode == 0,
                $"measure-video-performance.ps1 failed with exit code {process.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}");

            var samplesPath = Path.Combine(outputDirectory, "samples.csv");
            var summaryPath = Path.Combine(outputDirectory, "summary.json");
            Assert.True(File.Exists(samplesPath), $"Expected samples artifact at {samplesPath}.");
            Assert.True(File.Exists(summaryPath), $"Expected summary artifact at {summaryPath}.");

            using var summary = JsonDocument.Parse(
                await File.ReadAllTextAsync(summaryPath, TestContext.Current.CancellationToken));
            var root = summary.RootElement;

            Assert.Equal("unit-test", root.GetProperty("Scenario").GetString());
            Assert.Equal(Environment.ProcessId, root.GetProperty("ProcessId").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("ProcessName").GetString()));
            Assert.True(root.GetProperty("SampleCount").GetInt32() >= 2);
            Assert.Equal(10, root.GetProperty("DurationSecondsRequested").GetInt32());
            Assert.Equal(1, root.GetProperty("IntervalSeconds").GetInt32());
            Assert.True(root.GetProperty("ObservedDurationSeconds").GetDouble() >= 10);
            Assert.True(root.GetProperty("MaximumProcessSampleGapSeconds").GetDouble() <= 2);
            Assert.True(root.GetProperty("ProcessSampleCount").GetInt32() >= 10);
            Assert.Equal(root.GetProperty("SampleCount").GetInt32(), root.GetProperty("ProcessSampleCount").GetInt32());
            Assert.True(root.GetProperty("GpuSampleCount").ValueKind is JsonValueKind.Number);
            Assert.True(root.GetProperty("PrivateBytesSlopeBytesPerMinute").ValueKind is JsonValueKind.Number);
            Assert.True(root.GetProperty("GpuCountersAvailable").ValueKind is JsonValueKind.True or JsonValueKind.False);
            if (root.GetProperty("GpuCountersAvailable").GetBoolean())
            {
                Assert.True(root.GetProperty("GpuSampleCount").GetInt32() > 0);
            }

            var rows = ReadCsvRows(samplesPath);
            Assert.Equal(root.GetProperty("ProcessSampleCount").GetInt32(), rows.Rows.Count);
            Assert.Contains("GpuCountersAvailable", rows.Headers);
            Assert.Contains("GpuSampleAgeMilliseconds", rows.Headers);
            var firstElapsed = double.Parse(rows.Rows[0]["ElapsedSeconds"], System.Globalization.CultureInfo.InvariantCulture);
            var lastElapsed = double.Parse(rows.Last["ElapsedSeconds"], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, firstElapsed);
            Assert.True(lastElapsed >= 10);
            Assert.True(lastElapsed - firstElapsed >= 10);

            AssertMetricSummary(root, "PrivateBytes");
            AssertMetricSummary(root, "WorkingSetBytes");
            AssertMetricSummary(root, "HandleCount");
            AssertMetricSummary(root, "ThreadCount");
            AssertMetricSummary(root, "CpuPercent");
            AssertMetricSummary(root, "GpuVideoDecodePercent");
            AssertMetricSummary(root, "Gpu3DPercent");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MeasureVideoPerformanceScript_StreamsGpuCounterJobOutputBeforeMaxSamplesCompletes()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell), $"Expected PowerShell host at {powershell}.");

        var scriptPath = Path.Combine(FindRepositoryRoot(), "eng", "measure-video-performance.ps1");
        var scriptPathLiteral = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            $scriptText = Get-Content -Path '{{scriptPathLiteral}}' -Raw
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) {
                throw "Failed to parse measure-video-performance.ps1."
            }

            $functionAst = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Start-GpuCounterJob'
            }, $true)
            if ($null -eq $functionAst) {
                throw "Start-GpuCounterJob was not found."
            }

            Invoke-Expression $functionAst.Extent.Text
            $job = Start-GpuCounterJob -Id $PID -SampleIntervalSeconds 1 -MaxSamples 30
            try {
                Start-Sleep -Seconds 4
                $samples = @(Receive-Job -Job $job -Keep -ErrorAction SilentlyContinue)
                [pscustomobject]@{
                    PartialSampleCount = $samples.Count
                    JobState = [string]$job.State
                } | ConvertTo-Json -Compress

                if ($job.State -eq 'Running' -and $samples.Count -le 0) {
                    exit 42
                }
            }
            finally {
                if ($job.State -eq 'Running') {
                    Stop-Job -Job $job | Out-Null
                }

                Receive-Job -Job $job -ErrorAction SilentlyContinue | Out-Null
                Remove-Job -Job $job -Force | Out-Null
            }
            """;

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
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(
            process.ExitCode == 0,
            $"Start-GpuCounterJob did not stream partial samples before the long MaxSamples job completed. Exit code: {process.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}");
    }

    private static (string[] Headers, List<Dictionary<string, string>> Rows, Dictionary<string, string> Last) ReadCsvRows(string path)
    {
        var lines = File.ReadAllLines(path);
        Assert.True(lines.Length >= 2, $"Expected CSV header and at least one sample in {path}.");
        var headers = SplitCsvLine(lines[0]);
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1).Where(static line => !string.IsNullOrWhiteSpace(line)))
        {
            var values = SplitCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < headers.Length; index++)
            {
                row[headers[index]] = index < values.Length ? values[index] : string.Empty;
            }

            rows.Add(row);
        }

        return (headers, rows, rows[^1]);
    }

    private static string[] SplitCsvLine(string line) =>
        line.Split(',').Select(static value => value.Trim().Trim('"')).ToArray();

    private static void AssertMetricSummary(JsonElement root, string propertyName)
    {
        var block = root.GetProperty(propertyName);
        Assert.True(block.GetProperty("Initial").ValueKind is JsonValueKind.Number);
        Assert.True(block.GetProperty("Final").ValueKind is JsonValueKind.Number);
        Assert.True(block.GetProperty("Minimum").ValueKind is JsonValueKind.Number);
        Assert.True(block.GetProperty("Maximum").ValueKind is JsonValueKind.Number);
        Assert.True(block.GetProperty("Average").ValueKind is JsonValueKind.Number);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeWallpaper.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output directory.");
    }
}
