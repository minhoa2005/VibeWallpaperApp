using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeWallpaper.Engine.Core.Wallpapers;

public readonly record struct WallpaperId
{
    public Guid Value { get; }

    public WallpaperId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public static WallpaperId New() => new(Guid.NewGuid());
}

public readonly record struct RendererInstanceId
{
    public Guid Value { get; }

    public RendererInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Renderer instance ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public static RendererInstanceId New() => new(Guid.NewGuid());
}

public enum WallpaperKind
{
    SolidColor,
    Video,
    Web,
}

public enum DisplayMode
{
    Independent,
    Duplicate,
    Span,
}

public enum FitMode
{
    Cover,
    Contain,
    Stretch,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(VideoSource), "video")]
[JsonDerivedType(typeof(WebSource), "web")]
[JsonDerivedType(typeof(SolidColorSource), "solid")]
public abstract record WallpaperSource(WallpaperKind Kind);

public sealed record VideoSource : WallpaperSource
{
    public string FilePath { get; }

    [JsonConstructor]
    public VideoSource(string filePath)
        : base(WallpaperKind.Video)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("Absolute file path required.", nameof(filePath));
        }

        var canonicalPath = Path.GetFullPath(filePath);
        if (Path.EndsInDirectorySeparator(canonicalPath))
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        FilePath = canonicalPath;
    }

    public static VideoSource Create(string filePath) => new(filePath);
}

public sealed record WebSource : WallpaperSource
{
    public string DirectoryPath { get; }

    public string EntryPoint { get; }

    [JsonConstructor]
    public WebSource(string directoryPath, string entryPoint)
        : base(WallpaperKind.Web)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Path.IsPathFullyQualified(directoryPath))
        {
            throw new ArgumentException("Absolute directory required.", nameof(directoryPath));
        }

        if (string.IsNullOrWhiteSpace(entryPoint) || Path.IsPathRooted(entryPoint))
        {
            throw new ArgumentException("A relative entry point is required.", nameof(entryPoint));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var entry = Path.GetFullPath(Path.Combine(root, entryPoint));
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!entry.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Entry point must remain inside the root.", nameof(entryPoint));
        }

        DirectoryPath = root;
        EntryPoint = Path.GetRelativePath(root, entry);
    }

    public static WebSource Create(string directoryPath, string entryPoint) => new(directoryPath, entryPoint);
}

public sealed record SolidColorSource : WallpaperSource
{
    private static readonly Regex RgbHex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    public string HexColor { get; }

    [JsonConstructor]
    public SolidColorSource(string hexColor)
        : base(WallpaperKind.SolidColor)
    {
        ArgumentNullException.ThrowIfNull(hexColor);

        if (!RgbHex.IsMatch(hexColor))
        {
            throw new ArgumentException("Expected #RRGGBB.", nameof(hexColor));
        }

        HexColor = hexColor.ToUpperInvariant();
    }

    public static SolidColorSource Create(string hexColor) => new(hexColor);
}

public sealed record WallpaperDefinition
{
    public WallpaperId Id { get; }

    public string Name { get; }

    public WallpaperSource Source { get; }

    public FitMode Fit { get; }

    public int TargetFps { get; }

    public bool NetworkEnabled { get; }

    public bool AudioEnabled { get; }

    public int VolumePercent { get; }

    public bool InteractionEnabled { get; }

    [JsonConstructor]
    public WallpaperDefinition(
        WallpaperId id,
        string name,
        WallpaperSource source,
        FitMode fit,
        int targetFps,
        bool networkEnabled,
        bool audioEnabled,
        int volumePercent,
        bool interactionEnabled)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Wallpaper ID is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(source);

        if (!Enum.IsDefined(fit))
        {
            throw new ArgumentException("A defined fit mode is required.", nameof(fit));
        }

        if (targetFps is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps));
        }

        if (volumePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volumePercent));
        }

        if (source is not WebSource && (networkEnabled || interactionEnabled))
        {
            throw new ArgumentException("Network and interaction apply only to web wallpapers.");
        }

        if (source is not VideoSource && (audioEnabled || volumePercent != 0))
        {
            throw new ArgumentException("Audio and volume apply only to video wallpapers.");
        }

        Id = id;
        Name = name.Trim();
        Source = source;
        Fit = fit;
        TargetFps = targetFps;
        NetworkEnabled = networkEnabled;
        AudioEnabled = audioEnabled;
        VolumePercent = volumePercent;
        InteractionEnabled = interactionEnabled;
    }
}
