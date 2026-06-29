using System.Reflection;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// IPluginContext の本体側実装。
/// プラグインは本体DB/内部クラスへ直接触れず、このContext APIだけを使用する。
/// </summary>
internal sealed class PluginContext : IPluginReadContextV4, ILiveCommentPublisher
{
    private readonly LogRepository _log;
    private readonly EpgStore _epgStore;
    private readonly ReservationStore _reservationStore;
    private readonly TunerPool _tunerPool;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly ChannelFileLoader _channelLoader;
    private readonly TaskSchedulerService _taskScheduler;
    private readonly EpgScheduler _epgScheduler;
    private readonly LiveCommentStore _liveComments;
    private readonly ExternalTunerLeaseService _externalTuners;
    private readonly string _pluginName;

    public string AppDirectory { get; } = AppContext.BaseDirectory;
    public string DataDirectory { get; }
    public string PluginDataDirectory { get; }

    public PluginContext(
        LogRepository log,
        EpgStore epgStore,
        ReservationStore reservationStore,
        TunerPool tunerPool,
        ReservationAllocationRouteService allocationRoute,
        ChannelFileLoader channelLoader,
        TaskSchedulerService taskScheduler,
        EpgScheduler epgScheduler,
        LiveCommentStore liveComments,
        ExternalTunerLeaseService externalTuners,
        string pluginName,
        string dataDirectory)
    {
        _log = log;
        _epgStore = epgStore;
        _reservationStore = reservationStore;
        _tunerPool = tunerPool;
        _allocationRoute = allocationRoute;
        _channelLoader = channelLoader;
        _taskScheduler = taskScheduler;
        _epgScheduler = epgScheduler;
        _liveComments = liveComments;
        _externalTuners = externalTuners;
        _pluginName = SanitizePluginName(pluginName);
        DataDirectory = dataDirectory;
        PluginDataDirectory = Path.Combine(DataDirectory, "Plugins", _pluginName);
        Directory.CreateDirectory(PluginDataDirectory);
    }

    public IReadOnlyList<PluginEpgEvent> GetEpg(PluginEpgQuery? query = null)
    {
        query ??= new PluginEpgQuery();
        var from = query.From ?? DateTime.Now;
        var days = Math.Clamp(query.Days <= 0 ? 7 : query.Days, 1, 14);
        var to = query.To ?? from.AddDays(days);
        var limit = Math.Clamp(query.Limit <= 0 ? 2000 : query.Limit, 1, 10000);

        IEnumerable<EpgEvent> events = _epgStore.GetByRange(from, to);
        if (query.NetworkId.HasValue)
            events = events.Where(e => e.NetworkId == query.NetworkId.Value);
        if (query.TransportStreamId.HasValue)
            events = events.Where(e => e.TransportStreamId == query.TransportStreamId.Value);
        if (query.ServiceId.HasValue)
            events = events.Where(e => e.ServiceId == query.ServiceId.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            events = events.Where(e => Contains(EpgProjection.Title(e), kw) || Contains(EpgProjection.ShortText(e), kw) || Contains(EpgProjection.ExtendedText(e), kw));
        }
        if (!string.IsNullOrWhiteSpace(query.Genre))
        {
            var genre = query.Genre.Trim();
            events = events.Where(e => Contains(e.Genre, genre) || Contains(e.GenreCodes, genre));
        }

        return events
            .OrderBy(e => e.Start)
            .Take(limit)
            .Select(ToPluginEpgEvent)
            .ToList();
    }

    public IReadOnlyList<PluginReservation> GetReservations(PluginReservationQuery? query = null)
    {
        query ??= new PluginReservationQuery();
        IEnumerable<Reservation> reservations = _reservationStore.GetAll();
        if (!query.IncludeEpgSystemEntries)
            reservations = reservations.Where(r => r.Source != ReservationSource.Epg);
        if (query.IsEnabled.HasValue)
            reservations = reservations.Where(r => r.IsEnabled == query.IsEnabled.Value);
        if (query.IsConflicted.HasValue)
            reservations = reservations.Where(r => r.IsConflicted == query.IsConflicted.Value);
        if (!string.IsNullOrWhiteSpace(query.Source))
            reservations = reservations.Where(r => string.Equals(r.Source.ToString(), query.Source, StringComparison.OrdinalIgnoreCase));
        if (query.From.HasValue)
            reservations = reservations.Where(r => r.EndTime > query.From.Value);
        if (query.To.HasValue)
            reservations = reservations.Where(r => r.StartTime < query.To.Value);
        var activeRecordingIds = GetActiveTunerRecordingReservationIds();
        return reservations.OrderBy(r => r.StartTime).Select(r => ToPluginReservation(r, activeRecordingIds)).ToList();
    }

    public IReadOnlyList<PluginReservation> GetReservationHistory(PluginReservationHistoryQuery? query = null)
    {
        query ??= new PluginReservationHistoryQuery();
        var limit = Math.Clamp(query.Limit <= 0 ? 500 : query.Limit, 1, 5000);
        var activeRecordingIds = GetActiveTunerRecordingReservationIds();
        IEnumerable<Reservation> reservations = _reservationStore.GetAll()
            .Where(r => r.Status is ReservationStatus.Completed or ReservationStatus.Cancelled or ReservationStatus.Failed)
            .Where(r => !(r.Status == ReservationStatus.Failed && activeRecordingIds.Contains(r.Id)));
        if (query.From.HasValue)
            reservations = reservations.Where(r => r.EndTime >= query.From.Value);
        if (query.To.HasValue)
            reservations = reservations.Where(r => r.StartTime < query.To.Value);
        return reservations
            .OrderByDescending(r => r.EndTime)
            .Take(limit)
            .Select(r => ToPluginReservation(r, activeRecordingIds))
            .ToList();
    }

    public IReadOnlyList<PluginTunerStatus> GetTunerStatus()
        => _tunerPool.GetStatus().Select(s => new PluginTunerStatus
        {
            Name = s.Name,
            BonDriverFileName = s.BonDriverFileName,
            Did = s.Did,
            Group = s.Group,
            SlotIndex = s.SlotIndex,
            UsageKind = s.UsageKind.ToString(),
            ReservationId = s.ReservationId,
            ProcessId = s.ProcessId,
            PlannedEndTime = s.PlannedEndTime
        }).ToList();

    public IReadOnlyList<PluginConflictInfo> GetConflicts()
        => _reservationStore.GetAll()
            .Where(r => r.Source != ReservationSource.Epg && r.IsConflicted)
            .OrderBy(r => r.StartTime)
            .Select(r => new PluginConflictInfo
            {
                ReservationId = r.Id,
                Title = r.Title,
                ServiceName = r.ServiceName,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                Reason = "チューナー割当競合"
            })
            .ToList();

