/* v0.2.34 STOP_PHASE_ALLOC_ROUTE_GUARD: 録画停止フェーズ中のALLOC_ROUTEはStopPhaseGateで遅延扱いにする。 */
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Tuner;

namespace TvAIr.Schedule;

public sealed record ReservationAllocationRouteRequest(
    string Source,
    string Action,
    bool RunKeywordMatcher = false,
    bool SyncProgramRuleReservations = true,
    bool ReevaluateAllocations = true,
    bool RefreshPreRecordEpgEntries = false,
    bool RefreshWakeTask = false,
    bool BypassStopPhaseGate = false,
    bool EmitConflictLogs = false,
    string ConflictLogCategory = "ReservationRoute",
    string ConflictLogTitle = "Conflict",
    string ExecutionMode = "Normal");

public sealed record ReservationAllocationRouteResult(
    int ChangedCount,
    int ConflictOnCount,
    int ConflictOffCount);

public sealed class ReservationAllocationRouteService
{
    private readonly ReservationStore _store;
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly TunerPool _tunerPool;
    private readonly LogRepository _log;
    private readonly IServiceProvider _serviceProvider;

    public ReservationAllocationRouteService(
        ReservationStore store,
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        TunerPool tunerPool,
        LogRepository log,
        IServiceProvider serviceProvider)
    {
        _store = store;
        _ini = ini;
        _tunerProfiles = tunerProfiles;
        _tunerPool = tunerPool;
        _log = log;
        _serviceProvider = serviceProvider;
    }

