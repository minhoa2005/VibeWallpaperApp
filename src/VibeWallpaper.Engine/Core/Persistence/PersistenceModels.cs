using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Core.Persistence;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record WindowPlacementSettings
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;

    public double X { get => _x; init => _x = RequireFinite(value, nameof(X)); }
    public double Y { get => _y; init => _y = RequireFinite(value, nameof(Y)); }
    public double Width { get => _width; init => _width = RequirePositiveFinite(value, nameof(Width)); }
    public double Height { get => _height; init => _height = RequirePositiveFinite(value, nameof(Height)); }
    public bool IsMaximized { get; init; }

    [JsonConstructor]
    public WindowPlacementSettings(double x, double y, double width, double height, bool isMaximized)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsMaximized = isMaximized;
    }

    private static double RequireFinite(double value, string name) =>
        double.IsFinite(value) ? value : throw new ArgumentException("A finite value is required.", name);

    private static double RequirePositiveFinite(double value, string name) =>
        double.IsFinite(value) && value > 0 ? value : throw new ArgumentException("A positive finite value is required.", name);
}

public sealed record AppSettings
{
    private static readonly Regex RgbHex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private int _schemaVersion;
    private AppTheme _theme;
    private string _interactionHotkey = string.Empty;
    private int _batteryTargetFps;
    private int _batterySaverTargetFps;
    private IncompatibleThrottleBehavior _incompatibleThrottle;
    private WallpaperId? _fallbackWallpaper;
    private string _fallbackColor = string.Empty;
    private FitMode _defaultFit;
    private int _defaultTargetFps;
    private int _defaultVolumePercent;

    public static AppSettings Default { get; } = new(
        1, false, AppTheme.System, "Ctrl+Alt+I", true, false, true, true, true, true,
        30, 15, IncompatibleThrottleBehavior.Continue, null, "#101014", FitMode.Cover,
        30, false, 0, false, null);

