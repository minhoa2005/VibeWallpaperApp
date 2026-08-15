using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class EngineSnapshotTests
{
    [Fact]
    public void Constructor_DeepCopiesPersistedCollectionsAndGroupMembers()
    {
        var output = new MonitorIdentity("DISPLAY-A");
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var definition = new WallpaperDefinition(WallpaperId.New(), "Solid", SolidColorSource.Create("#112233"), FitMode.Cover, 30, false, false, 0, false);
        var library = new List<WallpaperLibraryItem>
        {
            new(definition, null, null, new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UtcNow)),
        };
        var assignment = new WallpaperAssignment(
            new PersistedMonitorReference(output, new MonitorIdentityEvidence(1, 1, 1, null, null, null, null, null, null, "Display", bounds)),
            definition.Id, DisplayMode.Duplicate, FitMode.Cover, 30, 0, new DisplayGroupId(Guid.NewGuid()));
        var assignments = new List<WallpaperAssignment> { assignment };
        var members = new List<MonitorIdentity> { output };
        var groups = new List<PersistedDisplayGroup>
        {
            new(assignment.GroupId!.Value, DisplayMode.Duplicate, definition.Id, members),
        };
        var snapshot = new EngineSnapshot(new PersistedState(1, library, assignments, groups, null), []);

        library.Clear();
        assignments.Clear();
        members.Clear();
        groups.Clear();

        Assert.Single(snapshot.State.Library);
        Assert.Single(snapshot.State.Assignments);
        Assert.Single(snapshot.State.Groups);
        Assert.Single(snapshot.State.Groups.Single().Members);
    }
}
