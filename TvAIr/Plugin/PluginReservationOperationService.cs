using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Schedule;

namespace TvAIr.Plugin;

internal sealed record PluginReservationMutationResult(bool Success, int? ReservationId, string Message);

/// <summary>
/// Plugin reservation mutations shared by the Runtime capability API.
/// ReservationStore mutation, ownership, allocation refresh and audit logging have one owner here.
/// </summary>
internal sealed class PluginReservationOperationService
{
    private readonly ReservationStore _reservationStore;
    private readonly ReservationAllocationRouteService _allocationRoute;
    private readonly PluginTypedEventHub _typedEvents;
    private readonly LogRepository _log;
    private readonly IniSettingsService _ini;
    private readonly ChannelFileLoader _channelLoader;

    public PluginReservationOperationService(
        ReservationStore reservationStore,
        ReservationAllocationRouteService allocationRoute,
        PluginTypedEventHub typedEvents,
        LogRepository log,
        IniSettingsService ini,
        ChannelFileLoader channelLoader)
    {
        _reservationStore = reservationStore;
        _allocationRoute = allocationRoute;
        _typedEvents = typedEvents;
        _log = log;
        _ini = ini;
        _channelLoader = channelLoader;
    }

    public PluginReservationMutationResult Add(string pluginId, PluginReservationMutationDraft draft)
    {
        using var eventScope = _typedEvents.BeginOutboxScope(out var commitEvents);
        try
        {
            var owner = PluginIdentity.Normalize(pluginId);
            var reservation = new Reservation
            {
                NetworkId = draft.NetworkId,
                TransportStreamId = draft.TransportStreamId,
                ServiceId = draft.ServiceId,
                EventId = draft.EventId,
                Title = draft.Title,
                // SERVICE_IDENTITY_CONTRACT: plugin-supplied ServiceName is never authoritative.
                // Persist the current Host display name when the exact triplet resolves; keep the
                // supplied value only as fallback for an identity no longer present in channel metadata.
                ServiceName = ServiceIdentityContract.ResolveCurrentServiceName(
                    _channelLoader.Load().Targets,
                    draft.NetworkId,
                    draft.TransportStreamId,
                    draft.ServiceId,
                    draft.ServiceName),
                StartTime = draft.StartTime.AddMinutes(-Math.Max(0, draft.PreMarginMinutes)),
                EndTime = draft.EndTime.AddMinutes(Math.Max(0, draft.PostMarginMinutes)),
                // BROADCAST_SLOT_EVENT_REBIND_INVARIANT: Plugin固有の録画前マージンをStartTimeへ適用しても、
                // 同一番組の放送枠identityはHostへ渡された番組本来の開始時刻で保持する。
                ScheduledStartTime = draft.StartTime,
                Status = ReservationStatus.Scheduled,
                Source = MapIntentToSource(draft.Intent),
                Intent = draft.Intent,
                CreatedThrough = "Plugin",
                CreatedByPluginId = owner,
                ChannelArgument = draft.ChannelArgument ?? string.Empty,
                IsEnabled = true,
                SourceRuleName = string.Empty,
                IsUserChain = false,
                UserChainPreviousId = null,
                UserChainRootId = null,
            };

            int id;
            bool added;
            Reservation committedReservation;
            if (draft.AllowChain && draft.ChainPreviousReservationId.HasValue)
            {
                var predecessor = _reservationStore.GetById(draft.ChainPreviousReservationId.Value);
                if (predecessor is null)
                    return new PluginReservationMutationResult(false, null, "チェーン前番組が見つかりません。");
                var canonicalRoot = predecessor.UserChainRootId ?? predecessor.Id;
                var chainResult = _reservationStore.AddOrPromoteUserChain(
                    reservation,
                    draft.ChainPreviousReservationId.Value,
                    canonicalRoot,
                    _ini.LaterProgramPriority && _ini.PseudoContinuousRecording);
                if (!chainResult.Applied || chainResult.Reservation is not { } committedChainReservation)
                    return new PluginReservationMutationResult(false, chainResult.ReservationId == 0 ? null : chainResult.ReservationId, chainResult.Reason);
                id = chainResult.ReservationId;
                added = chainResult.Added;
                committedReservation = committedChainReservation;
            }
            else
            {
                var addResult = _reservationStore.AddOrGetActiveParent(reservation);
                id = addResult.ReservationId;
                added = addResult.Added;
                committedReservation = addResult.Reservation;
                if (addResult.RefreshedBroadcastSlot)
                {
                    ReevaluateAllocations(owner, "RefreshBroadcastSlot");
                    commitEvents();
                    AddAuditLog(owner, "AddReservation",
                        $"result=REFRESHED_EXISTING title={draft.Title} id=R{id} status={committedReservation.Status} dataVersion={committedReservation.DataVersion} action=rebind_same_reservation_id_then_common_allocation rule=release_contract");
                    return new PluginReservationMutationResult(true, id, "既存予約を現行番組へ更新しました。");
                }
            }

            if (!added)
            {
                AddAuditLog(owner, "AddReservation",
                    $"result=REUSE_EXISTING title={draft.Title} id=R{id} status={committedReservation.Status} intent={committedReservation.Intent} createdThrough={committedReservation.CreatedThrough} createdByPluginId={committedReservation.CreatedByPluginId} dataVersion={committedReservation.DataVersion} chain={committedReservation.IsUserChain} chainRoot={(committedReservation.UserChainRootId.HasValue ? $"R{committedReservation.UserChainRootId.Value}" : "-")} rule=release_contract");
                return new PluginReservationMutationResult(true, id, "既存の予約を使用しました。");
            }

            ReevaluateAllocations(owner, "AddReservation");
            commitEvents();
            AddAuditLog(owner, "AddReservation", $"result=ADDED title={draft.Title} id=R{id} source={reservation.Source} intent={reservation.Intent} createdThrough={reservation.CreatedThrough} createdByPluginId={reservation.CreatedByPluginId} chain={committedReservation.IsUserChain} chainRoot={(committedReservation.UserChainRootId.HasValue ? $"R{committedReservation.UserChainRootId.Value}" : "-")} events=OUTBOX_COMMITTED rule=release_contract");
            return new PluginReservationMutationResult(true, id, "予約を追加しました。");
        }
        catch (Exception ex)
        {
            return new PluginReservationMutationResult(false, null, ex.Message);
        }
    }