    public int SchemaVersion { get => _schemaVersion; init => _schemaVersion = RequireNonnegative(value, nameof(SchemaVersion)); }
    public bool StartWithWindows { get; init; }
    public AppTheme Theme { get => _theme; init => _theme = RequireDefined(value, nameof(Theme)); }
    public string InteractionHotkey { get => _interactionHotkey; init => _interactionHotkey = RequireText(value, nameof(InteractionHotkey)); }
    public bool SuspendOnFullscreen { get; init; }
    public bool SuspendOnMaximized { get; init; }
    public bool SuspendOnRemoteDesktop { get; init; }
    public bool SuspendOnSessionLock { get; init; }
    public bool SuspendOnDisplayOff { get; init; }
    public bool SuspendOnSystemSleep { get; init; }
    public int BatteryTargetFps { get => _batteryTargetFps; init => _batteryTargetFps = RequireFps(value, nameof(BatteryTargetFps)); }
    public int BatterySaverTargetFps { get => _batterySaverTargetFps; init => _batterySaverTargetFps = RequireFps(value, nameof(BatterySaverTargetFps)); }
    public IncompatibleThrottleBehavior IncompatibleThrottle { get => _incompatibleThrottle; init => _incompatibleThrottle = RequireDefined(value, nameof(IncompatibleThrottle)); }
    public WallpaperId? FallbackWallpaper
    {
        get => _fallbackWallpaper;
        init
        {
            if (value.HasValue && value.Value.Value == Guid.Empty)
                throw new ArgumentException("A configured fallback wallpaper ID cannot be empty.", nameof(FallbackWallpaper));
            _fallbackWallpaper = value;
        }
    }
    public string FallbackColor
    {
        get => _fallbackColor;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _fallbackColor = RgbHex.IsMatch(value)
                ? value.ToUpperInvariant()
                : throw new ArgumentException("Expected #RRGGBB.", nameof(FallbackColor));
        }
    }

    public FitMode DefaultFit { get => _defaultFit; init => _defaultFit = RequireDefined(value, nameof(DefaultFit)); }
    public int DefaultTargetFps { get => _defaultTargetFps; init => _defaultTargetFps = RequireFps(value, nameof(DefaultTargetFps)); }
    public bool DefaultAudioEnabled { get; init; }
    public int DefaultVolumePercent { get => _defaultVolumePercent; init => _defaultVolumePercent = RequireVolume(value, nameof(DefaultVolumePercent)); }
    public bool DefaultInteractionEnabled { get; init; }
    public WindowPlacementSettings? ManagementWindow { get; init; }

    [JsonConstructor]
    public AppSettings(
        int schemaVersion,
        bool startWithWindows,
        AppTheme theme,
        string interactionHotkey,
        bool suspendOnFullscreen,
        bool suspendOnMaximized,
        bool suspendOnRemoteDesktop,
        bool suspendOnSessionLock,
        bool suspendOnDisplayOff,
        bool suspendOnSystemSleep,
        int batteryTargetFps,
        int batterySaverTargetFps,
        IncompatibleThrottleBehavior incompatibleThrottle,
        WallpaperId? fallbackWallpaper,
        string fallbackColor,
        FitMode defaultFit,
        int defaultTargetFps,
        bool defaultAudioEnabled,
        int defaultVolumePercent,
        bool defaultInteractionEnabled,
        WindowPlacementSettings? managementWindow)
    {
        SchemaVersion = schemaVersion;
        StartWithWindows = startWithWindows;
        Theme = theme;
        InteractionHotkey = interactionHotkey;
        SuspendOnFullscreen = suspendOnFullscreen;
        SuspendOnMaximized = suspendOnMaximized;
        SuspendOnRemoteDesktop = suspendOnRemoteDesktop;
        SuspendOnSessionLock = suspendOnSessionLock;
        SuspendOnDisplayOff = suspendOnDisplayOff;
        SuspendOnSystemSleep = suspendOnSystemSleep;
        BatteryTargetFps = batteryTargetFps;
        BatterySaverTargetFps = batterySaverTargetFps;
        IncompatibleThrottle = incompatibleThrottle;
        FallbackWallpaper = fallbackWallpaper;
        FallbackColor = fallbackColor;
        DefaultFit = defaultFit;
        DefaultTargetFps = defaultTargetFps;
        DefaultAudioEnabled = defaultAudioEnabled;
        DefaultVolumePercent = defaultVolumePercent;
        DefaultInteractionEnabled = defaultInteractionEnabled;
        ManagementWindow = managementWindow;
    }

    private static int RequireNonnegative(int value, string name) => value >= 0 ? value : throw new ArgumentOutOfRangeException(name);
    private static int RequireFps(int value, string name) => value is >= 1 and <= 60 ? value : throw new ArgumentOutOfRangeException(name);
    private static int RequireVolume(int value, string name) => value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(name);
    private static string RequireText(string value, string name) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException("A value is required.", name);
    private static TEnum RequireDefined<TEnum>(TEnum value, string name) where TEnum : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentException("A defined enum value is required.", name);
}

public enum SourceValidationStatus
{
    Available,
    Changed,
    Missing,
    Invalid,
    Unsupported,
}

public sealed record SourceStamp
{
    public long? Length { get; }
    public DateTimeOffset LastWriteUtc { get; }
    public string? Fingerprint { get; }

    [JsonConstructor]
    public SourceStamp(long? length, DateTimeOffset lastWriteUtc, string? fingerprint)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        RequireUtc(lastWriteUtc, nameof(lastWriteUtc));
        if (fingerprint is not null && string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Fingerprint cannot be blank.", nameof(fingerprint));
        }

        Length = length;
        LastWriteUtc = lastWriteUtc;
        Fingerprint = fingerprint;
    }

    internal static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC timestamp is required.", name);
        }
    }
}

public sealed record VideoMetadata
{
    public int Width { get; }
    public int Height { get; }
    public TimeSpan Duration { get; }
    public double? NominalFps { get; }
    public string? VideoCodec { get; }
    public bool HasAudio { get; }