    public PluginReservationPreview PreviewReservationAllocation(PluginReservationDraft draft)
    {
        var overlap = _reservationStore.GetAll()
            .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
            .Where(r => r.EndTime > draft.StartTime && r.StartTime < draft.EndTime)
            .ToList();

        var sameServiceChain = GetChainCandidates(new PluginChainQuery
        {
            From = draft.StartTime.AddHours(-4),
            To = draft.EndTime.AddHours(4),
            ServiceId = draft.ServiceId == 0 ? null : draft.ServiceId
        });

        var conflicts = overlap
            .Where(r => r.IsConflicted)
            .Select(r => new PluginConflictInfo
            {
                ReservationId = r.Id,
                Title = r.Title,
                ServiceName = r.ServiceName,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                Reason = "既存予約が競合状態"
            })
            .ToList();

        return new PluginReservationPreview
        {
            CanReserve = conflicts.Count == 0,
            HasConflict = conflicts.Count > 0,
            Reason = conflicts.Count == 0 ? "登録可能見込みです。最終割当は本登録後にTvAIr本体が再評価します。" : "既存競合があります。",
            SuggestedTunerName = overlap.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.TunerName))?.TunerName ?? string.Empty,
            Conflicts = conflicts,
            ChainCandidates = sameServiceChain
        };
    }

    public IReadOnlyList<PluginChainInfo> GetChainCandidates(PluginChainQuery? query = null)
    {
        query ??= new PluginChainQuery();
        var from = query.From ?? DateTime.Now.AddHours(-1);
        var to = query.To ?? DateTime.Now.AddDays(7);

        var reservations = _reservationStore.GetAll()
            .Where(r => r.Source != ReservationSource.Epg && r.IsEnabled)
            .Where(r => r.EndTime > from && r.StartTime < to)
            .Where(r => !query.ServiceId.HasValue || r.ServiceId == query.ServiceId.Value)
            .OrderBy(r => r.StartTime)
            .ToList();

        var result = new List<PluginChainInfo>();
        for (var i = 1; i < reservations.Count; i++)
        {
            var prev = reservations[i - 1];
            var cur = reservations[i];
            var adjacent = cur.StartTime >= prev.StartTime && cur.StartTime <= prev.EndTime.AddMinutes(3);
            if (!adjacent && !cur.UserChainPreviousId.HasValue) continue;

            var sameTuner = !string.IsNullOrWhiteSpace(prev.TunerName)
                && string.Equals(prev.TunerName, cur.TunerName, StringComparison.OrdinalIgnoreCase);

            result.Add(new PluginChainInfo
            {
                PreviousReservationId = prev.Id,
                CurrentReservationId = cur.Id,
                CurrentProgramId = $"{cur.NetworkId}:{cur.TransportStreamId}:{cur.ServiceId}:{cur.EventId}",
                SameTuner = sameTuner,
                LossTarget = "previous",
                LossPart = "end",
                LossDescription = "前番組後半がカットされます",
                IsAllowed = sameTuner || cur.UserChainPreviousId == prev.Id
            });
        }
        return result;
    }

    public PluginChainPreview PreviewChainReservation(PluginReservationDraft draft)
    {
        var candidates = GetChainCandidates(new PluginChainQuery
        {
            From = draft.StartTime.AddHours(-4),
            To = draft.EndTime,
            ServiceId = draft.ServiceId == 0 ? null : draft.ServiceId
        });
        var selected = candidates.FirstOrDefault(c => c.PreviousReservationId == draft.ChainPreviousReservationId)
            ?? candidates.LastOrDefault(c => c.IsAllowed);
        return new PluginChainPreview
        {
            CanChain = selected is not null && selected.IsAllowed,
            Message = selected is null ? "チェーン候補はありません。" : selected.LossDescription,
            ChainInfo = selected
        };
    }

    public PluginReservationOperationResult AddReservation(PluginReservationDraft draft)
    {
        try
        {
            var reservation = new Reservation
            {
                NetworkId = draft.NetworkId,
                TransportStreamId = draft.TransportStreamId,
                ServiceId = draft.ServiceId,
                EventId = draft.EventId,
                Title = draft.Title,
                ServiceName = draft.ServiceName,
                StartTime = draft.StartTime.AddMinutes(-Math.Max(0, draft.PreMarginMinutes)),
                EndTime = draft.EndTime.AddMinutes(Math.Max(0, draft.PostMarginMinutes)),
                Status = ReservationStatus.Scheduled,
                Source = ReservationSource.Program,
                ChannelArgument = draft.ChannelArgument,
                IsEnabled = true,
                SourceRuleName = $"Plugin:{_pluginName}",
                IsUserChain = draft.AllowChain && draft.ChainPreviousReservationId.HasValue,
                UserChainPreviousId = draft.AllowChain ? draft.ChainPreviousReservationId : null,
                UserChainRootId = draft.AllowChain ? draft.ChainPreviousReservationId : null,
            };
            var id = _reservationStore.Add(reservation);
            ReevaluateAllocations("AddReservation");
            AddAuditLog("AddReservation", $"title={draft.Title} id=R{id} rule=release_contract");
            return new PluginReservationOperationResult { Success = true, ReservationId = id, Message = "予約を追加しました。" };
        }
        catch (Exception ex)
        {
            NotifyError($"予約追加に失敗しました: {ex.Message}");
            return new PluginReservationOperationResult { Success = false, Message = ex.Message };
        }
    }

    public PluginReservationOperationResult UpdateReservation(PluginReservationUpdate update)
    {
        try
        {
            var existing = _reservationStore.GetById(update.ReservationId);
            if (existing is null)
                return new PluginReservationOperationResult { Success = false, ReservationId = update.ReservationId, Message = "予約が見つかりません。" };
            if (!IsOwnedByThisPlugin(existing))
                return new PluginReservationOperationResult { Success = false, ReservationId = update.ReservationId, Message = "このプラグインが作成した予約ではありません。" };
            if (update.IsEnabled.HasValue)
                _reservationStore.UpdateEnabled(update.ReservationId, update.IsEnabled.Value);
            ReevaluateAllocations("UpdateReservation");
            AddAuditLog("UpdateReservation", $"id=R{update.ReservationId} enabled={update.IsEnabled} rule=release_contract");
            return new PluginReservationOperationResult { Success = true, ReservationId = update.ReservationId, Message = "予約を更新しました。" };
        }
        catch (Exception ex)
        {
            NotifyError($"予約更新に失敗しました: {ex.Message}");
            return new PluginReservationOperationResult { Success = false, ReservationId = update.ReservationId, Message = ex.Message };
        }
    }

    public PluginReservationOperationResult DeleteReservation(int reservationId, bool force = false)
    {
        try
        {
            var existing = _reservationStore.GetById(reservationId);
            if (existing is null)
                return new PluginReservationOperationResult { Success = false, ReservationId = reservationId, Message = "予約が見つかりません。" };
            if (!force && !IsOwnedByThisPlugin(existing))
                return new PluginReservationOperationResult { Success = false, ReservationId = reservationId, Message = "このプラグインが作成した予約ではありません。" };
            _reservationStore.Delete(reservationId);
            ReevaluateAllocations("DeleteReservation");
            AddAuditLog("DeleteReservation", $"id=R{reservationId} force={force} rule=release_contract");
            return new PluginReservationOperationResult { Success = true, ReservationId = reservationId, Message = "予約を削除しました。" };
        }
        catch (Exception ex)
        {
            NotifyError($"予約削除に失敗しました: {ex.Message}");
            return new PluginReservationOperationResult { Success = false, ReservationId = reservationId, Message = ex.Message };
        }
    }


    // ─── release_contract Phase 2 読み取りAPI拡張 ──────────────────────

    public PluginProgramGuideSnapshot GetProgramGuideSnapshot(PluginProgramGuideQuery? query = null)
    {
        query ??= new PluginProgramGuideQuery();
        var channelQuery = new PluginProgramGuideChannelQuery
        {
            DisplayGroup = query.DisplayGroup,
            ProgramGuideFilterGroup = query.ProgramGuideFilterGroup,
            AllocationGroup = query.AllocationGroup,
            Limit = query.Limit
        };
        var channels = GetProgramGuideChannels(channelQuery);
        var snapshotAt = DateTime.Now;
        var revision = BuildProgramGuideRevision(snapshotAt);
        return new PluginProgramGuideSnapshot
        {
            SnapshotAt = snapshotAt,
            Revision = revision,
            Channels = channels,
            NowNext = query.IncludeNowNext
                ? BuildProgramGuideNowNext(channels, snapshotAt, revision)
                : Array.Empty<PluginProgramGuideNowNext>()
        };
    }

    public IReadOnlyList<PluginProgramGuideChannel> GetProgramGuideChannels(PluginProgramGuideChannelQuery? query = null)
    {
        query ??= new PluginProgramGuideChannelQuery();
        var limit = Math.Clamp(query.Limit <= 0 ? 500 : query.Limit, 1, 2000);
        var targets = _channelLoader.Load().Targets
            .Select((c, index) => ToProgramGuideChannel(c, index))
            .Where(c => MatchesProgramGuideChannelQuery(c, query))
            .Take(limit)
            .ToList();
        return targets;
    }

    public IReadOnlyList<PluginProgramGuideNowNext> GetProgramGuideNowNext(PluginProgramGuideNowNextQuery? query = null)
    {
        query ??= new PluginProgramGuideNowNextQuery();
        var at = query.At ?? DateTime.Now;
        var revision = BuildProgramGuideRevision(at);
        var channels = GetProgramGuideChannels(query);
        return BuildProgramGuideNowNext(channels, at, revision);
    }

    public IReadOnlyList<PluginViewerSessionInfo> GetViewerSessions(PluginViewerSessionQuery? query = null)
    {
        query ??= new PluginViewerSessionQuery();
        IEnumerable<ExternalTunerLeaseDto> leases = _externalTuners.GetActiveLeases();
        if (!string.IsNullOrWhiteSpace(query.AllocationGroup))
        {
            var requested = NormalizeAllocationGroup(query.AllocationGroup);
            leases = leases.Where(l => string.Equals(NormalizeAllocationGroup(l.Group), requested, StringComparison.OrdinalIgnoreCase));
        }
        var requestedFilterGroup = !string.IsNullOrWhiteSpace(query.ProgramGuideFilterGroup) ? query.ProgramGuideFilterGroup : query.DisplayGroup;
        if (!string.IsNullOrWhiteSpace(requestedFilterGroup))
        {
            var display = NormalizeProgramGuideFilterGroup(requestedFilterGroup);
            leases = leases.Where(l => string.Equals(DisplayGroupFromAllocation(l.Group, l.NetworkId), display, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.ClientId))
            leases = leases.Where(l => string.Equals(l.ClientId ?? string.Empty, query.ClientId.Trim(), StringComparison.OrdinalIgnoreCase));

        return leases
            .OrderBy(l => l.AcquiredAt)
            .Select(l =>
            {
                var filterGroup = DisplayGroupFromAllocation(l.Group, l.NetworkId);
                var allocation = NormalizeAllocationGroup(l.Group);
                var serviceName = ResolveViewerSessionServiceName(l.NetworkId, l.TransportStreamId, l.ServiceId);
                return new PluginViewerSessionInfo
                {
                    LeaseId = l.LeaseId,
                    Source = l.Source,
                    ClientId = l.ClientId ?? string.Empty,
                    DisplayGroup = filterGroup,
                    ProgramGuideFilterGroup = filterGroup,
                    AllocationGroup = allocation,
                    TunerGroup = allocation,
                    TunerName = l.TunerName,
                    BonDriverFileName = l.BonDriverFileName,
                    Did = l.Did,
                    Current = true,
                    ViewerState = string.IsNullOrWhiteSpace(l.ViewerState) ? "active" : l.ViewerState,
                    ServiceName = serviceName,
                    SlotIndex = l.SlotIndex,
                    AcquiredAt = l.AcquiredAt,
                    ProcessId = l.ProcessId,
                    NetworkId = l.NetworkId,
                    TransportStreamId = l.TransportStreamId,
                    ServiceId = l.ServiceId,
                    ChannelSpace = l.ChannelSpace,
                    ChannelIndex = l.ChannelIndex,
                    State = l.ViewerState,
                    LaunchResult = l.LaunchResult,
                    TuneResult = l.TuneResult,
                    ActivateResult = l.ActivateResult,
                    RollbackResult = l.RollbackResult,
                    ChannelArgument = l.ChannelArgument ?? string.Empty,
                    ViewerProfile = l.ViewerProfileId,
                    ViewerProfileName = l.ViewerProfileName,
                    TvTestPathKey = l.TvTestPathKey ?? string.Empty
                };
            })
            .ToList();
    }

    private string ResolveViewerSessionServiceName(ushort? networkId, ushort? transportStreamId, ushort? serviceId)
    {
        if (!networkId.HasValue || !transportStreamId.HasValue || !serviceId.HasValue) return string.Empty;
        try
        {
            return _channelLoader.Load().Targets
                .FirstOrDefault(c => c.OriginalNetworkId == networkId.Value
                                  && c.TransportStreamId == transportStreamId.Value
                                  && c.ServiceId == serviceId.Value)?.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public IReadOnlyList<PluginViewerTunerInfo> GetViewerTuners(PluginViewerTunerQuery? query = null)
    {
        query ??= new PluginViewerTunerQuery();
        var leases = _externalTuners.GetActiveLeases().ToList();
        var lastViewerActionResult = _externalTuners.GetLastViewerActionResult();
        IEnumerable<TunerSlotStatus> tuners = _tunerPool.GetStatus();
        if (!query.IncludeRecordingRole)
            tuners = tuners.Where(t => string.Equals(t.Role, "Viewing", StringComparison.OrdinalIgnoreCase));
        if (!query.IncludeBusy)
            tuners = tuners.Where(t => t.UsageKind == TunerUsageKind.Free);
        if (!string.IsNullOrWhiteSpace(query.AllocationGroup))
        {
            var requested = NormalizeAllocationGroup(query.AllocationGroup);
            tuners = tuners.Where(t => string.Equals(NormalizeAllocationGroup(t.Group), requested, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query.ProgramGuideFilterGroup))
        {
            var requested = NormalizeProgramGuideFilterGroup(query.ProgramGuideFilterGroup);
            tuners = tuners.Where(t => string.Equals(ProgramGuideFilterGroupFromTunerGroup(t.Group), requested, StringComparison.OrdinalIgnoreCase));
        }

        return tuners
            .OrderBy(t => t.Group)
            .ThenBy(t => t.SlotIndex)
            .Select(t =>
            {
                var lease = leases.FirstOrDefault(l => string.Equals(l.Did, t.Did, StringComparison.OrdinalIgnoreCase) || string.Equals(l.TunerName, t.Name, StringComparison.OrdinalIgnoreCase));
                var allocation = NormalizeAllocationGroup(t.Group);
                var filterGroup = lease is not null ? DisplayGroupFromAllocation(lease.Group, lease.NetworkId) : ProgramGuideFilterGroupFromTunerGroup(t.Group);
                return new PluginViewerTunerInfo
                {
                    Name = t.Name,
                    SlotIndex = t.SlotIndex,
                    Did = t.Did,
                    ProgramGuideFilterGroup = filterGroup,
                    DisplayGroup = filterGroup,
                    AllocationGroup = allocation,
                    TunerGroup = allocation,
                    Role = t.Role,
                    UsageKind = t.UsageKind.ToString(),
                    Busy = t.UsageKind != TunerUsageKind.Free || lease is not null,
                    IsViewingRole = string.Equals(t.Role, "Viewing", StringComparison.OrdinalIgnoreCase),
                    IsSelectableForViewer = string.Equals(t.Role, "Viewing", StringComparison.OrdinalIgnoreCase) && t.UsageKind == TunerUsageKind.Free && lease is null,
                    BonDriverFileName = t.BonDriverFileName,
                    CurrentLeaseId = lease?.LeaseId ?? string.Empty,
                    Availability = t.UsageKind == TunerUsageKind.Free && lease is null ? "Available" : "Busy",
                    OccupiedBy = lease is not null ? "TvAIrViewerLease" : string.Empty,
                    ExternalPid = null,
                    ExternalBusyReason = string.Empty,
                    AvailabilityMessage = string.Empty,
                    LastViewerActionState = lastViewerActionResult?.State ?? string.Empty,
                    LastViewerActionErrorCode = lastViewerActionResult?.ErrorCode ?? string.Empty,
                    LastViewerActionMessage = lastViewerActionResult?.Message ?? string.Empty,
                    NetworkId = lease?.NetworkId,
                    TransportStreamId = lease?.TransportStreamId,
                    ServiceId = lease?.ServiceId
                };
            })
            .ToList();
    }



    public IReadOnlyList<PluginViewerControlChannelInfo> GetViewerControlChannels(PluginProgramGuideChannelQuery? query = null)
    {
        query ??= new PluginProgramGuideChannelQuery();
        return GetProgramGuideChannels(query)
            .Select(ToViewerControlChannelInfo)
            .ToList();
    }


    public IReadOnlyList<PluginProgramGuideWaveFilterInfo> GetProgramGuideWaveFilters()
        => BuildProgramGuideWaveFilters();

    public PluginViewerControlHostContract GetViewerControlHostContract() => BuildViewerControlHostContract();

    public IReadOnlyList<PluginChannelInfo> GetChannels(PluginChannelQuery? query = null)
    {
        query ??= new PluginChannelQuery();
        IEnumerable<ChannelTarget> targets = _channelLoader.Load().Targets;
        if (!string.IsNullOrWhiteSpace(query.Group))
            targets = targets.Where(c => string.Equals(c.Group, query.Group, StringComparison.OrdinalIgnoreCase));
        if (query.NetworkId.HasValue)
            targets = targets.Where(c => c.OriginalNetworkId == query.NetworkId.Value);
        if (query.TransportStreamId.HasValue)
            targets = targets.Where(c => c.TransportStreamId == query.TransportStreamId.Value);
        if (query.ServiceId.HasValue)
            targets = targets.Where(c => c.ServiceId == query.ServiceId.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            targets = targets.Where(c => Contains(c.Name, query.Keyword.Trim()));

        return targets
            .OrderBy(c => c.Group)
            .ThenBy(c => c.ResolvedSpace)
            .ThenBy(c => c.ResolvedChannelIndex)
            .ThenBy(c => c.ServiceId)
            .Select(c => new PluginChannelInfo
            {
                Group = c.Group,
                ServiceName = c.Name,
                NetworkId = c.OriginalNetworkId,
                TransportStreamId = c.TransportStreamId,
                ServiceId = c.ServiceId,
                Nid = c.OriginalNetworkId,
                Tsid = c.TransportStreamId,
                Sid = c.ServiceId,
                ChannelSpace = c.ResolvedSpace,
                ChannelIndex = c.ResolvedChannelIndex,
                ChannelArgument = c.ChannelArgument,
                IsEnabledInUserChannelSet = true
            })
            .ToList();
    }

    public IReadOnlyList<PluginRecordingSessionInfo> GetRecordingSessions()
    {
        var reservations = _reservationStore.GetAll().ToDictionary(r => r.Id);
        return _tunerPool.GetStatus()
            .Where(s => s.UsageKind == TunerUsageKind.Recording && s.ReservationId.HasValue)
            .OrderBy(s => s.Group)
            .ThenBy(s => s.Name)
            .Select(s =>
            {
                reservations.TryGetValue(s.ReservationId!.Value, out var r);
                return new PluginRecordingSessionInfo
                {
                    ReservationId = s.ReservationId.Value,
                    ServiceName = r?.ServiceName ?? string.Empty,
                    Title = r?.Title ?? string.Empty,
                    Group = s.Group,
                    TunerName = s.Name,
                    ProcessId = s.ProcessId,
                    StartedAt = r?.RecordingStartedAt ?? DateTime.MinValue,
                    PlannedEnd = s.PlannedEndTime ?? r?.EndTime ?? DateTime.MinValue
                };
            })
            .ToList();
    }

    public IReadOnlyList<PluginWakePlanItem> GetWakePlan(PluginWakePlanQuery? query = null)
    {
        query ??= new PluginWakePlanQuery();
        var limit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 500);
        return _taskScheduler.GetWakePlanSnapshot(query.From, query.To, limit)
            .Select(w => new PluginWakePlanItem
            {
                At = w.At,
                Kind = w.Kind,
                ReservationId = w.ReservationId,
                Title = w.Title,
                TaskName = w.TaskName
            })
            .ToList();
    }

    public PluginViewerOperationResult RequestViewerStart(PluginViewerStartRequest request)
    {
        AddAuditLog("RequestViewerStart", $"service={request.ServiceName} nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} result=accepted_as_request_only rule=release_contract");
        return new PluginViewerOperationResult { Success = false, Message = "視聴操作APIはPhase 2では読み取りAPI拡張の対象外です。" };
    }

    public PluginViewerOperationResult RequestViewerStop(PluginViewerStopRequest request)
    {
        AddAuditLog("RequestViewerStop", $"nid={request.NetworkId} tsid={request.TransportStreamId} sid={request.ServiceId} result=accepted_as_request_only rule=release_contract");
        return new PluginViewerOperationResult { Success = false, Message = "視聴操作APIはPhase 2では読み取りAPI拡張の対象外です。" };
    }

    public IReadOnlyList<PluginEpgEvent> GetCurrentPrograms(PluginCurrentProgramQuery? query = null)
    {
        query ??= new PluginCurrentProgramQuery();
        var now = DateTime.Now;
        var from = now.AddMinutes(-Math.Clamp(query.WindowMinutesBefore, 0, 180));
        var to = now.AddMinutes(Math.Clamp(query.WindowMinutesAfter, 0, 180));
        var limit = Math.Clamp(query.Limit <= 0 ? 200 : query.Limit, 1, 1000);
        IEnumerable<EpgEvent> events = _epgStore.GetByRange(from, to)
            .Where(e => e.Start <= now && e.End > now);
        if (query.NetworkId.HasValue)
            events = events.Where(e => e.NetworkId == query.NetworkId.Value);
        if (query.TransportStreamId.HasValue)
            events = events.Where(e => e.TransportStreamId == query.TransportStreamId.Value);
        if (query.ServiceId.HasValue)
            events = events.Where(e => e.ServiceId == query.ServiceId.Value);
        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            var channels = GetChannels(new PluginChannelQuery { Group = query.Group })
                .Select(c => (c.NetworkId, c.TransportStreamId, c.ServiceId))
                .ToHashSet();
            events = events.Where(e => channels.Contains((e.NetworkId, e.TransportStreamId, e.ServiceId)));
        }
        return events.OrderBy(e => e.ServiceName).ThenBy(e => e.Start).Take(limit).Select(ToPluginEpgEvent).ToList();
    }

    public IReadOnlyList<PluginKeywordRuleInfo> GetKeywordRules(PluginRuleQuery? query = null)
    {
        query ??= new PluginRuleQuery();
        IEnumerable<KeywordRule> rules = _reservationStore.GetKeywordRules();
        if (query.Enabled.HasValue)
            rules = rules.Where(r => r.Enabled == query.Enabled.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            rules = rules.Where(r => Contains(r.Name, kw) || Contains(r.Pattern, kw) || Contains(r.ExcludePattern, kw));
        }
        var limit = Math.Clamp(query.Limit <= 0 ? 500 : query.Limit, 1, 5000);
        return rules.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Take(limit).Select(r => new PluginKeywordRuleInfo
        {
            Id = r.Id,
            Name = r.Name,
            Pattern = r.Pattern,
            ExcludePattern = r.ExcludePattern,
            UseRegex = r.UseRegex,
            Enabled = r.Enabled,
            UseAllChannels = r.UseAllChannels,
            TargetServices = r.TargetServices,
            TargetGenres = r.TargetGenres,
            TargetDays = r.TargetDays,
            UseTimeRange = r.UseTimeRange,
            StartTime = r.StartTime,
            EndTime = r.EndTime,
            SortOrder = r.SortOrder,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList();
    }

    public IReadOnlyList<PluginProgramRuleInfo> GetProgramRules(PluginRuleQuery? query = null)
    {
        query ??= new PluginRuleQuery();
        IEnumerable<ProgramRule> rules = _reservationStore.GetProgramRules();
        if (query.Enabled.HasValue)
            rules = rules.Where(r => r.Enabled == query.Enabled.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            rules = rules.Where(r => Contains(r.Name, kw));
        }
        var limit = Math.Clamp(query.Limit <= 0 ? 500 : query.Limit, 1, 5000);
        return rules.OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartTime).ThenBy(r => r.Id).Take(limit).Select(r => new PluginProgramRuleInfo
        {
            Id = r.Id,
            Name = r.Name,
            DayOfWeek = r.DayOfWeek,
            StartTime = r.StartTime,
            EndTime = r.EndTime,
            NetworkId = r.NetworkId,
            TransportStreamId = r.TransportStreamId,
            ServiceId = r.ServiceId,
            ExpiresOn = r.ExpiresOn,
            Enabled = r.Enabled,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList();
    }

    public IReadOnlyList<PluginLogItem> GetLogs(PluginLogQuery? query = null)
    {
        query ??= new PluginLogQuery();
        var count = Math.Clamp(query.Count <= 0 ? 200 : query.Count, 1, 2000);
        IEnumerable<LogEntry> logs = _log.GetRecent(count);
        if (!string.IsNullOrWhiteSpace(query.Event))
            logs = logs.Where(l => string.Equals(l.Event, query.Event.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            logs = logs.Where(l => Contains(l.Event, kw) || Contains(l.Title, kw) || Contains(l.Message, kw));
        }
        return logs.Select(l => new PluginLogItem
        {
            Event = l.Event,
            Title = l.Title,
            Message = l.Message,
            CreatedAt = l.CreatedAt
        }).ToList();
    }

    public PluginEpgRunStateInfo GetEpgRunState()
    {
        var s = _epgScheduler.GetRunState();
        return new PluginEpgRunStateInfo
        {
            IsRunning = s.IsRunning,
            CanStart = s.CanStart,
            CanCancel = s.CanCancel,
            Source = s.Source,
            TargetScope = s.TargetScope,
            Silent = s.Silent,
            UiMode = s.UiMode,
            CancelRoute = s.CancelRoute
        };
    }

    public PluginSystemStatusInfo GetSystemStatus()
    {
        var tuners = _tunerPool.GetStatus().ToList();
        var wake = _taskScheduler.GetWakePlanSnapshot(limit: 13).ToList();
        var activeRecordingIds = GetActiveTunerRecordingReservationIds();
        return new PluginSystemStatusInfo
        {
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty,
            Now = DateTime.Now,
            ReservationCount = _reservationStore.GetAll().Count(r => r.Source != ReservationSource.Epg),
            ActiveRecordingCount = activeRecordingIds.Count,
            TunerCount = tuners.Count,
            FreeTunerCount = tuners.Count(s => s.UsageKind == TunerUsageKind.Free),
            WakePlanCount = wake.Count,
            NextWakeAt = wake.OrderBy(w => w.At).FirstOrDefault()?.At
        };
    }

    public PluginThemeInfo GetThemeInfo() => new()
    {
        Appearance = "system",
        AccentColor = string.Empty,
        CssScopeRoot = "tvair"
    };

    public string? ReadPluginFile(string relativePath)
    {
        var path = ResolvePluginPath(relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WritePluginFile(string relativePath, string content)
    {
        var path = ResolvePluginPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? string.Empty);
    }

    public string? ReadPluginSettings(string name = "settings.json") => ReadPluginFile(name);
    public void WritePluginSettings(string content, string name = "settings.json") => WritePluginFile(name, content);


    public void PublishLiveComment(LiveCommentEvent comment)
    {
        try
        {
            var enriched = string.IsNullOrWhiteSpace(comment.PluginId)
                ? new LiveCommentEvent
                {
                    PluginId = _pluginName,
                    ReservationId = comment.ReservationId,
                    ServiceName = comment.ServiceName,
                    ProgramTitle = comment.ProgramTitle,
                    JkChannel = comment.JkChannel,
                    UnixTime = comment.UnixTime,
                    Vpos = comment.Vpos,
                    UserId = comment.UserId,
                    Mail = comment.Mail,
                    Content = comment.Content,
                    ReceivedAt = comment.ReceivedAt
                }
                : comment;
            _liveComments.Add(enriched);
        }
        catch (Exception ex)
        {
            _log.Add("PLUGIN_LIVE_COMMENT_ERROR", _pluginName, $"reservationId={comment?.ReservationId} message={ex.Message}");
        }
    }

    public void Log(string message) => Log(PluginLogLevel.Info, message);

    public void Log(PluginLogLevel level, string message)
        => _log.Add("Plugin", _pluginName, $"[{level}] {message}");

    public void NotifyInfo(string message) => Log(PluginLogLevel.Info, $"NotifyInfo: {message}");
    public void NotifyWarning(string message) => Log(PluginLogLevel.Warning, $"NotifyWarning: {message}");
    public void NotifyError(string message) => Log(PluginLogLevel.Error, $"NotifyError: {message}");
    public void AddTimelineEvent(string title, string message) => _log.Add("PLUGIN_TIMELINE", title, $"plugin={_pluginName} {message}");
    public void AddAuditLog(string action, string message) => _log.Add("PLUGIN_AUDIT", action, $"plugin={_pluginName} {message}");

    private void ReevaluateAllocations(string action)
    {
        _allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "Plugin",
            Action: action,
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: true,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: false,
            RefreshWakeTask: true));
    }

    private bool IsOwnedByThisPlugin(Reservation reservation)
        => string.Equals(reservation.SourceRuleName, $"Plugin:{_pluginName}", StringComparison.OrdinalIgnoreCase);

    private string ResolvePluginPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("プラグインファイル名が空です。");
        var combined = Path.GetFullPath(Path.Combine(PluginDataDirectory, relativePath));
        var root = Path.GetFullPath(PluginDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PluginDataDirectory外へのアクセスは禁止です。");
        return combined;
    }

    private IReadOnlyList<PluginProgramGuideNowNext> BuildProgramGuideNowNext(IReadOnlyList<PluginProgramGuideChannel> channels, DateTime at, long revision)
    {
        if (channels.Count == 0) return Array.Empty<PluginProgramGuideNowNext>();
        var from = at.AddHours(-8);
        var to = at.AddHours(12);
        var rangeEvents = _epgStore.GetByRange(from, to)
            .OrderBy(e => e.Start).ThenBy(e => e.EventId)
            .ToList();
        var byExact = rangeEvents
            .GroupBy(e => PluginProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var fallbackContext = BuildPluginProgramGuideProjectionFallbackContext(rangeEvents);

        var exact = 0;
        var nameSid = 0;
        var groupSid = 0;
        var exactNoOverlap = 0;
        var unresolved = 0;

        var result = channels.Select(ch =>
        {
            var list = ResolvePluginProgramGuideChannelEvents(ch, byExact, fallbackContext, from, to, out var resolveSource);
            switch (resolveSource)
            {
                case "exact": exact++; break;
                case "name_sid_unique": nameSid++; break;
                case "group_sid_unique": groupSid++; break;
                case "exact_no_range_overlap": exactNoOverlap++; break;
                default: unresolved++; break;
            }

            var current = list.FirstOrDefault(e => e.Start <= at && e.End > at);
            var next = list.Where(e => e.Start >= at)
                .OrderBy(e => e.Start)
                .FirstOrDefault(e => current is null || e.EventId != current.EventId)
                ?? list.Where(e => current is not null && e.Start >= current.End).OrderBy(e => e.Start).FirstOrDefault();
            var availability = list.Count == 0 ? "NoEpg"
                : current is null ? "NoCurrent"
                : next is null ? "NoNext"
                : "OK";
            return new PluginProgramGuideNowNext
            {
                Channel = ch,
                Current = current is null ? null : ToPluginEpgEvent(current),
                Next = next is null ? null : ToPluginEpgEvent(next),
                Availability = availability,
                SnapshotAt = at,
                Revision = revision
            };
        }).ToList();

        if (nameSid > 0 || groupSid > 0 || unresolved > 0)
        {
            var unresolvedSample = string.Join("/", result
                .Where(x => string.Equals(x.Availability, "NoEpg", StringComparison.OrdinalIgnoreCase))
                .Select(x => PluginProgramGuideAuditName(x.Channel.ServiceName))
                .Take(6));
            _log.Add("PLUGIN_PROGRAMGUIDE_NOW_NEXT_RESOLVE_AUDIT", _pluginName,
                $"result=OK channels={channels.Count} events={rangeEvents.Count} exact={exact} nameSid={nameSid} groupSid={groupSid} exactNoRangeOverlap={exactNoOverlap} unresolved={unresolved} unresolvedSample={(string.IsNullOrWhiteSpace(unresolvedSample) ? "-" : unresolvedSample)} commonResolver=programguide_projection_fallback rule=release_contract");
        }

        return result;
    }

    private static string PluginProgramGuideServiceKey3(ushort networkId, ushort transportStreamId, ushort serviceId)
        => $"{networkId}:{transportStreamId}:{serviceId}";

    private static string PluginProgramGuideEventServiceKey(EpgEvent e)
        => PluginProgramGuideServiceKey3(e.NetworkId, e.TransportStreamId, e.ServiceId);

    private static string PluginProgramGuideChannelServiceKey(PluginProgramGuideChannel ch)
        => PluginProgramGuideServiceKey3(ch.NetworkId, ch.TransportStreamId, ch.ServiceId);

    private static string NormalizePluginProgramGuideServiceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant();
        var chars = normalized.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(chars);
    }

    private static string PluginProgramGuideWaveGroupFromNetworkId(ushort networkId)
    {
        if (networkId == 4) return "BS";
        if (networkId == 6 || networkId == 7) return "CS";
        return "GR";
    }

    private static PluginProgramGuideProjectionFallbackContext BuildPluginProgramGuideProjectionFallbackContext(IReadOnlyList<EpgEvent> events)
    {
        var ctx = new PluginProgramGuideProjectionFallbackContext();

        foreach (var g in events.GroupBy(e => $"{PluginProgramGuideWaveGroupFromNetworkId(e.NetworkId)}:{e.ServiceId}:{NormalizePluginProgramGuideServiceName(e.ServiceName)}", StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(g.Key) || g.Key.EndsWith(":", StringComparison.Ordinal)) continue;
            var identityCount = g.Select(PluginProgramGuideEventServiceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (identityCount == 1)
                ctx.ByNameSid[g.Key] = g.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).ToList();
        }

        foreach (var g in events.GroupBy(e => $"{PluginProgramGuideWaveGroupFromNetworkId(e.NetworkId)}:{e.ServiceId}", StringComparer.OrdinalIgnoreCase))
        {
            var identityCount = g.Select(PluginProgramGuideEventServiceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (identityCount == 1)
                ctx.ByGroupSidUnique[g.Key] = g.OrderBy(e => e.Start).ThenBy(e => e.End).ThenBy(e => e.EventId).ToList();
        }

        return ctx;
    }

    private static IReadOnlyList<EpgEvent> ResolvePluginProgramGuideChannelEvents(
        PluginProgramGuideChannel ch,
        IReadOnlyDictionary<string, List<EpgEvent>> byExact,
        PluginProgramGuideProjectionFallbackContext fallbackContext,
        DateTime rangeStart,
        DateTime rangeEnd,
        out string resolveSource)
    {
        var exactKey = PluginProgramGuideChannelServiceKey(ch);
        if (byExact.TryGetValue(exactKey, out var exact) && exact.Any(e => e.End > rangeStart && e.Start < rangeEnd))
        {
            resolveSource = "exact";
            return exact;
        }

        var waveGroup = PluginProgramGuideWaveGroupFromNetworkId(ch.NetworkId);
        var nameKey = $"{waveGroup}:{ch.ServiceId}:{NormalizePluginProgramGuideServiceName(ch.ServiceName)}";
        if (fallbackContext.ByNameSid.TryGetValue(nameKey, out var byNameSid) && byNameSid.Any(e => e.End > rangeStart && e.Start < rangeEnd))
        {
            resolveSource = "name_sid_unique";
            return byNameSid;
        }

        var groupSidKey = $"{waveGroup}:{ch.ServiceId}";
        if (fallbackContext.ByGroupSidUnique.TryGetValue(groupSidKey, out var byGroupSid) && byGroupSid.Any(e => e.End > rangeStart && e.Start < rangeEnd))
        {
            resolveSource = "group_sid_unique";
            return byGroupSid;
        }

        if (byExact.TryGetValue(exactKey, out var exactAny))
        {
            resolveSource = "exact_no_range_overlap";
            return exactAny;
        }

        resolveSource = "none";
        return Array.Empty<EpgEvent>();
    }

    private static string PluginProgramGuideAuditName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var v = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return v.Length <= 32 ? v : v[..32] + "…";
    }

    private sealed class PluginProgramGuideProjectionFallbackContext
    {
        public Dictionary<string, List<EpgEvent>> ByNameSid { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<EpgEvent>> ByGroupSidUnique { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static PluginProgramGuideChannel ToProgramGuideChannel(ChannelTarget c, int index)
    {
        var filterGroup = ProgramGuideFilterGroupFromTarget(c);
        var allocation = NormalizeAllocationGroup(c.Group);
        return new PluginProgramGuideChannel
        {
            ProgramGuideOrder = index,
            DisplayGroup = filterGroup,
            ProgramGuideFilterGroup = filterGroup,
            ProgramGuideFilterKey = filterGroup,
            ProgramGuideFilterLabel = ProgramGuideFilterLabel(filterGroup),
            BroadcastGroup = filterGroup,
            AllocationGroup = allocation,
            TunerGroup = allocation,
            ServiceName = c.Name,
            NetworkId = c.OriginalNetworkId,
            TransportStreamId = c.TransportStreamId,
            ServiceId = c.ServiceId,
            Nid = c.OriginalNetworkId,
            Tsid = c.TransportStreamId,
            Sid = c.ServiceId,
            ChannelSpace = c.ResolvedSpace,
            ChannelIndex = c.ResolvedChannelIndex,
            ChannelArgument = c.ChannelArgument,
            IsProgramGuideVisible = true,
            IsEnabledInUserChannelSet = true
        };
    }



    private static PluginViewerControlChannelInfo ToViewerControlChannelInfo(PluginProgramGuideChannel c)
    {
        var ready = c.NetworkId != 0 && c.TransportStreamId != 0 && c.ServiceId != 0;
        return new PluginViewerControlChannelInfo
        {
            ProgramGuideOrder = c.ProgramGuideOrder,
            ServiceName = c.ServiceName,
            NetworkId = c.NetworkId,
            TransportStreamId = c.TransportStreamId,
            ServiceId = c.ServiceId,
            Nid = c.NetworkId,
            Tsid = c.TransportStreamId,
            Sid = c.ServiceId,
            ProgramGuideFilterGroup = c.ProgramGuideFilterGroup,
            ProgramGuideFilterKey = c.ProgramGuideFilterKey,
            ProgramGuideFilterLabel = c.ProgramGuideFilterLabel,
            BroadcastGroup = c.BroadcastGroup,
            AllocationGroup = c.AllocationGroup,
            TunerGroup = c.TunerGroup,
            ChannelSpace = c.ChannelSpace,
            ChannelIndex = c.ChannelIndex,
            ChannelArgument = c.ChannelArgument,
            IdentitySource = "ProgramGuideProjection",
            ViewerStartIdentityReady = ready
        };
    }


    private static bool MatchesProgramGuideChannelQuery(PluginProgramGuideChannel c, PluginProgramGuideChannelQuery query)
    {
        var requestedDisplayGroup = !string.IsNullOrWhiteSpace(query.ProgramGuideFilterGroup) ? query.ProgramGuideFilterGroup : query.DisplayGroup;
        if (!string.IsNullOrWhiteSpace(requestedDisplayGroup) && !string.Equals(c.ProgramGuideFilterGroup, NormalizeProgramGuideFilterGroup(requestedDisplayGroup), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.AllocationGroup) && !string.Equals(c.AllocationGroup, NormalizeAllocationGroup(query.AllocationGroup), StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.NetworkId.HasValue && c.NetworkId != query.NetworkId.Value)
            return false;
        if (query.TransportStreamId.HasValue && c.TransportStreamId != query.TransportStreamId.Value)
            return false;
        if (query.ServiceId.HasValue && c.ServiceId != query.ServiceId.Value)
            return false;
        return true;
    }

    private static long BuildProgramGuideRevision(DateTime snapshotAt) => snapshotAt.Ticks;

    private static string DisplayGroupFromTarget(ChannelTarget c) => ProgramGuideFilterGroupFromTarget(c);

    /// <summary>番組表UIの放送波フィルタ分類を唯一の正としてプラグインへ投影する。</summary>
    private static string ProgramGuideFilterGroupFromTarget(ChannelTarget c)
    {
        var group = (c.Group ?? string.Empty).Trim().ToUpperInvariant();
        if (group == "GR") return "GR";
        if (group == "BS") return "BS";
        if (group == "CS") return "CS";
        if (group == "BSCS")
            return c.OriginalNetworkId == 4 ? "BS" : "CS";
        return string.IsNullOrWhiteSpace(group) ? "GR" : group;
    }


    private static string ProgramGuideFilterGroupFromTunerGroup(string? group)
    {
        var allocation = NormalizeAllocationGroup(group);
        return allocation == "GR" ? "GR" : "BSCS";
    }

    private static string NormalizeProgramGuideFilterGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "地上波" or "GROUND" => "GR",
            "BS" => "BS",
            "CS" => "CS",
            "BS/CS" or "BSCS" => "BSCS",
            _ => string.IsNullOrWhiteSpace(g) ? string.Empty : g
        };
    }

    private static string ProgramGuideFilterLabel(string? group)
    {
        var g = NormalizeProgramGuideFilterGroup(group);
        return g switch
        {
            "GR" => "地上波",
            "BS" => "BS",
            "CS" => "CS",
            "BSCS" => "BS/CS",
            _ => g
        };
    }

    private static IReadOnlyList<PluginProgramGuideWaveFilterInfo> BuildProgramGuideWaveFilters() => new[]
    {
        new PluginProgramGuideWaveFilterInfo { Key = "GR", Group = "GR", Label = "地上波", Order = 0, IsProgramGuideFilter = true },
        new PluginProgramGuideWaveFilterInfo { Key = "BS", Group = "BS", Label = "BS", Order = 1, IsProgramGuideFilter = true },
        new PluginProgramGuideWaveFilterInfo { Key = "CS", Group = "CS", Label = "CS", Order = 2, IsProgramGuideFilter = true }
    };

    private static PluginViewerControlHostContract BuildViewerControlHostContract() => new()
    {
        ContractVersion = TvAIrVersionContract.PluginHostContractVersion,
        ToolWindowOnlySafeEvents = true,
        PluginScriptAllowed = false,
        SupportedEvents = new[] { "dblclick", "click" },
        SupportedActions = new[] { "viewerStart", "viewerStop", "refreshWindow", "updateWindow" },
        ViewerStartPayloadFields = "serviceId|networkId|transportStreamId|programGuideFilterGroup|broadcastGroup|allocationGroup|tunerGroup|channelSpace|channelIndex|viewerProfile|windowId|responseMode|refreshAfter|refreshTarget|preserveViewerWindowState|viewerActivation|retuneExistingViewer",
        ViewerStartPreferredTunerFields = "preferredTunerName|preferredDid|preferredSlot",
        ProgramGuideFilterSource = "TvAIr program guide wave filter module",
        ProgramGuideFilterField = "programGuideFilterGroup",
        TunerGroupField = "tunerGroup",
        ViewerTunersEndpoint = "/api/plugins/viewer-tuners",
        ViewerControlChannelsEndpoint = "/api/plugins/viewer-control/channels",
        ViewerControlIdentitySource = "ProgramGuideProjection",
        ViewerControlIdentityFields = "networkId|transportStreamId|serviceId",
        WaveFiltersEndpoint = "/api/plugins/program-guide/wave-filters",
        AlwaysOnTopAction = "updateWindow payload.alwaysOnTop",
        RefreshReloadScopeDirectContent = "toolwindow-content-document",
        PreferredOpenModeToolWindowSupported = true,
        ToolWindowContentOnly = true,
        ViewerProfilesEndpoint = "/api/plugins/viewer-profiles",
        ViewerProfilePayloadField = "viewerProfile",
        ViewerProfileReuseContract = "existing viewer reuse is scoped by viewerProfile/tvTestPathKey"
    };

    private static string DisplayGroupFromAllocation(string? allocationGroup, ushort? networkId)
    {
        var group = NormalizeAllocationGroup(allocationGroup);
        if (group == "GR") return "GR";
        if (group == "BSCS") return networkId == 4 ? "BS" : "CS";
        return NormalizeDisplayGroup(group);
    }

    private static string NormalizeDisplayGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "地上波" or "GROUND" => "GR",
            "BS/CS" or "BSCS" => "BSCS",
            _ => string.IsNullOrWhiteSpace(g) ? string.Empty : g
        };
    }

    private static string NormalizeAllocationGroup(string? group)
    {
        var g = (group ?? string.Empty).Trim().ToUpperInvariant();
        return g switch
        {
            "BS" or "CS" or "BS/CS" or "BSCS" => "BSCS",
            "地上波" or "GR" or "GROUND" => "GR",
            _ => string.IsNullOrWhiteSpace(g) ? string.Empty : g
        };
    }

    private static bool Contains(string? source, string keyword)
        => !string.IsNullOrEmpty(source) && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static PluginEpgEvent ToPluginEpgEvent(EpgEvent e) => new()
    {
        NetworkId = e.NetworkId,
        TransportStreamId = e.TransportStreamId,
        ServiceId = e.ServiceId,
        EventId = e.EventId,
        ServiceName = e.ServiceName,
        Title = EpgProjection.Title(e),
        Description = EpgProjection.ShortText(e),
        ExtendedDescription = EpgProjection.ExtendedText(e),
        Genre = e.Genre,
        GenreCodes = e.GenreCodes,
        Start = e.Start,
        End = e.End,
        DurationSeconds = e.DurationSeconds
    };

    private HashSet<int> GetActiveTunerRecordingReservationIds()
        => _tunerPool.GetStatus()
            .Where(s => s.UsageKind == TunerUsageKind.Recording && s.ReservationId.HasValue)
            .Select(s => s.ReservationId!.Value)
            .ToHashSet();

    private static PluginReservation ToPluginReservation(Reservation r, HashSet<int>? activeRecordingIds = null)
    {
        var status = r.Status.ToString();
        if (r.Status == ReservationStatus.Failed && activeRecordingIds is not null && activeRecordingIds.Contains(r.Id))
            status = ReservationStatus.Recording.ToString();

        return new PluginReservation
    {
        Id = r.Id,
        NetworkId = r.NetworkId,
        TransportStreamId = r.TransportStreamId,
        ServiceId = r.ServiceId,
        EventId = r.EventId,
        Title = r.Title,
        ServiceName = r.ServiceName,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        Status = status,
        Source = r.Source.ToString(),
        IsEnabled = r.IsEnabled,
        IsConflicted = r.IsConflicted,
        TunerName = r.TunerName,
        ActualTunerName = r.ActualTunerName,
        IsUserChain = r.IsUserChain,
        UserChainPreviousId = r.UserChainPreviousId,
        UserChainRootId = r.UserChainRootId,
        CreatedByPlugin = r.SourceRuleName.StartsWith("Plugin:", StringComparison.OrdinalIgnoreCase) ? r.SourceRuleName[7..] : string.Empty
    };
    }

    private static string SanitizePluginName(string value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "UnknownPlugin" : value.Trim();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        return new string(chars);
    }
}
