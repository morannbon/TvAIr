using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Epg.Projection;
using TvAIr.Tuner;

namespace TvAIr.Schedule;

public sealed class ReservationPresentationService
{
    private readonly ReservationStore _store;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly ChannelFileLoader _channelLoader;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly IProgramEventSource _programEvents;
    private readonly ReservationProjectionMetadataStore _projectionMetadata;
    private readonly SystemEpgResponsibilityPlanService _systemEpgResponsibilityPlan;

    public ReservationPresentationService(
        ReservationStore store,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        TunerPool tunerPool,
        LogRepository log,
        ChannelFileLoader channelLoader,
        ReservationAllocationRouteService allocationRoute,
        IProgramEventSource programEvents,
        ReservationProjectionMetadataStore projectionMetadata,
        SystemEpgResponsibilityPlanService systemEpgResponsibilityPlan)
    {
        _store = store;
        _ini = ini;
        _tunerProfiles = tunerProfiles;
        _tunerPool = tunerPool;
        _log = log;
        _channelLoader = channelLoader;
        _allocationRoute = allocationRoute;
        _programEvents = programEvents;
        _projectionMetadata = projectionMetadata;
        _systemEpgResponsibilityPlan = systemEpgResponsibilityPlan;
    }

    public ReservationPresentationItem? GetReservation(int id)
        => GetReservations().FirstOrDefault(x => x.Id == id);

