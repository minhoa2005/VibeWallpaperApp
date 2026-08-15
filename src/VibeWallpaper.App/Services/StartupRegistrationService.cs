namespace VibeWallpaper.App.Services;

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string ValueName = "VibeWallpaper";
    private const string BackgroundArgument = " --background";
    private readonly IUserRunKey _runKey;

    public StartupRegistrationService(IUserRunKey runKey)
    {
        _runKey = runKey ?? throw new ArgumentNullException(nameof(runKey));
    }

    public StartupRegistrationStatus Inspect(string currentExecutable)
    {
        var current = NormalizeExecutable(currentExecutable);
        var commandLine = _runKey.Read(ValueName);
        var registered = ExtractExecutable(commandLine);
        if (registered is null)
        {
            return new StartupRegistrationStatus(StartupRegistrationState.Disabled, null);
        }

        var state = string.Equals(current, registered, StringComparison.OrdinalIgnoreCase)
            ? StartupRegistrationState.CurrentPath
            : StartupRegistrationState.MovedPath;
        return new StartupRegistrationStatus(state, registered);
    }

    public void Enable(string currentExecutable)
    {
        var executable = NormalizeExecutable(currentExecutable);
        _runKey.Write(ValueName, Quote(executable) + BackgroundArgument);
    }

    public void Disable() => _runKey.Delete(ValueName);

    public void Repair(string currentExecutable) => Enable(currentExecutable);

    private static string NormalizeExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(executable));
        }

        return Path.GetFullPath(executable);
    }

    private static string? ExtractExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        var separator = value.IndexOfAny([' ', '\t']);
        return separator > 0 ? value[..separator] : value;
    }

    private static string Quote(string executable) => $"\"{executable.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
