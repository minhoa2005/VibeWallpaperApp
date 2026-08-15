using Microsoft.Win32;

namespace VibeWallpaper.App.Services;

public sealed class RegistryUserRunKey : IUserRunKey
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string valueName, string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the per-user startup registry key.");
        key.SetValue(valueName, commandLine, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
