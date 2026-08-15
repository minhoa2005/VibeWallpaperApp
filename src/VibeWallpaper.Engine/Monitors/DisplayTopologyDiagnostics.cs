using System.Security.Cryptography;
using System.Text;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Monitors;

/// <summary>
/// Creates a diagnostic-safe snapshot. Runtime snapshots retain raw Windows identity
/// evidence for reconciliation; diagnostic exports replace raw device paths with hashes.
/// </summary>
public static class DisplayTopologyDiagnostics
{
    public static DisplayTopologySnapshot CreateRedacted(DisplayTopologySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var outputs = snapshot.LogicalOutputs.Select(output =>
        {
            var redactedTargets = output.TargetEvidence.Select(Redact).ToArray();
            var original = output.Descriptor;
            var descriptor = new MonitorDescriptor(
                original.Identity,
                Redact(original.Evidence),
                original.FriendlyName,
                original.Bounds,
                original.WorkArea,
                original.Dpi,
                original.DpiScale,
                original.Orientation,
                original.IsPrimary);
            return new DisplayTopologyOutput(descriptor, output.CloneGroupKey, redactedTargets);
        }).ToArray();

        return new(snapshot.Version, snapshot.VirtualDesktop, outputs);
    }

    private static MonitorIdentityEvidence Redact(MonitorIdentityEvidence evidence) => evidence with
    {
        MonitorDevicePath = RedactValue(evidence.MonitorDevicePath),
        TargetInstanceId = RedactValue(evidence.TargetInstanceId),
    };

    private static string? RedactValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return $"redacted:sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
