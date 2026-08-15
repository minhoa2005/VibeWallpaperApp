namespace VibeWallpaper.App.Services;

public enum StartupRegistrationState
{
    Disabled,
    CurrentPath,
    MovedPath,
}

public sealed record StartupRegistrationStatus(
    StartupRegistrationState State,
    string? RegisteredExecutable);

public interface IStartupRegistrationService
{
    StartupRegistrationStatus Inspect(string currentExecutable);
    void Enable(string currentExecutable);
    void Disable();
    void Repair(string currentExecutable);
}
