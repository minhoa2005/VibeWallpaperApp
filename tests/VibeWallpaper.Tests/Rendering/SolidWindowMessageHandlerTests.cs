using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Rendering.Solid;

namespace VibeWallpaper.Tests.Rendering;

public sealed class SolidWindowMessageHandlerTests
{
    [Fact]
    public void HandleResizeThenPaint_InvalidatesAndFillsTheCurrentFullClientArea()
    {
        var drawing = new FakeSolidWindowDrawingApi
        {
            ClientBounds = new DisplayViewport(0, 0, 800, 600),
        };
        var handler = new SolidWindowMessageHandler(drawing);

        handler.HandleResize(55);
        drawing.ClientBounds = new DisplayViewport(0, 0, 1280, 720);
        handler.HandlePaint(55, 0x00563412);

        Assert.Equal(new[] { (nint)55 }, drawing.InvalidatedWindows);
        var fill = Assert.Single(drawing.Fills);
        Assert.Equal((nint)55, fill.Hwnd);
        Assert.Equal(0x00563412u, fill.Color);
        Assert.Equal(new DisplayViewport(0, 0, 1280, 720), fill.Bounds);
        Assert.Empty(drawing.LiveBrushes);
        Assert.Equal(1, drawing.EndPaintCount);
    }

    [Fact]
    public void HandlePaint_WhenFillFails_StillReleasesBrushAndPaintSession()
    {
        var drawing = new FakeSolidWindowDrawingApi
        {
            ClientBounds = new DisplayViewport(0, 0, 640, 480),
            FillFailure = new InvalidOperationException("injected fill failure"),
        };
        var handler = new SolidWindowMessageHandler(drawing);

        var exception = Assert.Throws<InvalidOperationException>(() => handler.HandlePaint(77, 0x00010203));

        Assert.Equal("injected fill failure", exception.Message);
        Assert.Empty(drawing.LiveBrushes);
        Assert.Equal(1, drawing.EndPaintCount);
    }
}

internal sealed class FakeSolidWindowDrawingApi : ISolidWindowDrawingApi
{
    private readonly Dictionary<nint, (nint Hwnd, uint Color)> _brushes = [];
    private nint _nextBrush = 100;
    private nint _paintingHwnd;

    public DisplayViewport ClientBounds { get; set; } = new(0, 0, 1, 1);
    public Exception? FillFailure { get; init; }
    public List<nint> InvalidatedWindows { get; } = [];
    public List<(nint Hwnd, uint Color, DisplayViewport Bounds)> Fills { get; } = [];
    public IReadOnlyCollection<nint> LiveBrushes => _brushes.Keys;
    public int EndPaintCount { get; private set; }

    public nint BeginPaint(nint hwnd)
    {
        _paintingHwnd = hwnd;
        return 500;
    }

    public void EndPaint(nint hwnd)
    {
        Assert.Equal(_paintingHwnd, hwnd);
        _paintingHwnd = 0;
        EndPaintCount++;
    }

    public DisplayViewport GetClientBounds(nint hwnd) => ClientBounds;

    public nint CreateBrush(nint hwnd, uint color)
    {
        var brush = _nextBrush++;
        _brushes.Add(brush, (hwnd, color));
        return brush;
    }

    public void Fill(nint deviceContext, DisplayViewport bounds, nint brush)
    {
        if (FillFailure is not null)
        {
            throw FillFailure;
        }

        var state = _brushes[brush];
        Fills.Add((state.Hwnd, state.Color, bounds));
    }

    public void DeleteBrush(nint brush) => _brushes.Remove(brush);
    public void Invalidate(nint hwnd) => InvalidatedWindows.Add(hwnd);
}
