using System.Collections.Frozen;

namespace VibeWallpaper.Engine.Core.Activity;

public sealed class PerformanceReasonSet
{
    private readonly Dictionary<PerformanceReasonOwner, HashSet<PerformanceReason>> _reasonsByOwner = [];

    public bool Add(PerformanceReasonOwner owner, PerformanceReason reason)
    {
        ValidateOwner(owner);
        ValidateReason(reason);

        if (!_reasonsByOwner.TryGetValue(owner, out var reasons))
        {
            reasons = [];
            _reasonsByOwner.Add(owner, reasons);
        }

        return reasons.Add(reason);
    }

    public bool Remove(PerformanceReasonOwner owner, PerformanceReason reason)
    {
        ValidateOwner(owner);
        ValidateReason(reason);

        if (!_reasonsByOwner.TryGetValue(owner, out var reasons) || !reasons.Remove(reason))
        {
            return false;
        }

        if (reasons.Count == 0)
        {
            _reasonsByOwner.Remove(owner);
        }

        return true;
    }

    public bool ReplaceOwnedReasons(PerformanceReasonOwner owner, IEnumerable<PerformanceReason> reasons)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(reasons);

        var replacement = new HashSet<PerformanceReason>();
        foreach (var reason in reasons)
        {
            ValidateReason(reason);
            replacement.Add(reason);
        }

        if (!_reasonsByOwner.TryGetValue(owner, out var existing))
        {
            if (replacement.Count == 0)
            {
                return false;
            }

            _reasonsByOwner[owner] = replacement;
            return true;
        }

        if (existing.SetEquals(replacement))
        {
            return false;
        }

        if (replacement.Count == 0)
        {
            _reasonsByOwner.Remove(owner);
        }
        else
        {
            _reasonsByOwner[owner] = replacement;
        }

        return true;
    }

    public FrozenSet<PerformanceReason> Snapshot()
    {
        var union = new HashSet<PerformanceReason>();
        foreach (var reasons in _reasonsByOwner.Values)
        {
            union.UnionWith(reasons);
        }

        return union.ToFrozenSet();
    }

    public FrozenSet<PerformanceReason> Snapshot(PerformanceReasonOwner owner)
    {
        ValidateOwner(owner);
        return _reasonsByOwner.TryGetValue(owner, out var reasons)
            ? reasons.ToFrozenSet()
            : FrozenSet<PerformanceReason>.Empty;
    }

    private static void ValidateOwner(PerformanceReasonOwner owner)
    {
        if (!Enum.IsDefined(owner))
        {
            throw new ArgumentException("A defined performance reason owner is required.", nameof(owner));
        }
    }

    private static void ValidateReason(PerformanceReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException("A defined performance reason is required.", nameof(reason));
        }
    }
}
