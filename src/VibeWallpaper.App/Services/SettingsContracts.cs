using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.App.Services;

public enum HotkeyChangeStatus { Applied, Conflict, Invalid }

public sealed record HotkeyChangeResult(HotkeyChangeStatus Status, string EffectiveGesture, string? ErrorCode)
{
    public static HotkeyChangeResult Conflict(string currentGesture, string errorCode) =>
        new(HotkeyChangeStatus.Conflict, currentGesture, errorCode);
}

public interface ISettingsController
{
    Task<HotkeyChangeResult> ChangeHotkeyAsync(string gesture, CancellationToken cancellationToken);
}
