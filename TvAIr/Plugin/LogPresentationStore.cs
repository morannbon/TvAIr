using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// Stores plugin-provided log presentation snapshots in memory.
/// The store does not replace the host log database and does not mutate reservations.
/// </summary>
internal sealed class LogPresentationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, PluginLogPresentationSnapshot>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PluginLogPresentationSnapshot>> _inactiveLogSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginRecordingQualitySnapshot> _recordingQuality = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PluginLogPresentationPolicy>> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PluginLogPresentationPolicy>> _inactivePolicies = new(StringComparer.OrdinalIgnoreCase);

    public TvAirLogPresentationReplaceResultDto ReplaceLogSnapshot(string pluginId, TvAirLogPresentationSnapshotDto snapshot)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        var viewKey = NormalizeViewKey(snapshot.ViewKey);
        var normalized = new PluginLogPresentationSnapshot(sourcePluginId, NormalizeLogSnapshot(snapshot, viewKey));
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(viewKey, out var byPlugin))
            {
                byPlugin = new Dictionary<string, PluginLogPresentationSnapshot>(StringComparer.OrdinalIgnoreCase);
                _snapshots[viewKey] = byPlugin;
            }
            byPlugin[sourcePluginId] = normalized;
            if (_inactiveLogSnapshots.TryGetValue(viewKey, out var inactiveByPlugin))
            {
                inactiveByPlugin.Remove(sourcePluginId);
                if (inactiveByPlugin.Count == 0) _inactiveLogSnapshots.Remove(viewKey);
            }
        }

        return new TvAirLogPresentationReplaceResultDto
        {
            Accepted = true,
            ViewKey = viewKey,
            EntryCount = normalized.Snapshot.Entries.Count,
            Message = "Accepted"
        };
    }

    public TvAirLogPresentationReplaceResultDto SetLogPolicy(string pluginId, TvAirLogPresentationPolicyDto policy)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        var viewKey = NormalizeViewKey(policy.ViewKey);
        var normalizedDetailKeys = NormalizePolicyDetailKeys(policy);
        var normalized = new PluginLogPresentationPolicy(sourcePluginId, new TvAirLogPresentationPolicyDto
        {
            ViewKey = viewKey,
            Title = (policy.Title ?? string.Empty).Trim(),
            Enabled = policy.Enabled,
            DetailKeys = normalizedDetailKeys,
            Layout = (policy.DetailKeys is null || policy.DetailKeys.Count == 0) && policy.MultilineOnDetail
                ? TvAirLogDetailLayout.Multiline
                : policy.Layout,
            HideEmptyDetails = policy.HideEmptyDetails,
            Priority = policy.Priority,
            UpdatedAt = policy.UpdatedAt == default ? DateTimeOffset.Now : policy.UpdatedAt
        });
        lock (_gate)
        {
            if (!_policies.TryGetValue(viewKey, out var byPlugin))
            {
                byPlugin = new Dictionary<string, PluginLogPresentationPolicy>(StringComparer.OrdinalIgnoreCase);
                _policies[viewKey] = byPlugin;
            }
            byPlugin[sourcePluginId] = normalized;
            if (_inactivePolicies.TryGetValue(viewKey, out var inactive))
            {
                inactive.Remove(sourcePluginId);
                if (inactive.Count == 0) _inactivePolicies.Remove(viewKey);
            }
        }
        return new TvAirLogPresentationReplaceResultDto { Accepted = true, ViewKey = viewKey, EntryCount = 0, Message = "PolicyAccepted" };
    }

    public PluginLogPresentationPolicy? GetActiveLogPolicy(string viewKey)
    {
        var normalizedViewKey = NormalizeViewKey(viewKey);
        lock (_gate)
        {
            return _policies.TryGetValue(normalizedViewKey, out var rows)
                ? rows.Values.Where(x => x.Policy.Enabled).OrderByDescending(x => x.Policy.Priority).ThenByDescending(x => x.Policy.UpdatedAt).FirstOrDefault()
                : null;
        }
    }

    public IReadOnlyList<PluginLogPresentationPolicy> ListLogPolicies(string? viewKey = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(viewKey)) return _policies.Values.SelectMany(x => x.Values).ToList();
            var key = NormalizeViewKey(viewKey);
            return _policies.TryGetValue(key, out var rows) ? rows.Values.ToList() : Array.Empty<PluginLogPresentationPolicy>();
        }
    }

    public void ClearLogSnapshot(string pluginId, string? viewKey = null)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(viewKey))
            {
                foreach (var key in _snapshots.Keys.ToList()) SuspendLogSnapshotLocked(sourcePluginId, key);
                return;
            }

            var normalizedViewKey = NormalizeViewKey(viewKey);
            SuspendLogSnapshotLocked(sourcePluginId, normalizedViewKey);
        }
    }

    public void ClearLogPolicy(string pluginId, string? viewKey = null)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(viewKey))
            {
                foreach (var key in _policies.Keys.ToList()) SuspendLogPolicyLocked(sourcePluginId, key);
                return;
            }
            SuspendLogPolicyLocked(sourcePluginId, NormalizeViewKey(viewKey));
        }
    }

    public IReadOnlyList<PluginLogPresentationSnapshot> ReactivateInactiveLogSnapshots(string pluginId, string? viewKey = null)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        lock (_gate)
        {
            var keys = string.IsNullOrWhiteSpace(viewKey)
                ? _inactiveLogSnapshots.Keys.ToList()
                : new List<string> { NormalizeViewKey(viewKey) };
            var restored = new List<PluginLogPresentationSnapshot>();
            foreach (var key in keys)
            {
                if (!_inactiveLogSnapshots.TryGetValue(key, out var inactiveByPlugin)
                    || !inactiveByPlugin.TryGetValue(sourcePluginId, out var snapshot))
                    continue;

                if (!_snapshots.TryGetValue(key, out var activeByPlugin))
                {
                    activeByPlugin = new Dictionary<string, PluginLogPresentationSnapshot>(StringComparer.OrdinalIgnoreCase);
                    _snapshots[key] = activeByPlugin;
                }

                var reactivated = new PluginLogPresentationSnapshot(
                    snapshot.SourcePluginId,
                    CloneLogSnapshotWithUpdatedAt(snapshot.Snapshot, DateTimeOffset.Now));
                activeByPlugin[sourcePluginId] = reactivated;
                inactiveByPlugin.Remove(sourcePluginId);
                if (inactiveByPlugin.Count == 0) _inactiveLogSnapshots.Remove(key);
                restored.Add(reactivated);
            }
            var policyKeys = string.IsNullOrWhiteSpace(viewKey) ? _inactivePolicies.Keys.ToList() : new List<string> { NormalizeViewKey(viewKey) };
            foreach (var key in policyKeys)
            {
                if (!_inactivePolicies.TryGetValue(key, out var inactive) || !inactive.TryGetValue(sourcePluginId, out var policy)) continue;
                if (!_policies.TryGetValue(key, out var active)) { active = new Dictionary<string, PluginLogPresentationPolicy>(StringComparer.OrdinalIgnoreCase); _policies[key] = active; }
                active[sourcePluginId] = new PluginLogPresentationPolicy(policy.SourcePluginId, new TvAirLogPresentationPolicyDto
                {
                    ViewKey = policy.Policy.ViewKey, Title = policy.Policy.Title, Enabled = true,
                    DetailKeys = policy.Policy.DetailKeys, Layout = policy.Policy.Layout,
                    HideEmptyDetails = policy.Policy.HideEmptyDetails,
                    Priority = policy.Policy.Priority, UpdatedAt = DateTimeOffset.Now
                });
                inactive.Remove(sourcePluginId);
                if (inactive.Count == 0) _inactivePolicies.Remove(key);
            }
            return restored;
        }
    }

    public IReadOnlyList<PluginLogPresentationSnapshot> ListInactiveLogSnapshots(string? viewKey = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(viewKey))
                return _inactiveLogSnapshots.Values.SelectMany(x => x.Values).OrderBy(x => x.Snapshot.ViewKey).ThenBy(x => x.SourcePluginId).ToList();
            var normalizedViewKey = NormalizeViewKey(viewKey);
            return _inactiveLogSnapshots.TryGetValue(normalizedViewKey, out var rows)
                ? rows.Values.OrderByDescending(x => x.Snapshot.Priority).ThenByDescending(x => x.Snapshot.UpdatedAt).ToList()
                : Array.Empty<PluginLogPresentationSnapshot>();
        }
    }

    private void SuspendLogSnapshotLocked(string sourcePluginId, string viewKey)
    {
        if (!_snapshots.TryGetValue(viewKey, out var activeByPlugin)
            || !activeByPlugin.TryGetValue(sourcePluginId, out var snapshot))
            return;

        if (!_inactiveLogSnapshots.TryGetValue(viewKey, out var inactiveByPlugin))
        {
            inactiveByPlugin = new Dictionary<string, PluginLogPresentationSnapshot>(StringComparer.OrdinalIgnoreCase);
            _inactiveLogSnapshots[viewKey] = inactiveByPlugin;
        }

        inactiveByPlugin[sourcePluginId] = snapshot;
        activeByPlugin.Remove(sourcePluginId);
        if (activeByPlugin.Count == 0) _snapshots.Remove(viewKey);
    }

    private void SuspendLogPolicyLocked(string sourcePluginId, string viewKey)
    {
        if (!_policies.TryGetValue(viewKey, out var active) || !active.TryGetValue(sourcePluginId, out var policy)) return;
        if (!_inactivePolicies.TryGetValue(viewKey, out var inactive))
        {
            inactive = new Dictionary<string, PluginLogPresentationPolicy>(StringComparer.OrdinalIgnoreCase);
            _inactivePolicies[viewKey] = inactive;
        }
        inactive[sourcePluginId] = policy;
        active.Remove(sourcePluginId);
        if (active.Count == 0) _policies.Remove(viewKey);
    }

    public PluginLogPresentationSnapshot? GetActiveLogSnapshot(string viewKey)
    {
        var normalizedViewKey = NormalizeViewKey(viewKey);
        lock (_gate)
        {
            return _snapshots.TryGetValue(normalizedViewKey, out var rows)
                ? rows.Values
                    .Where(x => x.Snapshot.ReplaceHostDefault)
                    .OrderByDescending(x => x.Snapshot.Priority)
                    .ThenByDescending(x => x.Snapshot.UpdatedAt)
                    .FirstOrDefault()
                : null;
        }
    }

    public IReadOnlyList<PluginLogPresentationSnapshot> ListLogSnapshots(string? viewKey = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(viewKey))
                return _snapshots.Values.SelectMany(x => x.Values).OrderBy(x => x.Snapshot.ViewKey).ThenBy(x => x.SourcePluginId).ToList();
            var normalizedViewKey = NormalizeViewKey(viewKey);
            return _snapshots.TryGetValue(normalizedViewKey, out var rows)
                ? rows.Values.OrderByDescending(x => x.Snapshot.Priority).ThenByDescending(x => x.Snapshot.UpdatedAt).ToList()
                : Array.Empty<PluginLogPresentationSnapshot>();
        }
    }

    public TvAirRecordingQualityReplaceResultDto ReplaceRecordingQualitySnapshot(string pluginId, TvAirRecordingQualitySnapshotDto snapshot)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        var normalized = new PluginRecordingQualitySnapshot(sourcePluginId, NormalizeRecordingQualitySnapshot(snapshot));
        lock (_gate)
            _recordingQuality[sourcePluginId] = normalized;

        return new TvAirRecordingQualityReplaceResultDto
        {
            Accepted = true,
            ItemCount = normalized.Snapshot.Items.Count,
            Message = "Accepted"
        };
    }

    public void ClearRecordingQualitySnapshot(string pluginId)
    {
        var sourcePluginId = NormalizePluginId(pluginId);
        lock (_gate)
            _recordingQuality.Remove(sourcePluginId);
    }

    public PluginRecordingQualitySnapshot? GetActiveRecordingQualitySnapshot()
    {
        lock (_gate)
        {
            return _recordingQuality.Values
                .OrderByDescending(x => x.Snapshot.Priority)
                .ThenByDescending(x => x.Snapshot.UpdatedAt)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<PluginRecordingQualitySnapshot> ListRecordingQualitySnapshots()
    {
        lock (_gate)
            return _recordingQuality.Values.OrderByDescending(x => x.Snapshot.Priority).ThenByDescending(x => x.Snapshot.UpdatedAt).ToList();
    }

    private static TvAirLogPresentationSnapshotDto CloneLogSnapshotWithUpdatedAt(TvAirLogPresentationSnapshotDto snapshot, DateTimeOffset updatedAt)
    {
        return new TvAirLogPresentationSnapshotDto
        {
            ViewKey = snapshot.ViewKey,
            Title = snapshot.Title,
            Summary = snapshot.Summary,
            ReplaceHostDefault = snapshot.ReplaceHostDefault,
            Priority = snapshot.Priority,
            UpdatedAt = updatedAt,
            Entries = snapshot.Entries
        };
    }

    private static TvAirLogPresentationSnapshotDto NormalizeLogSnapshot(TvAirLogPresentationSnapshotDto snapshot, string viewKey)
    {
        var entries = (snapshot.Entries ?? Array.Empty<TvAirLogPresentationEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Message) || !string.IsNullOrWhiteSpace(e.ProgramTitle) || !string.IsNullOrWhiteSpace(e.ReservationId))
            .Take(5000)
            .Select((e, index) => new TvAirLogPresentationEntryDto
            {
                EntryId = SafeSingleLine(e.EntryId, 128, string.Empty),
                Timestamp = e.Timestamp,
                Severity = SafeSingleLine(e.Severity, 32, "Info"),
                Category = SafeSingleLine(e.Category, 128, string.Empty),
                ReservationId = SafeSingleLine(e.ReservationId, 64, string.Empty),
                ServiceName = SafeSingleLine(e.ServiceName, 256, string.Empty),
                ProgramTitle = SafeSingleLine(e.ProgramTitle, 512, string.Empty),
                Target = SafeSingleLine(e.Target, 1024, string.Empty),
                TargetTextMode = SafeTextMode(e.TargetTextMode, string.Empty),
                ResultTextMode = SafeTextMode(e.ResultTextMode, string.Empty),
                MessageTextMode = SafeTextMode(e.MessageTextMode, string.Empty),
                Message = SafeMultiline(e.Message, 4096, string.Empty),
                Result = SafeSingleLine(e.Result, 128, string.Empty),
                DropCount = e.DropCount,
                ErrorCount = e.ErrorCount,
                ScrambleCount = e.ScrambleCount,
                FilePath = SafeSingleLine(e.FilePath, 1024, string.Empty),
                Details = NormalizeDetails(e.Details)
            })
            .ToArray();

        return new TvAirLogPresentationSnapshotDto
        {
            ViewKey = viewKey,
            Title = SafeSingleLine(snapshot.Title, 128, string.Empty),
            Summary = SafeMultiline(snapshot.Summary, 2048, string.Empty),
            ReplaceHostDefault = snapshot.ReplaceHostDefault,
            Priority = snapshot.Priority,
            UpdatedAt = snapshot.UpdatedAt == default ? DateTimeOffset.Now : snapshot.UpdatedAt,
            Entries = entries
        };
    }

    private static TvAirRecordingQualitySnapshotDto NormalizeRecordingQualitySnapshot(TvAirRecordingQualitySnapshotDto snapshot)
    {
        var items = (snapshot.Items ?? Array.Empty<TvAirRecordingQualityDto>())
            .Where(i => !string.IsNullOrWhiteSpace(i.ReservationId) || !string.IsNullOrWhiteSpace(i.FilePath) || !string.IsNullOrWhiteSpace(i.ProgramTitle))
            .Take(5000)
            .Select(i => new TvAirRecordingQualityDto
            {
                ReservationId = SafeSingleLine(i.ReservationId, 64, string.Empty),
                ServiceName = SafeSingleLine(i.ServiceName, 256, string.Empty),
                ProgramTitle = SafeSingleLine(i.ProgramTitle, 512, string.Empty),
                FilePath = SafeSingleLine(i.FilePath, 1024, string.Empty),
                Start = i.Start,
                End = i.End,
                State = SafeSingleLine(i.State, 64, string.Empty),
                DropCount = i.DropCount,
                ErrorCount = i.ErrorCount,
                ScrambleCount = i.ScrambleCount,
                Summary = SafeMultiline(i.Summary, 2048, string.Empty),
                Details = NormalizeDetails(i.Details)
            })
            .ToArray();

        return new TvAirRecordingQualitySnapshotDto
        {
            Title = SafeSingleLine(snapshot.Title, 128, string.Empty),
            Summary = SafeMultiline(snapshot.Summary, 2048, string.Empty),
            Priority = snapshot.Priority,
            UpdatedAt = snapshot.UpdatedAt == default ? DateTimeOffset.Now : snapshot.UpdatedAt,
            Items = items
        };
    }

    private static string SafeTextMode(string? value, string fallback)
    {
        var v = SafeSingleLine(value, 32, fallback).Trim().ToLowerInvariant();
        return v is "singleline" or "multiline" or "auto" ? v : fallback;
    }

    private static IReadOnlyDictionary<string, string> NormalizeDetails(IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
            return new Dictionary<string, string>();
        return details
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .Take(200)
            .ToDictionary(kv => SafeSingleLine(kv.Key, 128, string.Empty), kv => SafeMultiline(kv.Value, 4096, string.Empty), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> NormalizePolicyDetailKeys(TvAirLogPresentationPolicyDto policy)
    {
        var keys = new List<string>();
        if (policy.DetailKeys is { Count: > 0 }) keys.AddRange(policy.DetailKeys);
        else
        {
            if (policy.ShowReservationId) keys.Add(TvAirLogDetailKeys.ReservationId);
            if (policy.ShowRecordingId) keys.Add(TvAirLogDetailKeys.RecordingId);
            if (policy.ShowSchedule) { keys.Add(TvAirLogDetailKeys.ScheduleStart); keys.Add(TvAirLogDetailKeys.ScheduleEnd); }
            if (policy.ShowQuality)
            {
                keys.Add(TvAirLogDetailKeys.RecordingQualityDrop);
                keys.Add(TvAirLogDetailKeys.RecordingQualityError);
                keys.Add(TvAirLogDetailKeys.RecordingQualityScramble);
            }
            if (policy.ShowFilePath) keys.Add(TvAirLogDetailKeys.RecordingFilePath);
        }
        return keys
            .Select(x => SafeSingleLine(x, 128, string.Empty))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private static string NormalizePluginId(string pluginId) => PluginIdentity.Normalize(pluginId, "unknown");
    private static string NormalizeViewKey(string? viewKey) => SafeSingleLine(viewKey, 128, "reservation-log");

    private static string SafeSingleLine(string? value, int maxLength, string fallback)
        => PluginPresentationText.SingleLine(value, maxLength, fallback);

    private static string SafeMultiline(string? value, int maxLength, string fallback)
        => PluginPresentationText.Multiline(value, maxLength, fallback);
}

internal sealed record PluginLogPresentationSnapshot(string SourcePluginId, TvAirLogPresentationSnapshotDto Snapshot);
internal sealed record PluginLogPresentationPolicy(string SourcePluginId, TvAirLogPresentationPolicyDto Policy);
internal sealed record PluginRecordingQualitySnapshot(string SourcePluginId, TvAirRecordingQualitySnapshotDto Snapshot);
