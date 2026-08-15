using VibeWallpaper.App.Services;

namespace VibeWallpaper.Tests.App;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public void Enable_WritesQuotedUnicodePortablePath()
    {
        var runKey = new FakeUserRunKey();
        var service = new StartupRegistrationService(runKey);
        var executable = @"D:\Ứng dụng của tôi\VibeWallpaper.exe";

        service.Enable(executable);

        Assert.Equal(
            "\"D:\\Ứng dụng của tôi\\VibeWallpaper.exe\" --background",
            runKey.Value);
        Assert.Equal(StartupRegistrationState.CurrentPath, service.Inspect(executable).State);
    }

    [Fact]
    public void Inspect_DetectsMovedPortablePath()
    {
        var runKey = new FakeUserRunKey { Value = "\"D:\\Old\\VibeWallpaper.exe\" --background" };
        var service = new StartupRegistrationService(runKey);

        var result = service.Inspect(@"D:\New\VibeWallpaper.exe");

        Assert.Equal(StartupRegistrationState.MovedPath, result.State);
        Assert.Equal(@"D:\Old\VibeWallpaper.exe", result.RegisteredExecutable);
    }

    [Fact]
    public void Disable_IsIdempotent()
    {
        var runKey = new FakeUserRunKey { Value = "old" };
        var service = new StartupRegistrationService(runKey);

        service.Disable();
        service.Disable();

        Assert.Null(runKey.Value);
        Assert.Equal(2, runKey.DeleteCount);
    }

    [Fact]
    public void Repair_ReplacesMovedPath()
    {
        var runKey = new FakeUserRunKey { Value = "\"D:\\Old\\VibeWallpaper.exe\" --background" };
        var service = new StartupRegistrationService(runKey);
        var current = @"D:\New Folder\VibeWallpaper.exe";

        service.Repair(current);

        Assert.Equal($"\"{current}\" --background", runKey.Value);
    }

    private sealed class FakeUserRunKey : IUserRunKey
    {
        public string? Value { get; set; }
        public int DeleteCount { get; private set; }

        public string? Read(string valueName) => Value;
        public void Write(string valueName, string commandLine) => Value = commandLine;
        public void Delete(string valueName) { Value = null; DeleteCount++; }
    }
}