    public IReadOnlyList<ReservationPresentationItem> GetReservations()
    {
        try
        {
            var reservations = _store.GetAll();
            var projection = BuildSystemEpgVisibleProjection(reservations);
            var visible = reservations.Where(r => r.Source != ReservationSource.Epg).ToList();
            visible.AddRange(projection.Rows
                .OrderBy(r => r.StartTime)
                .ThenBy(r => ResolveRecordingTunerOrder(projection.RecordingTuners, projection.PriorityNames.TryGetValue(r.Id, out var priority) ? priority : r.TunerName))
                .ThenBy(r => r.Id));
            return BuildPresentationItems(visible, projection.PriorityNames);
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "GetReservations", $"予約一覧整形失敗: {ex}");
            return Array.Empty<ReservationPresentationItem>();
        }
    }

    public IReadOnlyList<KeywordRuleReservationPresentationGroup> GetKeywordRuleReservations()
    {
        try
        {
            var items = GetReservations()
                .Where(x => x.SourceKind == ReservationSource.Keyword)
                .Where(x => x.StatusKind is ReservationStatus.Scheduled or ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
                .OrderBy(x => x.StartTime)
                .ThenBy(x => x.ServiceName)
                .ThenBy(x => x.Title)
                .ToList();

            if (items.Count == 0)
                return Array.Empty<KeywordRuleReservationPresentationGroup>();

            var ruleOrderMap = _store.GetKeywordRules()
                .Select((rule, index) => new { rule.Id, Order = rule.SortOrder > 0 ? rule.SortOrder : index + 1 })
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First().Order);

            var groups = items
                .GroupBy(x => new { x.SourceRuleId, RuleName = string.IsNullOrWhiteSpace(x.SourceRuleName) ? "（ルール不明）" : x.SourceRuleName })
                .OrderBy(g => g.Key.SourceRuleId.HasValue && ruleOrderMap.TryGetValue(g.Key.SourceRuleId.Value, out var order) ? order : int.MaxValue)
                .ThenBy(g => g.Key.RuleName, StringComparer.Ordinal)
                .Select(g => new KeywordRuleReservationPresentationGroup
                {
                    RuleId = g.Key.SourceRuleId,
                    RuleName = g.Key.RuleName,
                    Count = g.Count(),
                    Items = g.ToList()
                })
                .ToList();

            return groups;
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "GetKeywordRuleReservations", $"自動検索予約一覧整形失敗: {ex}");
            return Array.Empty<KeywordRuleReservationPresentationGroup>();
        }
    }


    private SystemEpgVisibleProjection BuildSystemEpgVisibleProjection(IReadOnlyList<Reservation> reservations)
    {
        var recordingTuners = GetRecordingTuners();
        if (recordingTuners.Count == 0)
            return new SystemEpgVisibleProjection(recordingTuners, Array.Empty<Reservation>(), new Dictionary<int, string>());

        var now = DateTime.Now;
        var preRecordEnabled = _ini.EpgPreRecordMinutes > 0;
        var dailyEnabled = _ini.EpgEnabled;
        if (!preRecordEnabled && !dailyEnabled)
            return new SystemEpgVisibleProjection(recordingTuners, Array.Empty<Reservation>(), new Dictionary<int, string>());

        var activeSystemRows = reservations
            .Where(r => IsActiveFutureSystemEpgRow(r, now))
            .ToList();
        var plan = _systemEpgResponsibilityPlan.Build(reservations, now);
        var selectedByParent = plan.PreRecordResponsibilities
            .GroupBy(x => x.ParentReservationId)
            .ToDictionary(g => g.Key, g => g.First());

        var visibleRows = new List<Reservation>();
        var priorityNames = new Dictionary<int, string>();

        // EPG確認はSystemEpgResponsibilityPlanが選んだ直近責務だけを表示する。
        // 直近は「現在から概ね3時間以内の予約イベント群」と、そこに候補が無い場合の
        // 「予約リスト全体の先頭1件」の二軸。各Tunerごとの未来先頭を独立に先取りしない。
        // Presentation層独自の再計算は禁止。
        if (preRecordEnabled)
        {
            foreach (var row in activeSystemRows
                         .Where(IsPreRecordEpgRow)
                         .Where(r => r.SourceRuleId.HasValue && selectedByParent.ContainsKey(r.SourceRuleId.Value))
                         .OrderBy(r => r.Status == ReservationStatus.Recording ? 0 : 1)
                         .ThenBy(r => r.StartTime)
                         .ThenBy(r => r.Id))
            {
                visibleRows.Add(row);
                var responsibility = selectedByParent[row.SourceRuleId!.Value];
                if (!string.IsNullOrWhiteSpace(responsibility.PriorityName))
                    priorityNames[row.Id] = responsibility.PriorityName;
            }
        }

        if (dailyEnabled)
        {
            // SYSTEM_EPG_DAILY_ENTRY_SINGLE_SOURCE:
            // 定時EPGの表示時刻はEpgSchedulerがPlanner結果から永続化したSystemDailyEpg行だけを正本とする。
            // Presentation層で設定時刻・深度・局数から仮想枠を再計算すると、可動枠の後方移動や
            // pending/skipで実行行が存在しない状態を後段から上書きしてしまうため、仮想Daily行は生成しない。
            var dailyRows = activeSystemRows
                .Where(IsDailyEpgRow)
                .Where(r => !string.IsNullOrWhiteSpace(r.TunerName))
                .GroupBy(r => r.TunerName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // 選択済み直近PreRecが責務を持つTunerだけ、同じTunerのDaily fallbackを隠す。
            // それ以外はDailyへ収束する。ON/ONでも1物理録画Tunerあたり可視System責務は最大1件。
            var usedPriorityNames = plan.PreRecordResponsibilities
                .Where(x => !string.IsNullOrWhiteSpace(x.PriorityName))
                .Select(x => x.PriorityName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var tuner in recordingTuners)
            {
                if (usedPriorityNames.Contains(tuner.Name))
                    continue;
                if (!dailyRows.TryGetValue(tuner.Name, out var tunerRows))
                    continue;

                var daily = SelectVisibleDailyEpg(tunerRows);
                if (daily is null)
                    continue;

                visibleRows.Add(daily);
                priorityNames[daily.Id] = tuner.Name;
            }
        }

        return new SystemEpgVisibleProjection(recordingTuners, visibleRows, priorityNames);
    }

    private static Reservation? SelectVisibleDailyEpg(IEnumerable<Reservation> rows)
        => rows
            .Where(IsDailyEpgRow)
            .OrderBy(r => r.Status == ReservationStatus.Recording ? 0 : 1)
            .ThenBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .FirstOrDefault();

    private IReadOnlyList<TunerProfile> GetRecordingTuners()
        => _tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static bool IsActiveFutureSystemEpgRow(Reservation r, DateTime now)
        => r.Source == ReservationSource.Epg
        && (r.Status == ReservationStatus.Scheduled || r.Status == ReservationStatus.Recording)
        && r.EndTime > now;

    private static bool IsPreRecordEpgRow(Reservation r)
        => ReservationIntentContract.IsPreRecordEpg(r);

    private static bool IsDailyEpgRow(Reservation r)
        => ReservationIntentContract.IsDailyEpg(r);

    private static int ResolveRecordingTunerOrder(IReadOnlyList<TunerProfile> tuners, string? tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return int.MaxValue;
        for (var i = 0; i < tuners.Count; i++)
        {
            if (string.Equals(tuners[i].Name, tunerName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return int.MaxValue;
    }

    private sealed record SystemEpgVisibleProjection(
        IReadOnlyList<TunerProfile> RecordingTuners,
        IReadOnlyList<Reservation> Rows,
        IReadOnlyDictionary<int, string> PriorityNames);

    private IReadOnlyList<ReservationPresentationItem> BuildPresentationItems(IReadOnlyList<Reservation> reservations, IReadOnlyDictionary<int, string>? priorityOverrides = null)
    {
        var chainMap = BuildChainMap();
        var items = new List<ReservationPresentationItem>(reservations.Count);

        foreach (var r in reservations)
        {
            try
            {
                chainMap.TryGetValue(r.Id, out var chainInfo);

                var sourceLabel = ReservationOriginClassifier.GetUserSourceLabel(r);
                var displayStatusLabel = BuildDisplayStatusLabel(r, sourceLabel);
                // RESERVATION_CONFLICT_PROJECTION_SINGLE_SOURCE:
                // IsConflicted is FinalAllocationCommit が永続化した競合正本。Presentation層で
                // 時刻重複やProgramGuideMissing重複から別の競合状態・競合相手を再計算しない。
                var conflictPrefix = string.Empty;
                var priorityName = priorityOverrides is not null && priorityOverrides.TryGetValue(r.Id, out var projectedPriority)
                    ? projectedPriority
                    : ResolvePresentationTunerName(r);
                var title = ResolveReservationTitle(r);
                if (IsPreRecordEpgRow(r) && !string.IsNullOrWhiteSpace(priorityName))
                    title = $"EPG確認（{priorityName}）";
                var resolvedServiceName = ResolveServiceName(r);
                var genreCodes = ResolveGenreCodes(r);
                var origin = ReservationOriginClassifier.Classify(r);

                items.Add(new ReservationPresentationItem
                {
                    Id = r.Id,
                    NetworkId = r.NetworkId,
                    TransportStreamId = r.TransportStreamId,
                    ServiceId = r.ServiceId,
                    EventId = r.EventId,
                    Title = title,
                    TitleDisplay = string.IsNullOrEmpty(conflictPrefix) ? title : $"{conflictPrefix}{title}",
                    ConflictPrefix = conflictPrefix,
                    ConflictTitles = Array.Empty<string>(),
                    StartTime = r.StartTime,
                    EndTime = r.EndTime,
                    Status = r.Status.ToString().ToLowerInvariant(),
                    Source = r.Source.ToString().ToLowerInvariant(),
                    StatusKind = r.Status,
                    SourceKind = r.Source,
                    SystemEpgKind = IsPreRecordEpgRow(r) ? "pre-record-epg" : IsDailyEpgRow(r) ? "daily-epg" : string.Empty,
                    SourceLabel = sourceLabel,
                    DisplayStatusLabel = displayStatusLabel,
                    ChannelArgument = r.ChannelArgument ?? string.Empty,
                    IsConflicted = r.IsConflicted,
                    IsEnabled = r.IsEnabled,
                    TunerName = priorityName,
                    PriorityName = priorityName,
                    ActualTunerName = r.ActualTunerName ?? string.Empty,
                    HasRecordingStarted = r.RecordingStartedAt.HasValue,
                    RecordingStartedAt = r.RecordingStartedAt,
                    RecordingFinishedAt = r.RecordingFinishedAt,
                    ServiceName = resolvedServiceName,
                    GenreCodes = genreCodes,
                    ScheduledStartTime = r.ScheduledStartTime,
                    SourceRuleId = r.SourceRuleId,
                    SourceRuleName = r.SourceRuleName ?? string.Empty,
                    ReservationOrigin = origin.Origin.ToString(),
                    ReservationIdentity = origin.Identity.ToString(),
                    IsProgramGuideMissing = origin.IsProgramGuideMissingProgramRule,
                    IsResolvedEventReservation = origin.IsResolvedEventReservation,
                    CanToggleEnabled = ReservationOperationPolicy.CanToggleEnabled(r),
                    CanCancel = ReservationOperationPolicy.CanCancel(r),
                    ChainRole = chainInfo?.Role ?? string.Empty,
                    ChainLabel = chainInfo?.Label ?? string.Empty,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt
                });
            }
            catch (Exception ex)
            {
                _log.Add("ReservationAPI", "BuildPresentationItem", $"予約ID={r.Id} 整形失敗: {ex.Message}");
            }
        }

        return items;
    }

    private static string ResolvePresentationTunerName(Reservation reservation)
    {
        if (reservation.Source == ReservationSource.Epg)
            return string.Empty;

        if (!reservation.IsEnabled || reservation.IsConflicted)
            return string.Empty;

        if (reservation.Status is ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
            return !string.IsNullOrWhiteSpace(reservation.ActualTunerName)
                ? reservation.ActualTunerName
                : reservation.TunerName ?? string.Empty;

        if (reservation.Status == ReservationStatus.Scheduled)
            return reservation.TunerName ?? string.Empty;

        return string.Empty;
    }

    private string ResolveGenreCodes(Reservation reservation)
    {
        try
        {
            var ev = ResolveProjectedEvent(reservation);
            return ev?.GenreCodes ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "ResolveGenreCodes", $"予約ID={reservation.Id} genre解決失敗: {ex.Message}");
            return string.Empty;
        }
    }

    private ProjectedProgramEvent? ResolveProjectedEvent(Reservation reservation)
    {
        var metadata = _projectionMetadata.Get(reservation.Id);
        if (metadata is not null && metadata.OverlayEventId != 0)
        {
            var byProjectionIdentity = _programEvents.GetByEventKey(
                metadata.OverlayNetworkId,
                metadata.OverlayTransportStreamId,
                metadata.OverlayServiceId,
                metadata.OverlayEventId);
            if (byProjectionIdentity is not null) return byProjectionIdentity;
        }

        if (reservation.EventId == 0) return null;
        return _programEvents.GetByEventKey(
            reservation.NetworkId,
            reservation.TransportStreamId,
            reservation.ServiceId,
            reservation.EventId);
    }

    private Dictionary<int, ReservationChainInfo> BuildChainMap()
    {
        var predecessors = _store.GetChainPredecessors();
        var result = new Dictionary<int, ReservationChainInfo>();
        foreach (var pair in predecessors)
        {
            var successorId = pair.Key;
            var predecessorId = pair.Value;

            if (!result.TryGetValue(predecessorId, out var prevInfo))
            {
                result[predecessorId] = new ReservationChainInfo("head", "チェーン");
            }
            else if (prevInfo.Role == "tail")
            {
                result[predecessorId] = new ReservationChainInfo("mid", "チェーン");
            }

            if (!result.TryGetValue(successorId, out var nextInfo))
            {
                result[successorId] = new ReservationChainInfo("tail", "チェーン");
            }
            else if (nextInfo.Role == "head")
            {
                result[successorId] = new ReservationChainInfo("mid", "チェーン");
            }
        }
        return result;
    }


    private string ResolveReservationTitle(Reservation r)
    {
        var title = NormalizeTitle(r.Title);
        if (!string.IsNullOrWhiteSpace(title)) return title;

        // 保存予約のタイトルが空の場合は、同じ番組イベント投影から表示用タイトルを補う。
        if (r.EventId != 0)
        {
            try
            {
                var ev = ResolveProjectedEvent(r);
                if (ev is not null)
                {
                    var projected = NormalizeTitle(ev.Title);
                    if (!string.IsNullOrWhiteSpace(projected)) return projected;
                }
            }
            catch
            {
                // Presentation fallback below keeps raw Reservation.Title untouched.
            }
        }

        // 予約タイトル表示は次の優先順で解決する。
        // 保存値は書き換えず、空タイトルは番組表と同じ表示規則で補う。
        return ReservationTitleDisplayContract.ForUser(r.Title);
    }

    private static string NormalizeTitle(string? title)
    {
        var value = (title ?? string.Empty).TrimStart();

        while (true)
        {
            var updated = value;
            updated = System.Text.RegularExpressions.Regex.Replace(
                updated,
                @"^(?:【競合(?::\s*[^】]+)?】|\[競合(?::\s*[^\]]+)?\]|［競合(?::\s*[^］]+)?］)\s*",
                string.Empty);

            if (updated == value)
                break;

            value = updated.TrimStart();
        }

        return value;
    }

    private string ResolveServiceName(Reservation reservation)
    {
        var stored = reservation.ServiceName?.Trim() ?? string.Empty;

        // SERVICE_IDENTITY_CONTRACT:
        // NID/TSID/SID is the service identity. ServiceName is mutable display metadata.
        // Reservation rows keep the name captured at creation time as a fallback snapshot, but
        // current presentation must prefer the exact current ChannelTarget name when available.
        // Never infer a current station by SID-only or by station-name matching.
        try
        {
            return ServiceIdentityContract.ResolveCurrentServiceName(
                _channelLoader.Load().Targets,
                reservation.NetworkId,
                reservation.TransportStreamId,
                reservation.ServiceId,
                stored);
        }
        catch (Exception ex)
        {
            _log.Add("ReservationAPI", "ResolveServiceName", $"予約ID={reservation.Id} 局名解決失敗: {ex.Message}");
            return stored;
        }
    }

    private static string BuildDisplayStatusLabel(Reservation reservation, string sourceLabel)
    {
        return sourceLabel;
    }

}

public sealed class ReservationPresentationItem
{
    public int Id { get; set; }
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public string Title { get; set; } = "";
    public string TitleDisplay { get; set; } = "";
    public string ConflictPrefix { get; set; } = "";
    public IReadOnlyList<string> ConflictTitles { get; set; } = Array.Empty<string>();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Status { get; set; } = "scheduled";
    public string Source { get; set; } = "manual";
    internal ReservationStatus StatusKind { get; set; }
    internal ReservationSource SourceKind { get; set; }
    /// <summary>System EPG行の表示種別。内部ReservationIntentをUIへ直接公開せず、表示に必要な分類だけを投影する。</summary>
    public string SystemEpgKind { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string DisplayStatusLabel { get; set; } = "";
    public string ChannelArgument { get; set; } = "";
    public bool IsConflicted { get; set; }
    public bool IsEnabled { get; set; }
    public string TunerName { get; set; } = "";
    public string PriorityName { get; set; } = "";
    public string ActualTunerName { get; set; } = "";
    public bool HasRecordingStarted { get; set; }
    public DateTime? RecordingStartedAt { get; set; }
    public DateTime? RecordingFinishedAt { get; set; }
    public string ServiceName { get; set; } = "";
    public string GenreCodes { get; set; } = "";
    public DateTime? ScheduledStartTime { get; set; }
    public int? SourceRuleId { get; set; }
    public string SourceRuleName { get; set; } = "";
    public string ReservationOrigin { get; set; } = "Unknown";
    public string ReservationIdentity { get; set; } = "Unknown";
    public bool IsProgramGuideMissing { get; set; }
    public bool IsResolvedEventReservation { get; set; }
    public bool CanToggleEnabled { get; set; }
    public bool CanCancel { get; set; }
    public string ChainRole { get; set; } = "";
    public string ChainLabel { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class KeywordRuleReservationPresentationGroup
{
    public int? RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public int Count { get; set; }
    public IReadOnlyList<ReservationPresentationItem> Items { get; set; } = Array.Empty<ReservationPresentationItem>();
}

public sealed record ReservationChainInfo(string Role, string Label);