    [JsonConstructor]
    public VideoMetadata(int width, int height, TimeSpan duration, double? nominalFps, string? videoCodec, bool hasAudio)
    {
        if (width <= 0) throw new ArgumentException("Positive width required.", nameof(width));
        if (height <= 0) throw new ArgumentException("Positive height required.", nameof(height));
        if (duration <= TimeSpan.Zero) throw new ArgumentException("Positive duration required.", nameof(duration));
        if (nominalFps.HasValue && (!double.IsFinite(nominalFps.Value) || nominalFps.Value <= 0))
            throw new ArgumentException("Positive finite FPS required.", nameof(nominalFps));
        if (videoCodec is not null && string.IsNullOrWhiteSpace(videoCodec))
            throw new ArgumentException("Codec cannot be blank.", nameof(videoCodec));

        Width = width;
        Height = height;
        Duration = duration;
        NominalFps = nominalFps;
        VideoCodec = videoCodec;
        HasAudio = hasAudio;
    }
}

public sealed record SourceValidation
{
    public SourceValidationStatus Status { get; }
    public SourceStamp? Stamp { get; }
    public string? DiagnosticCode { get; }
    public DateTimeOffset CheckedUtc { get; }

    [JsonConstructor]
    public SourceValidation(SourceValidationStatus status, SourceStamp? stamp, string? diagnosticCode, DateTimeOffset checkedUtc)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("A defined status is required.", nameof(status));
        if (diagnosticCode is not null && string.IsNullOrWhiteSpace(diagnosticCode))
            throw new ArgumentException("Diagnostic code cannot be blank.", nameof(diagnosticCode));
        SourceStamp.RequireUtc(checkedUtc, nameof(checkedUtc));

        Status = status;
        Stamp = stamp;
        DiagnosticCode = diagnosticCode;
        CheckedUtc = checkedUtc;
    }
}

public sealed record WallpaperLibraryItem
{
    public WallpaperDefinition Definition { get; }
    public string? ThumbnailCachePath { get; }
    public VideoMetadata? Video { get; }
    public SourceValidation Validation { get; }

    [JsonConstructor]
    public WallpaperLibraryItem(WallpaperDefinition definition, string? thumbnailCachePath, VideoMetadata? video, SourceValidation validation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validation);

        if (thumbnailCachePath is not null)
        {
            if (string.IsNullOrWhiteSpace(thumbnailCachePath) || !Path.IsPathFullyQualified(thumbnailCachePath))
                throw new ArgumentException("An absolute cache path is required.", nameof(thumbnailCachePath));
            var canonical = Path.GetFullPath(thumbnailCachePath);
            if (!string.Equals(canonical, thumbnailCachePath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A canonical cache path is required.", nameof(thumbnailCachePath));
            thumbnailCachePath = canonical;
        }

        Definition = definition;
        ThumbnailCachePath = thumbnailCachePath;
        Video = video;
        Validation = validation;
    }
}

public readonly record struct DisplayGroupId
{
    public Guid Value { get; }

    [JsonConstructor]
    public DisplayGroupId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Group ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static DisplayGroupId New() => new(Guid.NewGuid());
}

public sealed record PersistedMonitorReference
{
    public MonitorIdentity Identity { get; }
    public MonitorIdentityEvidence Evidence { get; }

    [JsonConstructor]
    public PersistedMonitorReference(MonitorIdentity identity, MonitorIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.FriendlyName))
            throw new ArgumentException("Monitor evidence requires a friendly name.", nameof(evidence));
        if (evidence.LastBounds is null)
            throw new ArgumentNullException(nameof(evidence), "Monitor evidence requires last bounds.");
        Identity = identity;
        Evidence = evidence;
    }
}

public sealed record WallpaperAssignment
{
    public PersistedMonitorReference Monitor { get; }
    public WallpaperId Wallpaper { get; }
    public DisplayMode Mode { get; }
    public FitMode Fit { get; }
    public int TargetFps { get; }
    public int VolumePercent { get; }
    public DisplayGroupId? GroupId { get; }

    [JsonConstructor]
    public WallpaperAssignment(PersistedMonitorReference monitor, WallpaperId wallpaper, DisplayMode mode, FitMode fit, int targetFps, int volumePercent, DisplayGroupId? groupId)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (wallpaper.Value == Guid.Empty) throw new ArgumentException("Wallpaper is required.", nameof(wallpaper));
        if (!Enum.IsDefined(mode)) throw new ArgumentException("A defined mode is required.", nameof(mode));
        if (!Enum.IsDefined(fit)) throw new ArgumentException("A defined fit is required.", nameof(fit));
        if (targetFps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(targetFps));
        if (volumePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volumePercent));

        Monitor = monitor;
        Wallpaper = wallpaper;
        Mode = mode;
        Fit = fit;
        TargetFps = targetFps;
        VolumePercent = volumePercent;
        GroupId = groupId;
    }
}