    public PluginReservationMutationResult Update(string pluginId, int reservationId, bool? isEnabled)
    {
        using var eventScope = _typedEvents.BeginOutboxScope(out var commitEvents);
        try
        {
            var owner = PluginIdentity.Normalize(pluginId);
            var existing = _reservationStore.GetById(reservationId);
            if (existing is null)
                return new PluginReservationMutationResult(false, reservationId, "予約が見つかりません。");
            if (!IsOwnedByPlugin(existing, owner))
                return new PluginReservationMutationResult(false, reservationId, "このプラグインが作成した予約ではありません。");
            if (!isEnabled.HasValue)
            {
                AddAuditLog(owner, "UpdateReservation", $"id=R{reservationId} result=NO_CHANGE reason=no_mutation_requested allocationSkipped=True rule=release_contract");
                return new PluginReservationMutationResult(true, reservationId, "変更はありません。");
            }

            var enabledUpdate = _reservationStore.UpdateEnabledIfChanged(reservationId, isEnabled.Value);
            if (!enabledUpdate.Found)
                return new PluginReservationMutationResult(false, reservationId, "予約が見つかりません。");
            if (!enabledUpdate.Changed)
            {
                var success = enabledUpdate.Reason != "compare_and_set_failed";
                AddAuditLog(owner, "UpdateReservation", $"id=R{reservationId} result={(success ? "NO_CHANGE" : "CONFLICT")} enabled={enabledUpdate.CurrentEnabled} dataVersion={enabledUpdate.CurrentDataVersion} reason={enabledUpdate.Reason} allocationSkipped=True rule=release_contract");
                return new PluginReservationMutationResult(success, reservationId,
                    success ? "変更はありません。" : "予約状態が同時に変更されました。");
            }

            ReevaluateAllocations(owner, "UpdateReservation");
            commitEvents();
            AddAuditLog(owner, "UpdateReservation", $"id=R{reservationId} result=CHANGED enabled={isEnabled.Value} dataVersion={enabledUpdate.CurrentDataVersion} events=OUTBOX_COMMITTED rule=release_contract");
            return new PluginReservationMutationResult(true, reservationId, "予約を更新しました。");
        }
        catch (Exception ex)
        {
            return new PluginReservationMutationResult(false, reservationId, ex.Message);
        }
    }

