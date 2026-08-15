namespace VibeWallpaper.Engine.Rendering.Video.Diagnostics;

public sealed record VideoResourceSample(
    TimeSpan Elapsed,
    long PrivateBytes,
    long WorkingSetBytes,
    int HandleCount,
    int ThreadCount,
    double CpuPercent,
    double GpuVideoDecodePercent,
    double Gpu3DPercent)
{
    public static double CalculatePrivateBytesSlope(IEnumerable<VideoResourceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var orderedSamples = samples.ToArray();
        if (orderedSamples.Length < 2)
        {
            throw new ArgumentException("At least two samples are required to calculate a slope.", nameof(samples));
        }

        var previousElapsed = orderedSamples[0].Elapsed;
        for (var index = 1; index < orderedSamples.Length; index++)
        {
            var currentElapsed = orderedSamples[index].Elapsed;
            if (currentElapsed < previousElapsed)
            {
                throw new ArgumentException("Sample timestamps must be non-decreasing.", nameof(samples));
            }

            previousElapsed = currentElapsed;
        }

        var xMean = orderedSamples.Average(static sample => sample.Elapsed.TotalMinutes);
        var yMean = orderedSamples.Average(static sample => (double)sample.PrivateBytes);

        double numerator = 0;
        double denominator = 0;

        foreach (var sample in orderedSamples)
        {
            var centeredMinutes = sample.Elapsed.TotalMinutes - xMean;
            numerator += centeredMinutes * (sample.PrivateBytes - yMean);
            denominator += centeredMinutes * centeredMinutes;
        }

        if (denominator <= 0)
        {
            throw new ArgumentException("Samples must span more than one elapsed timestamp.", nameof(samples));
        }

        return numerator / denominator;
    }
}
