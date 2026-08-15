using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Runtime;

public sealed record OutputRuntimeSnapshot(
    MonitorIdentity Output,
    long Generation,
    RendererLifecycle? Lifecycle,
    PerformanceState? PerformanceState,
    EffectiveWallpaperState? EffectiveState = null,
    IReadOnlySet<PerformanceReason>? Reasons = null);

public sealed record EngineSnapshot
{
    public EngineSnapshot(PersistedState state, IReadOnlyList<OutputRuntimeSnapshot> outputs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outputs);
        State = CloneState(state);
        Outputs = new ReadOnlyCollection<OutputRuntimeSnapshot>(outputs.Select(CloneOutput).ToArray());
    }

    public PersistedState State { get; }
    public IReadOnlyList<OutputRuntimeSnapshot> Outputs { get; }

    private static PersistedState CloneState(PersistedState state) => new(
        state.SchemaVersion,
        state.Library.Select(CloneLibraryItem).ToArray(),
        state.Assignments.Select(CloneAssignment).ToArray(),
        state.Groups.Select(group => new PersistedDisplayGroup(
            group.Id,
            group.Mode,
            group.Wallpaper,
            group.Members.Select(CloneIdentity).ToArray())).ToArray(),
        state.AudioOwner is null ? null : CloneIdentity(state.AudioOwner));

    private static WallpaperLibraryItem CloneLibraryItem(WallpaperLibraryItem item) => new(
        CloneDefinition(item.Definition),
        item.ThumbnailCachePath,
        item.Video is null ? null : new VideoMetadata(
            item.Video.Width,
            item.Video.Height,
            item.Video.Duration,
            item.Video.NominalFps,
            item.Video.VideoCodec,
            item.Video.HasAudio),
        new SourceValidation(
            item.Validation.Status,
            item.Validation.Stamp is null ? null : new SourceStamp(
                item.Validation.Stamp.Length,
                item.Validation.Stamp.LastWriteUtc,
                item.Validation.Stamp.Fingerprint),
            item.Validation.DiagnosticCode,
            item.Validation.CheckedUtc));

    private static VibeWallpaper.Engine.Core.Wallpapers.WallpaperDefinition CloneDefinition(
        VibeWallpaper.Engine.Core.Wallpapers.WallpaperDefinition definition) => new(
            definition.Id,
            definition.Name,
            definition.Source switch
            {
                VibeWallpaper.Engine.Core.Wallpapers.SolidColorSource solid =>
                    VibeWallpaper.Engine.Core.Wallpapers.SolidColorSource.Create(solid.HexColor),
                VibeWallpaper.Engine.Core.Wallpapers.VideoSource video =>
                    VibeWallpaper.Engine.Core.Wallpapers.VideoSource.Create(video.FilePath),
                VibeWallpaper.Engine.Core.Wallpapers.WebSource web =>
                    VibeWallpaper.Engine.Core.Wallpapers.WebSource.Create(web.DirectoryPath, web.EntryPoint),
                _ => throw new InvalidOperationException("Unsupported wallpaper source type."),
            },
            definition.Fit,
            definition.TargetFps,
            definition.NetworkEnabled,
            definition.AudioEnabled,
            definition.VolumePercent,
            definition.InteractionEnabled);

    private static WallpaperAssignment CloneAssignment(WallpaperAssignment assignment) => new(
        new PersistedMonitorReference(
            CloneIdentity(assignment.Monitor.Identity),
            CloneEvidence(assignment.Monitor.Evidence)),
        assignment.Wallpaper,
        assignment.Mode,
        assignment.Fit,
        assignment.TargetFps,
        assignment.VolumePercent,
        assignment.GroupId);

    private static OutputRuntimeSnapshot CloneOutput(OutputRuntimeSnapshot output) => new(
        CloneIdentity(output.Output),
        output.Generation,
        output.Lifecycle,
        output.PerformanceState,
        output.EffectiveState is null ? null : new EffectiveWallpaperState(
            output.EffectiveState.AssignedWallpaper,
            output.EffectiveState.EffectiveKind,
            output.EffectiveState.EffectiveWallpaper,
            output.EffectiveState.FallbackReasonCode),
        output.Reasons is null ? null : new HashSet<PerformanceReason>(output.Reasons));

    private static MonitorIdentity CloneIdentity(MonitorIdentity identity) => new(identity.Key);

    private static MonitorIdentityEvidence CloneEvidence(MonitorIdentityEvidence evidence) => new(
        evidence.AdapterLuid,
        evidence.SourceId,
        evidence.TargetId,
        evidence.ConnectorInstance,
        evidence.TargetInstanceId,
        evidence.MonitorDevicePath,
        evidence.EdidManufacturer,
        evidence.EdidProductCode,
        evidence.EdidSerialNumber,
        evidence.FriendlyName,
        new DisplayViewport(
            evidence.LastBounds.X,
            evidence.LastBounds.Y,
            evidence.LastBounds.Width,
            evidence.LastBounds.Height));
}
