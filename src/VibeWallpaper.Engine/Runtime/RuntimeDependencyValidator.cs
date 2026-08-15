namespace VibeWallpaper.Engine.Runtime;

public sealed record RuntimeCapability(bool Available, string? FailureCode, Uri? RepairUri);

public sealed record RuntimeDependencyReport(RuntimeCapability Video, RuntimeCapability Web);

public sealed record RuntimeDependencyPaths(
    string LibVlcPath,
    string LibVlcCorePath,
    string PluginsDirectory,
    Uri? WebRepairUri);

public static class RuntimeDependencyValidator
{
    public static RuntimeDependencyReport Validate(RuntimeDependencyPaths paths, bool webAvailable)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var video = File.Exists(paths.LibVlcPath)
            && File.Exists(paths.LibVlcCorePath)
            && Directory.Exists(paths.PluginsDirectory)
            ? new RuntimeCapability(true, null, null)
            : new RuntimeCapability(false, "runtime.video.missing", null);
        var web = webAvailable
            ? new RuntimeCapability(true, null, null)
            : new RuntimeCapability(false, "runtime.web.unavailable", paths.WebRepairUri);
        return new RuntimeDependencyReport(video, web);
    }
}
