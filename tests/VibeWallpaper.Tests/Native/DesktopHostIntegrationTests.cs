using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Desktop;
using VibeWallpaper.Engine.Monitors;
using VibeWallpaper.Engine.Native;
using VibeWallpaper.Engine.Rendering.Solid;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Native;

public sealed class DesktopHostIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task CapturedOutputs_CreateDesktopChildrenAtExactPhysicalBounds_AndDisposeThem()
    {
        if (!WindowsIntegrationGate.IsEnabled(
                Environment.GetEnvironmentVariable(WindowsIntegrationGate.EnvironmentVariable)))
        {
            Assert.Skip(WindowsIntegrationGate.SkipReason);
        }

        var topologyService = new DisplayConfigTopologyService();
        if (!topologyService.IsInteractiveDesktopAvailable)
        {
            Assert.Skip("No interactive Windows desktop is available for WorkerW integration.");
        }

        var topology = topologyService.Capture();
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        await using var provider = new DesktopHostProvider(dispatcher);
        var hostHandles = new List<nint>();
        var renderers = new List<SolidColorRenderer>();
        var colors = WindowsIntegrationPalette.CreateDistinct(topology.LogicalOutputs.Count);

        try
        {
            for (var index = 0; index < topology.LogicalOutputs.Count; index++)
            {
                var output = topology.LogicalOutputs[index];
                var host = await provider.CreateAsync(output.Descriptor, TestContext.Current.CancellationToken);
                var nativeHost = Assert.IsType<WallpaperHostWindow>(host);
                var renderer = new SolidColorRenderer(dispatcher, NativeDesktopWindowApi.Instance);
                renderers.Add(renderer);
                hostHandles.Add(host.Hwnd);
                await dispatcher.InvokeAsync(async token =>
                {
                    var context = new RendererContext(
                        host.Hwnd,
                        output.Descriptor,
                        topology.VirtualDesktop,
                        output.Descriptor.Bounds);
                    await renderer.InitializeAsync(context, token);
                    await renderer.LoadAsync(SolidColorSource.Create(colors[index]), token);
                    host.SetRendererChild(renderer.Hwnd);
                    host.Show();
                    await renderer.ActivateAsync(token);

                    Assert.True(User32.IsWindow(host.Hwnd));
                    var actualParent = User32.GetParent(host.Hwnd);
                    Assert.Equal(nativeHost.DesktopResolution.ParentHwnd, actualParent);
                    var expectedParentClass = nativeHost.DesktopResolution.Strategy switch
                    {
                        "WorkerWSibling" => "WorkerW",
                        "ProgmanDefView" => "Progman",
                        "ProgmanRaisedDesktop" => "Progman",
                        _ => throw new InvalidOperationException(
                            $"Unexpected desktop strategy {nativeHost.DesktopResolution.Strategy}."),
                    };
                    Assert.Equal(
                        expectedParentClass,
                        NativeDesktopWindowApi.Instance.GetWindowClassName(actualParent));
                    if (nativeHost.DesktopResolution.RequiresLayeredChildren)
                    {
                        Assert.NotEqual(
                            0,
                            NativeDesktopWindowApi.Instance.GetExtendedWindowStyle(host.Hwnd) & 0x00080000);
                        Assert.NotEqual(
                            0,
                            NativeDesktopWindowApi.Instance.GetExtendedWindowStyle(renderer.Hwnd) & 0x00080000);
                    }

                    Assert.True(User32.GetWindowRect(host.Hwnd, out var rectangle));
                    Assert.Equal(output.Descriptor.Bounds.X, rectangle.Left);
                    Assert.Equal(output.Descriptor.Bounds.Y, rectangle.Top);
                    Assert.Equal(output.Descriptor.Bounds.Width, rectangle.Right - rectangle.Left);
                    Assert.Equal(output.Descriptor.Bounds.Height, rectangle.Bottom - rectangle.Top);
                }, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            foreach (var renderer in renderers)
            {
                await renderer.DisposeAsync();
            }

            await provider.DisposeAsync();
        }

        await dispatcher.InvokeAsync(_ =>
        {
            Assert.All(hostHandles, hwnd => Assert.False(User32.IsWindow(hwnd)));
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }
}