    public ReservationAllocationRouteResult Run(ReservationAllocationRouteRequest request)
    {
        var startedAt = DateTime.Now;

        if (!request.BypassStopPhaseGate
            && StopPhaseGate.TryDeferAllocRoute(request.Source, request.Action,
                msg => _log.Add("ALLOC_ROUTE", "StopPhase", msg)))
        {
            _log.Add("ALLOC_ROUTE", "Deferred",
                $"source={request.Source} action={request.Action} reason=recording_stop_phase_or_quiet_active elapsedMs={(int)(DateTime.Now - startedAt).TotalMilliseconds}");
            return new ReservationAllocationRouteResult(0, 0, 0);
        }
        var tunerStatusBefore = _tunerPool.GetStatusSummary();
        _log.Add("ALLOC_TRACE", "Enter",
            $"[ALLOC] source={request.Source} action={request.Action} stage=enter tunerStatus={tunerStatusBefore}");
        _log.Add("ALLOC_ROUTE", "Enter",
            $"source={request.Source} action={request.Action} executionMode={request.ExecutionMode} matcher={request.RunKeywordMatcher} syncProgram={request.SyncProgramRuleReservations} reevaluate={request.ReevaluateAllocations} preEpg={request.RefreshPreRecordEpgEntries} wake={request.RefreshWakeTask} bypassStopPhase={request.BypassStopPhaseGate} rule=common_allocation_route_contract");

        List<(int Id, string ServiceName, string Title, bool Conflicted)> changes = new();

        if (request.RunKeywordMatcher)
        {
            Resolve<KeywordMatcher>()?.RunMatching();
        }

        if (request.SyncProgramRuleReservations)
        {
            _store.SyncProgramRuleReservations();
        }

        if (request.ReevaluateAllocations)
        {
            // v0.11.87:
            // 同一イベントの親予約が複数残った場合は、割当評価へ入る前に正本を1件へ寄せる。
            // ここは共通割り当てルート内なので、番組表/局別/自動検索/起動復旧の横串で効く。
            var suppressedDuplicates = _store.SuppressDuplicateScheduledParentReservations(request.Source, request.Action);
            if (suppressedDuplicates > 0)
            {
                _log.Add("RESERVATION_DEDUPE", "SUMMARY",
                    $"result=SUPPRESSED count={suppressedDuplicates} source={request.Source} action={request.Action} commonRoute=ALLOC_ROUTE/TUNER_ALLOC rule=v0.11.87_reservation_parent_dedupe");
            }

            // v0.3.33:
            // チェーン判定は後番組優先の派生。
            // 設定上のPseudoContinuousRecordingだけでなく、DB上にUserChainが残っている場合は
            // 既存チェーンの整合性維持のため割当評価でchain=trueとして扱う。
            // これにより UI/予約DB上はチェーン扱いなのに、ALLOC側だけ chain=false で評価するズレを潰す。
            var userChainPairs = _store.GetChainPredecessors();
            var effectiveLater = _ini.LaterProgramPriority;
            var effectiveChain = effectiveLater && (_ini.PseudoContinuousRecording || userChainPairs.Count > 0);

            _log.Add("ALLOC_POLICY", "Effective",
                $"source={request.Source} action={request.Action} executionMode={request.ExecutionMode} later={effectiveLater} iniChain={_ini.PseudoContinuousRecording} userChainPairs={userChainPairs.Count} effectiveChain={effectiveChain} rule=common_allocation_route_contract");

            changes = _store.ReevaluateConflicts(
                _tunerProfiles,
                effectiveLater,
                effectiveChain,
                _ini.PostEndMarginSeconds,
                _tunerPool.GetStatus(),
                _ini.PreStartMarginSeconds)
                .ToList();
        }

        if (request.RefreshPreRecordEpgEntries)
        {
            Resolve<EpgScheduler>()?.RefreshPreRecordEpgEntries();
        }

        if (request.RefreshWakeTask)
        {
            try
            {
                var taskScheduler = Resolve<TaskSchedulerService>();
                if (taskScheduler is not null)
                {
                    if (IsLowPriorityWakeRefresh(request))
                    {
                        taskScheduler.RequestWakeTaskRefreshSoon(request.Source, request.Action, TimeSpan.FromMinutes(2));
                        _log.Add("ALLOC_ROUTE", "Wake",
                            $"source={request.Source} action={request.Action} Wakeタスク再構築を遅延集約: rule=v0.5.66_viewing_safe_wake");
                    }
                    else
                    {
                        taskScheduler.UpdateWakeTask();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Add("ALLOC_ROUTE", "Wake", $"source={request.Source} action={request.Action} Wakeタスク再構築失敗: {ex.Message}");
            }
        }

        if (request.EmitConflictLogs)
        {
            foreach (var (id, serviceName, title, conflicted) in changes)
            {
                var state = conflicted ? "ON" : "OFF";
                var displayTitle = ReservationTitleDisplayContract.ForLog(title);
                var rawTitleBlank = ReservationTitleDisplayContract.RawBlankFlag(title);
                var msg = $"競合フラグ {state}: service=[{serviceName}] title=[{displayTitle}] rawTitleBlank={rawTitleBlank} id=R{id} rule=v0.11.516_reservation_display_metadata_contract";
                _log.Add(request.ConflictLogCategory, request.ConflictLogTitle, msg);
            }
        }

        var result = new ReservationAllocationRouteResult(
            changes.Count,
            changes.Count(x => x.Conflicted),
            changes.Count(x => !x.Conflicted));

        _log.Add("ALLOC_TRACE", "Exit",
            $"[ALLOC] source={request.Source} action={request.Action} stage=exit changed={result.ChangedCount} on={result.ConflictOnCount} off={result.ConflictOffCount} elapsedMs={(int)(DateTime.Now - startedAt).TotalMilliseconds} tunerStatusBefore={tunerStatusBefore} tunerStatusAfter={_tunerPool.GetStatusSummary()}");
        _log.Add("ALLOC_ROUTE", "Exit",
            $"source={request.Source} action={request.Action} changed={result.ChangedCount} on={result.ConflictOnCount} off={result.ConflictOffCount} elapsedMs={(int)(DateTime.Now - startedAt).TotalMilliseconds}");

        return result;
    }


    private static bool IsLowPriorityWakeRefresh(ReservationAllocationRouteRequest request)
    {
        // v0.5.66:
        // 自動検索予約・UI編集・プラグイン操作などは、数秒遅れてもWake予約の実用性を損なわない。
        // ここで同期schtasks I/Oを避け、予約差分UI更新や外部LIVETest視聴と負荷が重なるのを抑える。
        if (request.Source.Contains("Keyword", StringComparison.OrdinalIgnoreCase)) return true;
        if (request.Source.Contains("Program", StringComparison.OrdinalIgnoreCase)) return true;
        if (request.Source.Contains("Plugin", StringComparison.OrdinalIgnoreCase)) return true;
        if (request.Action.Contains("Update", StringComparison.OrdinalIgnoreCase)) return true;
        if (request.Action.Contains("Add", StringComparison.OrdinalIgnoreCase)) return true;
        if (request.Action.Contains("Delete", StringComparison.OrdinalIgnoreCase)) return true;
        if (request.Action.Contains("Toggle", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private T? Resolve<T>() where T : class
        => _serviceProvider.GetService<T>();
}
