namespace VibeWallpaper.Engine.Rendering.Web;

public interface IWebEnvironmentProvider : IAsyncDisposable
{
    long Generation { get; }

    Task<WebEnvironmentHandle> GetAsync(CancellationToken cancellationToken);

    Task InvalidateAsync(long expectedGeneration, CancellationToken cancellationToken);
}

public sealed record WebEnvironmentHandle(long Generation, string UserDataFolder);
