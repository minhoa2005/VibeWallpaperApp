using VibeWallpaper.App.ViewModels;
using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Tests.App;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task ChangeHotkey_ConflictKeepsPriorBinding()
    {
        var controller = new FakeController();
        var vm = new SettingsViewModel(AppSettings.Default, controller);

        await vm.ChangeHotkeyAsync("Ctrl+Shift+I", TestContext.Current.CancellationToken);

        Assert.Equal("Ctrl+Alt+I", vm.InteractionHotkey);
        Assert.True(vm.HasHotkeyConflict);
    }

    private sealed class FakeController : ISettingsController
    {
        public Task<HotkeyChangeResult> ChangeHotkeyAsync(string gesture, CancellationToken cancellationToken) =>
            Task.FromResult(HotkeyChangeResult.Conflict("Ctrl+Alt+I", "hotkey.conflict"));
    }
}
