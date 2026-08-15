namespace VibeWallpaper.Tests.Rendering;

internal static class NativeRepeatProgressTestAsset
{
    private static readonly string SourcePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestAssets", "NativeRepeatProgress.mp4"));

    public static string Create(string directory, string fileName)
    {
        if (!File.Exists(SourcePath))
        {
            throw new FileNotFoundException($"Native repeat MP4 test asset is missing at '{SourcePath}'.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.Copy(SourcePath, path, overwrite: true);
        return path;
    }
}