    public PluginReservationMutationResult Delete(string pluginId, int reservationId, bool force)
    {
        using var eventScope = _typedEvents.BeginOutboxScope(out var commitEvents);
        try
        {
            var owner = PluginIdentity.Normalize(pluginId);
            var existing = _reservationStore.GetById(reservationId);
            if (existing is null)
                return new PluginReservationMutationResult(false, reservationId, "予約が見つかりません。");
            if (!force && !IsOwnedByPlugin(existing, owner))
                return new PluginReservationMutationResult(false, reservationId, "このプラグインが作成した予約ではありません。");

            // PLUGIN_PHYSICAL_DELETE_TERMINAL_CAS_INVARIANT:
            // force は所有者確認だけを迂回できる。Scheduled / Starting / Recording / Stopping を物理削除する権限にはしない。
            // 物理削除はUIと同じ終端予約Status＋DataVersion＋チェーントポロジーCASを必ず通す。
            if (existing.Status is not (ReservationStatus.Completed or ReservationStatus.Failed or ReservationStatus.Cancelled))
                return new PluginReservationMutationResult(false, reservationId, "未開始または実行中の予約は物理削除できません。先に正規の取消・停止操作を行ってください。");
            if (!_reservationStore.TryDeleteTerminalReservationAtomicCas(existing, out _))
                return new PluginReservationMutationResult(false, reservationId, "予約状態またはチェーン構造が同時に更新されました。");

            ReevaluateAllocations(owner, "DeleteReservation");
            commitEvents();
            AddAuditLog(owner, "DeleteReservation", $"id=R{reservationId} force={force} terminalCas=True events=OUTBOX_COMMITTED rule=release_contract");
            return new PluginReservationMutationResult(true, reservationId, "予約を削除しました。");
        }
        catch (Exception ex)
        {
            return new PluginReservationMutationResult(false, reservationId, ex.Message);
        }
    }

    private void ReevaluateAllocations(string pluginId, string action)
    {
        // PLUGIN_RESERVATION_ALLOCATION_INPUT_INVARIANT:
        // Pluginの予約Mutationも本体UIと同じALLOC_ROUTE/TUNER_ALLOCを正本とする。
        // Add/Update/DeleteはProgramRule定義そのものを変更しないため、無関係なProgramRule全件同期を
        // クリティカルパスへ混載しない。一方、InteractiveProgramEvent/Keyword系はPreRec親候補になり得るため、
        // PreRec再評価を省略して本体UI予約と責務差を作ってはならない。System/ProgramはPreRec側のSource判定で除外される。
        _allocationRoute.Run(new ReservationAllocationRouteRequest(
            Source: "Plugin",
            Action: action,
            RunKeywordMatcher: false,
            SyncProgramRuleReservations: false,
            ReevaluateAllocations: true,
            RefreshPreRecordEpgEntries: true,
            RefreshWakeTask: true,
            WakeRefreshMode: ReservationAllocationWakeRefreshMode.BoundedCoalesce));
    }

    private void AddAuditLog(string pluginId, string action, string message)
        => _log.Add("PLUGIN_AUDIT", action, $"plugin={pluginId} {message}");

    private static bool IsOwnedByPlugin(Reservation reservation, string pluginId)
        => string.Equals(reservation.CreatedThrough, "Plugin", StringComparison.OrdinalIgnoreCase)
           && string.Equals(reservation.CreatedByPluginId, pluginId, StringComparison.OrdinalIgnoreCase);

    private static ReservationSource MapIntentToSource(ReservationIntent intent)
        => intent switch
        {
            ReservationIntent.InteractiveProgramEvent => ReservationSource.Manual,
            ReservationIntent.ProgramTimeSlot => ReservationSource.Program,
            ReservationIntent.AutomaticSearch => ReservationSource.Keyword,
            ReservationIntent.KeywordRule => ReservationSource.Keyword,
            ReservationIntent.System or ReservationIntent.SystemDailyEpg or ReservationIntent.SystemPreRecordEpg => ReservationSource.Epg,
            _ => ReservationSource.Program
        };

}

internal sealed record PluginReservationMutationDraft(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    ushort EventId,
    string Title,
    string ServiceName,
    DateTime StartTime,
    DateTime EndTime,
    int PreMarginMinutes,
    int PostMarginMinutes,
    string? ChannelArgument,
    bool AllowChain,
    int? ChainPreviousReservationId,
    ReservationIntent Intent);
