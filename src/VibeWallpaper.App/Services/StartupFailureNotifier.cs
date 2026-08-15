using System.Runtime.InteropServices;

namespace VibeWallpaper.App.Services;

public sealed class StartupFailureNotifier
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    public void Show()
    {
        try
        {
            _ = MessageBox(
                0,
                "Không thể khởi động Vibe Wallpaper. Chi tiết lỗi đã được ghi vào thư mục Logs trong dữ liệu ứng dụng.",
                "Vibe Wallpaper",
                MbOk | MbIconError | MbSetForeground);
        }
        catch
        {
            // A startup notification is a last-resort path and must never hide the original failure.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
