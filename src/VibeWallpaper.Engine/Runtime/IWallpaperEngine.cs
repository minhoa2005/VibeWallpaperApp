using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Runtime;

public interface IWallpaperEngine
{
    Task<AssignmentResult> ApplyAsync(
        AssignmentRequest request,
        CancellationToken cancellationToken);

    Task SetReasonsAsync(
        MonitorIdentity output,
        PerformanceReasonOwner owner,
        IReadOnlySet<PerformanceReason> reasons,
        CancellationToken cancellationToken);

    EngineSnapshot GetSnapshot();
}
