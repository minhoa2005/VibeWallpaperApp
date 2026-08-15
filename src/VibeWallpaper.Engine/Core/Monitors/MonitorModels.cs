namespace VibeWallpaper.Engine.Core.Monitors;

public sealed record MonitorIdentity
{
    public string Key { get; }

    public MonitorIdentity(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Identity is required.", nameof(key));
        }

        Key = key.Trim();
    }
}

public sealed record DisplayViewport
{
    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public DisplayViewport(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

public sealed record MonitorIdentityEvidence(
    long AdapterLuid,
    uint SourceId,
    uint TargetId,
    uint? ConnectorInstance,
    string? TargetInstanceId,
    string? MonitorDevicePath,
    string? EdidManufacturer,
    ushort? EdidProductCode,
    uint? EdidSerialNumber,
    string FriendlyName,
    DisplayViewport LastBounds);

public enum DisplayOrientation
{
    Landscape,
    Portrait,
    LandscapeFlipped,
    PortraitFlipped,
}

public sealed record MonitorDescriptor
{
    public MonitorIdentity Identity { get; }

    public MonitorIdentityEvidence Evidence { get; }

    public string FriendlyName { get; }

    public DisplayViewport Bounds { get; }

    public DisplayViewport WorkArea { get; }

    public uint Dpi { get; }

    public double DpiScale { get; }

    public DisplayOrientation Orientation { get; }

    public bool IsPrimary { get; }

    public MonitorDescriptor(
        MonitorIdentity identity,
        MonitorIdentityEvidence evidence,
        string friendlyName,
        DisplayViewport bounds,
        DisplayViewport workArea,
        uint dpi,
        double dpiScale,
        DisplayOrientation orientation,
        bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(workArea);

        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            throw new ArgumentException("Name is required.", nameof(friendlyName));
        }

        if (dpi < 96)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }

        if (!double.IsFinite(dpiScale) || dpiScale < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        }

        if (!Enum.IsDefined(orientation))
        {
            throw new ArgumentException("A defined display orientation is required.", nameof(orientation));
        }

        Identity = identity;
        Evidence = evidence;
        FriendlyName = friendlyName.Trim();
        Bounds = bounds;
        WorkArea = workArea;
        Dpi = dpi;
        DpiScale = dpiScale;
        Orientation = orientation;
        IsPrimary = isPrimary;
    }
}
