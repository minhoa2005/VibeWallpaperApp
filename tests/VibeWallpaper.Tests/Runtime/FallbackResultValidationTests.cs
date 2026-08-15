using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class FallbackResultValidationTests
{
    [Theory]
    [InlineData(AssignmentOutcome.Superseded)]
    public void EnsureApplied_WhenRuntimeResultIsNotApplied_ThrowsTypedFallbackFailure(AssignmentOutcome outcome)
    {
        var output = new MonitorIdentity("DISPLAY-A");
        var result = new AssignmentResult(7, outcome, [], false, [new AssignmentDiagnostic(output, AssignmentDiagnosticCode.HostUnavailable, null)]);

        var exception = Assert.Throws<RuntimeFallbackAssignmentException>(
            () => FallbackRuntimeActivationGuard.EnsureApplied(result, output));

        Assert.Equal(output, exception.Output);
        Assert.Same(result, exception.Result);
    }
}