public sealed record PersistedDisplayGroup
{
    public DisplayGroupId Id { get; }
    public DisplayMode Mode { get; }
    public WallpaperId Wallpaper { get; }
    public IReadOnlyList<MonitorIdentity> Members { get; }

    [JsonConstructor]
    public PersistedDisplayGroup(DisplayGroupId id, DisplayMode mode, WallpaperId wallpaper, IReadOnlyList<MonitorIdentity> members)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("Group ID is required.", nameof(id));
        if (!Enum.IsDefined(mode)) throw new ArgumentException("A defined mode is required.", nameof(mode));
        if (wallpaper.Value == Guid.Empty) throw new ArgumentException("Wallpaper is required.", nameof(wallpaper));
        ArgumentNullException.ThrowIfNull(members);
        if (members.Any(static member => member is null)) throw new ArgumentException("Group members cannot contain null.", nameof(members));

        Id = id;
        Mode = mode;
        Wallpaper = wallpaper;
        Members = members;
    }
}

public sealed record PersistedState
{
    public static PersistedState Default { get; } = new(1, [], [], [], null);

    public int SchemaVersion { get; }
    public IReadOnlyList<WallpaperLibraryItem> Library { get; }
    public IReadOnlyList<WallpaperAssignment> Assignments { get; }
    public IReadOnlyList<PersistedDisplayGroup> Groups { get; }
    public MonitorIdentity? AudioOwner { get; }

    [JsonConstructor]
    public PersistedState(
        int schemaVersion,
        IReadOnlyList<WallpaperLibraryItem> library,
        IReadOnlyList<WallpaperAssignment> assignments,
        IReadOnlyList<PersistedDisplayGroup> groups,
        MonitorIdentity? audioOwner)
    {
        if (schemaVersion < 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(groups);
        if (library.Any(static item => item is null)) throw new ArgumentException("Library cannot contain null.", nameof(library));
        if (assignments.Any(static item => item is null)) throw new ArgumentException("Assignments cannot contain null.", nameof(assignments));
        if (groups.Any(static item => item is null)) throw new ArgumentException("Groups cannot contain null.", nameof(groups));

        SchemaVersion = schemaVersion;
        Library = library;
        Assignments = assignments;
        Groups = groups;
        AudioOwner = audioOwner;
    }
}

public enum PersistenceLoadSource
{
    Primary,
    Backup,
    Migrated,
    Defaults,
}

public sealed record PersistenceLoadResult<T>
{
    public T Value { get; }
    public PersistenceLoadSource Source { get; }
    public string? DiagnosticCode { get; }

    public PersistenceLoadResult(T value, PersistenceLoadSource source, string? diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(source)) throw new ArgumentException("A defined load source is required.", nameof(source));
        if (diagnosticCode is not null && string.IsNullOrWhiteSpace(diagnosticCode))
            throw new ArgumentException("Diagnostic code cannot be blank.", nameof(diagnosticCode));

        Value = value;
        Source = source;
        DiagnosticCode = diagnosticCode;
    }
}

public interface ISettingsStore
{
    Task<PersistenceLoadResult<AppSettings>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface IStateStore
{
    Task<PersistenceLoadResult<PersistedState>> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PersistedState state, CancellationToken cancellationToken);
}

public static class PersistenceDiagnosticCodes
{
    public const string PrimaryCorrupt = "persistence.primary_corrupt";
    public const string PrimaryIncompatible = "persistence.primary_incompatible";
    public const string BackupCorrupt = "persistence.backup_corrupt";
    public const string DocumentsMissing = "persistence.documents_missing";
    public const string UnsupportedSchema = "persistence.unsupported_schema";
    public const string MigrationGap = "persistence.migration_gap";
    public const string MigrationDuplicate = "persistence.migration_duplicate";
    public const string MigrationFailed = "persistence.migration_failed";
}
