using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering.Web;

public interface IWebControllerAdapter : IAsyncDisposable
{
    Task InitializeAsync(RendererContext context, CancellationToken cancellationToken);
    Task NavigateAsync(WebSource source, CancellationToken cancellationToken);
    Task SetVisibleAsync(bool visible, CancellationToken cancellationToken);
    Task SetPresentationThrottleAsync(int? targetPresentationFps, CancellationToken cancellationToken);
    Task<bool> TrySuspendAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
}
