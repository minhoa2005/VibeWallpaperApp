using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Runtime;

public sealed class RuntimeFallbackAssignmentException : Exception
{
    public RuntimeFallbackAssignmentException(
        MonitorIdentity output,
        AssignmentResult result)
        : base($"Runtime fallback assignment for '{output.Key}' did not apply ({result.Outcome}).")
    {
        Output = output;
        Result = result;
    }

    public MonitorIdentity Output { get; }
    public AssignmentResult Result { get; }
}

public static class FallbackRuntimeActivationGuard
{
    public static void EnsureApplied(AssignmentResult result, MonitorIdentity output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);
        if (result.Outcome != AssignmentOutcome.Applied ||
            !result.AppliedOutputs.Any(item => string.Equals(item.Key, output.Key, StringComparison.Ordinal)))
        {
            throw new RuntimeFallbackAssignmentException(output, result);
        }
    }
}
