using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Rendering.Solid;

internal interface ISolidWindowDrawingApi
{
    nint BeginPaint(nint hwnd);
    void EndPaint(nint hwnd);
    DisplayViewport GetClientBounds(nint hwnd);
    nint CreateBrush(nint hwnd, uint color);
    void Fill(nint deviceContext, DisplayViewport bounds, nint brush);
    void DeleteBrush(nint brush);
    void Invalidate(nint hwnd);
}

internal sealed class SolidWindowMessageHandler(ISolidWindowDrawingApi drawing)
{
    private readonly ISolidWindowDrawingApi _drawing = drawing ?? throw new ArgumentNullException(nameof(drawing));

    internal void HandlePaint(nint hwnd, uint color)
    {
        var deviceContext = _drawing.BeginPaint(hwnd);
        try
        {
            var bounds = _drawing.GetClientBounds(hwnd);
            var brush = _drawing.CreateBrush(hwnd, color);
            try
            {
                _drawing.Fill(deviceContext, bounds, brush);
            }
            finally
            {
                _drawing.DeleteBrush(brush);
            }
        }
        finally
        {
            _drawing.EndPaint(hwnd);
        }
    }

    internal void HandleResize(nint hwnd) => _drawing.Invalidate(hwnd);
}
