/* release_contract wake-credential-probe-diagnostics: Wake本線を変えず、CredentialProbeの必要判定・実行理由・probeタスク名を診断ログへ露出する。 */
/* release_contract wake-responsibility-log-cleanup: Wake本線の挙動を維持し、plan読取副作用・CredentialProbe名前空間・ログ分類を整理する。 */
/* release_contract wake-log-alignment-cleanup: release_contractのWAKE本線は維持し、個別登録rule名・差分適用next表示・activeExtra監査名だけを整理する。 */
/* release_contract wake-coverage-required-deferred-fix: Task Schedulerは起床だけ。Coverageは直近必須範囲と将来Deferredを分離し、未来予約をmissing扱いしない。 */
/* release_contract wake-tvrock-style-minimal-wakeup: Task Schedulerは起床時刻だけ、TvAIr本体がWakeCoverage/予約DBから判断する。通常Wake本数目標は録画チューナー数、13本は非常用上限。 */
﻿/* release_contract wake-coverage-complete: WAKE時刻スロット化をWakeCoverageItemへ完全分離し、TaskScheduler登録単位と予約メタ属性を混在させない。 */
/* release_contract wake-time-slot-coverage-simplification: Task Scheduler側の主単位を起床時刻へ寄せ、予約/目的メタ属性はWakeCoverageへ分離する。 */
/* release_contract wake-stale-orphan-quarantine-fix: 削除不能な旧世代WakeSlotを現行Wake判定から隔離し、current_processで消せないタスクへ資格情報削除を連発しない。 */
/* release_contract wake-diff-apply-generation-log-fix: WakeSlot世代を安定化し、計画変更時の全削除/全登録とOVERFLOW誤用を抑制、再構築中要求を最新pendingへ集約する。 */
/* release_contract wake-slot-dynamic-cap-contract: WakeSlot上限を「録画チューナー総数×2＋制御用1」に定義化し、固定13から動的算出へ変更する。 */
/* release_contract wake-slot-overflow-cleanup: 遅延Wake再構築中でもTask Scheduler上のWakeSlot過剰残骸を即時検出・掃除し、一覧増殖を抑止する。 */
/* release_contract task-management-local-maintenance-fix: WakeSlot削除/掃除はローカルタスク管理として現在プロセストークンを先行し、資格情報失敗ノイズと掃除遅延を抑制する。 */
/* release_contract wake-credential-context-record-verdict-crosscheck: Wake操作の資格情報コンテキストを横串化し、録画結果判定の後段TS検証を統合する。 */
/* release_contract wake-log-polish-quality-wording-noise-reduce: 安定経路を変えず、Wake/品質/周期ログの誤解とノイズを抑制する。 */
/* release_contract wake-stale-task-safe-cleanup: WakePlan世代登録成功後に旧世代/旧固定/同世代余剰WakeSlotだけを安全削除する。 */
/* release_contract wake-plan-generation-slot-guard: WakeSlot世代をアプリ版固定からWakePlan単位へ移し、同世代余剰Wakeの実行をslotId照合で防ぐ。 */
/* release_contract wake-generation-credential-probe: WakeSlotを世代化し、旧世代/削除不能タスクを現行Wake同期から分離する。 */
/* release_contract wake-slot-rebuild: Primary/Backup固定名の上書き再登録をやめ、予約時刻入りのWakeSlotタスクを最大13本生成して既存固定名権限不整合を回避する。 */
/* release_contract near-wake-immediate-rebuild: 近接予約のWake再構築はデバウンスせず即時実行し、スリープ前にWakeタスク実体を作る。 */
/* release_contract wake-register-fallback-contract: Password方式Wake登録がアクセス拒否等で失敗した場合、InteractiveToken方式へフォールバックし、失敗時はWAKE_REGISTER_CRITICALで明示する。 */
/* release_contract wake-fixed-slot-recovery-scheduler: Wakeタスク起動時は --wake-task を単一インスタンス合流シグナルとして扱い、既存TvAIrがいる場合は本体二重起動せず signal ファイル経由で常駐プロセスへ合流する。 */
/* release_contract wake-plan-hash-trigger-limit: Wake計画ハッシュが不変なら短時間内のschtasks照合も省略し、再構築発火をさらに抑制する。 */
/* release_contract wake-contract-validated-kept-tasks: 既存Wakeタスクを名前一致だけでkeptにせず、Action/WakeToRun/StartWhenAvailable/ExecutionTimeLimit/Triggerを検証して不一致なら再登録する。 */
/* release_contract wake-task-nochange-skip: Wakeタスク計画が前回適用済みで実体も一致する場合は削除→再登録を省略し、不要なschtasks I/Oを抑制する。 */
/* release_contract wake-task-clean-rebuild: TvAIr_Wake_* を再構築前に完全削除し、現在必要なWakeタスクだけ再登録する。 */
/* release_contract STOP_PHASE_WAKE_GUARD: 録画停止フェーズ中のWake再構築は即時実行せず遅延・バッチ化する。 */
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Principal;
using TvAIr.Core;

namespace TvAIr.Schedule;

/// <summary>
/// Windowsタスクスケジューラーへの登録・削除を schtasks.exe 経由で行うサービス。
///
/// ///   固定2タスク + 複数Trigger 方式を廃止し、
///   release_contract:
///   固定名 Primary/Backup/SystemEpg の削除・上書き方式を廃止し、
///   Wake時刻・種別・予約IDを含む TvAIr_WakeSlot_* を「録画チューナー総数×2＋制御用1」の上限で生成する。
///
///   目的:
///     - 旧固定名タスクの作成者/権限不整合に録画Wakeを巻き込ませないこと
///     - 近接予約追加時に当日分Wakeを即時作成すること
///     - Register後に Query /XML で WakeToRun / LogonType / RunLevel を検証できること
///
///   重要仕様:
///     - TaskUserName / TaskPasswordEncrypted が未設定なら Wake タスクは作らない。
///     - Password + LeastPrivilege + WakeToRun=true を本線とする。
///     - 旧 TvAIr_Wake_Primary/Backup/SystemEpg は削除・上書きしない。
///     - 管理対象タスクは TvAIr_WakeSlot_* のみ。
/// </summary>
public sealed class TaskSchedulerService
{
    private const string WakeTaskPrefix    = "TvAIr_Wake_";
    private const string WakeEpgTaskPrefix = "TvAIr_Wake_Epg_";
    private const string WakeRecTaskPrefix = "TvAIr_Wake_Rec_";

    // release_contract:
    // 固定名 Primary/Backup を毎回削除・上書きする方式は、Windows側で既存タスクの
    // 作成者/権限が食い違った瞬間にアクセス拒否で詰む。録画失敗を避けるため、
    // 新しい名前空間の時刻入りWakeSlotを必要分だけ生成し、既存固定名タスクには触らない。
    private const string WakeSlotTaskPrefix = "TvAIr_WakeSlot_";
    private const string WakeProbeTaskPrefix = "TvAIr_WakeProbe_";
    private const string WakeSlotGenerationFallback = "g01013";
    private const int WakeSlotControlTaskCount = 1;
    // release_contract:
    // WakeSlot名に含める世代を計画内容ハッシュから安定世代へ変更する。
    // 計画変更ごとに全WakeSlot名が変わると、同じ時刻/種別/予約のタスクまで毎回13本総入替になり、
    // 連続予約追加時のTask Scheduler I/Oが重くなる。stale判定は active-slots で担保する。
    private const string WakeSlotStableGeneration = "gWAKEDIFF";

    // 旧設計の固定名タスクと残骸掃除用プレフィックス
    private const string LegacyWakeTaskName    = "TvAIr_Wake";
    private const string LegacyWakeEpgTaskName = "TvAIr_Wake_Epg";
    private const string LegacyWakeRecTaskName = "TvAIr_Wake_Rec";

    private readonly ReservationStore   _store;
    private readonly IniSettingsService _ini;
    private readonly LogRepository      _log;
    private readonly UserEventLogService _userEvents;

    private static readonly TimeSpan FullValidationInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WakeOverdueGrace = TimeSpan.FromMinutes(2);

    private string?  _lastPlanSignature;
    private DateTime _lastFullValidationUtc = DateTime.MinValue;
    private bool     _legacyCleanupAttempted;
    private bool     _missingCredentialLogged;
    private DateTime _lastRuntimeAuditUtc = DateTime.MinValue;
    private string _lastEpgWakePlanLogKey = string.Empty;
    private DateTime _lastEpgWakePlanLogUtc = DateTime.MinValue;
    private List<string> _lastWakeCoverageLines = new();

    // release_contract:
    // 自動検索予約更新・UI操作直後に schtasks の削除/登録を同期実行すると、
    // ブラウザ描画や外部LIVETest視聴と負荷が重なりやすい。
    // Wake計画変更は数秒デバウンスし、同一更新内の多重要求を1回に集約する。
    private readonly object _wakeUpdateGate = new();
    private readonly object _deferredWakeGate = new();
    private static readonly TimeSpan DeferredWakeDefaultDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeferredWakeQuietPeriod = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DeferredWakeMaxHold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeferredWakeUrgentWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DeferredWakeImmediateWindow = TimeSpan.FromMinutes(30);

    private DateTime _deferredWakeDueUtc = DateTime.MinValue;
    private DateTime _deferredWakeFirstRequestUtc = DateTime.MinValue;
    private DateTime _deferredWakeLastRequestUtc = DateTime.MinValue;
    private string _deferredWakeReason = string.Empty;
    private int _deferredWakeRequestCount;

    // release_contract:
    // Wake再構築中に別操作が入った場合、単に already-running で捨てず、
    // 最新要求だけを保持し、実行完了後に1回だけ再実行する。
    private readonly object _wakePendingGate = new();
    private bool _wakeRebuildPending;
    private string _wakeRebuildPendingReason = string.Empty;
    private int _wakeRebuildPendingCount;

    public TaskSchedulerService(
        ReservationStore   store,
        IniSettingsService ini,
        LogRepository      log,
        UserEventLogService userEvents)
    {
        _store = store;
        _ini   = ini;
        _log   = log;
        _userEvents = userEvents;
    }

    /// <summary>
    /// 各チューナーごとの直近ユーザー予約(source != Epg, is_conflicted=0, is_enabled=1)を
    /// 基に、予約単位のWakeタスク群を再構築する。
    /// </summary>
    public void UpdateWakeTask()
    {
        UpdateWakeTaskCore("direct");
    }

    private void UpdateWakeTaskCore(string triggerReason)
    {
        if (!System.Threading.Monitor.TryEnter(_wakeUpdateGate))
        {
            var pendingCount = MarkWakeRebuildPending(triggerReason);
            _log.Add("TaskScheduler", "WAKE_REBUILD_PENDING_COALESCED",
                $"reason=already-running latest={CompactOneLine(triggerReason)} pendingCount={pendingCount} action=run_latest_after_current rule=release_contract");
            return;
        }

        try
        {
        if (StopPhaseGate.TryDeferWakeRebuild("TaskSchedulerService.UpdateWakeTask", msg => _log.Add("TaskScheduler", "StopPhase", msg)))
        {
            _log.Add("TaskScheduler", "Wake", "Wakeタスク再構築延期: reason=stop-phase-active");
            return;
        }

        if (!_legacyCleanupAttempted)
        {
            TryDeleteLegacyFixedTasks();
            _legacyCleanupAttempted = true;
        }

        if (!HasWakeCredentials())
        {
            DeleteAllManagedTasks();
            if (!_missingCredentialLogged)
            {
                _log.Add("TaskScheduler", "Wake",
                    "TaskUserName または TaskPasswordEncrypted が未設定のため Wake タスクは作成しません。Password ログオン方式の資格情報設定が必要です。");
                _missingCredentialLogged = true;
            }
            _lastPlanSignature = "";
            return;
        }

        _missingCredentialLogged = false;

        var desired = BuildDesiredTasks(DateTime.Now, emitAudit: true);
        var desiredGeneration = ResolveDesiredWakeGeneration(desired);
        var planSignature = BuildPlanSignature(desired);
        var previousHash = ShortHash(_lastPlanSignature);
        var currentHash = ShortHash(planSignature);
        var planChanged = _lastPlanSignature is null || !string.Equals(_lastPlanSignature, planSignature, StringComparison.Ordinal);
        var validationExpired = DateTime.UtcNow - _lastFullValidationUtc >= FullValidationInterval;
        var nextDesired = desired.OrderBy(t => t.When).FirstOrDefault();
        var nextDesiredText = nextDesired is null ? "none" : $"{nextDesired.When:MM/dd HH:mm:ss} {nextDesired.Name}";
        var systemEpgDesired = desired.Where(t => string.Equals(t.Kind, "SYSTEM_EPG", StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.When).FirstOrDefault();
        var epgWakePlanKey = systemEpgDesired is null
            ? $"none|future={HasFutureScheduledDailyEpg(DateTime.Now)}"
            : $"included|{systemEpgDesired.When:O}|{systemEpgDesired.Name}";
        var shouldLogEpgWakePlan = planChanged
            || validationExpired
            || !string.Equals(_lastEpgWakePlanLogKey, epgWakePlanKey, StringComparison.Ordinal)
            || DateTime.UtcNow - _lastEpgWakePlanLogUtc >= TimeSpan.FromMinutes(30);
        if (shouldLogEpgWakePlan)
        {
            _lastEpgWakePlanLogKey = epgWakePlanKey;
            _lastEpgWakePlanLogUtc = DateTime.UtcNow;
            if (systemEpgDesired is not null)
            {
                var systemCount = desired.Count(t => string.Equals(t.Kind, "SYSTEM_EPG", StringComparison.OrdinalIgnoreCase));
                _log.Add("TaskScheduler", "EPG_WAKE_PLAN",
                    $"result=INCLUDED systemEpgWakeCount={systemCount} next={systemEpgDesired.When:MM/dd HH:mm:ss} task={systemEpgDesired.Name} reservation=R{systemEpgDesired.Reservation.Id} title={CompactOneLine(systemEpgDesired.Reservation.Title)} warning=False reason=system_epg_wake_in_desired_plan rule=wake_log_polish_quality_wording_noise_reduce");
            }
            else if (HasFutureScheduledDailyEpg(DateTime.Now))
            {
                _log.Add("TaskScheduler", "SYSTEM_EPG_WAKE_NOT_REQUIRED_YET",
                    "result=INFO reason=future_daily_epg_exists_but_outside_current_wake_budget_or_slot_limit warning=False action=none rule=wake_log_polish_quality_wording_noise_reduce");
            }
        }
        var recordingTunerCountForWakePlan = GetRecordingWakeTunerCount();
        var targetWakeSlotLimit = GetWakeSlotLimit();
        var emergencyWakeSlotLimit = GetWakeSlotEmergencyLimit();
        _log.Add("TaskScheduler", "WAKE_PLAN_HASH",
            $"class=summary before={previousHash} after={currentHash} changed={planChanged} validationExpired={validationExpired} desired={desired.Count} recordingTunerCount={recordingTunerCountForWakePlan} targetTaskCount={targetWakeSlotLimit} emergencyLimit={emergencyWakeSlotLimit} formula=recording_tuners_target_emergency_limit next={nextDesiredText}");

        // release_contract:
        // 予約追加/キャンセルを短時間に繰り返した場合、Wake再構築自体を遅延集約していても
        // Windowsタスクスケジューラの一覧には旧WakeSlotが先に積み上がって見える。
        // plan-hash fast skip や遅延待ちに入る前に、現行desired以外のWakeSlotが残っているかだけを軽量確認し、
        // 「録画チューナー総数×2＋制御用1」の上限超過または差分外検出時は現行desiredを保護したまま掃除する。
        CleanupWakeSlotOverflowIfNeeded(desiredGeneration, desired.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            reason: planChanged ? "plan_changed_preflight" : validationExpired ? "validation_preflight" : "fast_skip_preflight");

        // release_contract:
        // 録画終了後Tick・通常Tickなど、予約構造が変わっていない発火では
        // schtasks /Query すら行わない。Windows側の実体照合は5分に1回だけ通す。
        // 起動直後・予約構造変更時・検証期限切れ時は従来どおり実体照合し、
        // ゴミ混入や外部削除を検出した場合は完全削除→再登録へ進む。
        if (!planChanged && !validationExpired)
        {
            _log.Add("TaskScheduler", "Wake",
                $"Wakeタスク再構築省略: reason=plan-hash-nochange fast=true desired={desired.Count} next={nextDesiredText} rule=wake_log_polish_quality_wording_noise_reduce");
            return;
        }

        var existing = GetManagedTaskNames(desiredGeneration);
        AuditStaleWakeSlotNamesOutsideCurrentGeneration(desiredGeneration);
        var desiredNames = desired.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredByName = desired.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var missingDesired = desiredNames.Where(name => !existing.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var unexpectedExisting = existing.Where(name => !desiredNames.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

        // release_contract:
        // 「登録できている」だけではWake保証にならない。Windows側で予定時刻を過ぎても
        // LastRunTime が未実行/0x41303 のまま残っている管理対象タスクを、削除前に監査する。
        AuditManagedWakeRuntimeState(existing, desiredNames, DateTime.Now, validationExpired || planChanged);

        // release_contract:
        // 名前と予定時刻が同じでも、Windowsタスクの中身が旧契約のままならWake保証にならない。
        // 既存タスクをkept扱いにする前に、Action / Arguments / StartBoundary / WakeToRun /
        // StartWhenAvailable / ExecutionTimeLimit / LogonType / RunLevel を検証し、
        // 1項目でも違えば再登録対象にする。
        var contractMismatchDesired = new List<string>();
        foreach (var spec in desired.OrderBy(t => t.When).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.Contains(spec.Name) || missingDesired.Contains(spec.Name))
                continue;

            var contract = VerifyExistingTaskContract(spec);
            if (!contract.Matches)
            {
                contractMismatchDesired.Add(spec.Name);
                _log.Add("TaskScheduler", "WAKE_CONTRACT_MISMATCH",
                    $"task={spec.Name} kind={spec.Kind} at={spec.When:MM/dd HH:mm:ss} reason={contract.Reason} action=reregister rule=wake_log_polish_quality_wording_noise_reduce");
            }
        }

        // release_contract + release_contract:
        // 同じ予約状態でも、名前一致だけでは省略しない。契約検証まで一致した場合だけkeptにする。
        if (!planChanged && missingDesired.Count == 0 && unexpectedExisting.Count == 0 && contractMismatchDesired.Count == 0)
        {
            // release_contract:
            // 現行Wakeは正しく保持されていても、旧世代/旧固定/同世代余剰WakeSlotが
            // タスクスケジューラ上に積み上がると、一覧上も運用上も不安定に見える。
            // 契約検証OKのタイミングでだけ、現行desiredを保護しながら、録画チューナー数から算出した正式上限で安全掃除する。
            CleanupStaleWakeSlotTasksAfterSuccessfulApply(desiredGeneration, desiredNames, reason: "validated_nochange");
            _lastFullValidationUtc = DateTime.UtcNow;
            _log.Add("TaskScheduler", "Wake",
                $"Wakeタスク再構築省略: reason=nochange contractValidated=true managed={existing.Count} desired={desired.Count} next={nextDesiredText} rule=wake_log_polish_quality_wording_noise_reduce");
            return;
        }

        if (!planChanged || contractMismatchDesired.Count > 0)
        {
            _log.Add("TaskScheduler", "Wake",
                $"Wakeタスク実体差異検出: class=audit missing={missingDesired.Count} unexpected={unexpectedExisting.Count} contractMismatch={contractMismatchDesired.Count} managed={existing.Count} desired={desired.Count} rule=wake_log_polish_quality_wording_noise_reduce");
        }

        // release_contract:
        // release_contract:
        // 現行Wake保護を優先するため、Wake同期本線では削除を先行しない。
        // current generation の差分外タスクも登録成功後の後処理対象として扱い、旧世代/旧名の削除不能は
        // 現行Wake登録の成否と分離する。削除リトライで録画直前Wakeを巻き込まない。
        var deletedCount = 0;
        var deleteFailedCount = 0;
        if (unexpectedExisting.Count > 0)
        {
            _log.Add("TaskScheduler", "WAKE_CURRENT_GENERATION_EXTRA_AUDIT",
                $"result=INFO unexpectedCurrentGeneration={unexpectedExisting.Count} action=post_register_cleanup_deferred recording_blocking=False rule=release_contract");
        }

        if (desired.Count == 0)
        {
            _log.Add("TaskScheduler", "Wake", $"対象予約なし。Wakeタスク差分適用完了: class=summary deleted={deletedCount} deleteFailed={deleteFailedCount} registered=0 kept=0 rule=wake_log_polish_quality_wording_noise_reduce");
            _lastPlanSignature = deleteFailedCount == 0 ? "" : null;
            if (deleteFailedCount == 0)
                _lastFullValidationUtc = DateTime.UtcNow;
            return;
        }

        var registerNames = missingDesired
            .Concat(contractMismatchDesired)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var specsToRegister = registerNames
            .Select(name => desiredByName.TryGetValue(name, out var spec) ? spec : null)
            .Where(spec => spec is not null)
            .Cast<WakeTaskSpec>()
            .OrderBy(spec => spec.When)
            .ThenBy(spec => spec.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<WakeTaskApplyResult>(specsToRegister.Count);
        var credentialProbeReason = BuildWakeCredentialProbeReason(
            specsToRegister.Count,
            missingDesired.Count,
            contractMismatchDesired.Count,
            unexpectedExisting.Count,
            planChanged,
            validationExpired);
        var credentialProbeOk = true;
        if (specsToRegister.Count > 0)
        {
            credentialProbeOk = ProbeWakeCredentialContract(credentialProbeReason, specsToRegister.Count, missingDesired.Count, contractMismatchDesired.Count, unexpectedExisting.Count, planChanged, validationExpired);
        }
        if (!credentialProbeOk)
        {
            foreach (var spec in specsToRegister)
                results.Add(new WakeTaskApplyResult(spec, false));
        }
        else
        {
            foreach (var spec in specsToRegister)
                results.Add(RegisterOrUpdateTask(spec));
        }

        var successCount = results.Count(r => r.Success);
        var failedCount = results.Count - successCount;
        var nextSuccess = results
            .Where(r => r.Success)
            .OrderBy(r => r.Spec.When)
            .Select(r => r.Spec)
            .FirstOrDefault();

        var keptCount = desired.Count - specsToRegister.Count;
        // release_contract:
        // 差分登録が「後方の1本」だけ発生した場合でも、サマリの next は registeredNext ではなく
        // 現在有効な desired 全体の最短Wakeを表示する。ユーザー/監査が「次に起きるべき時刻」を
        // 誤読しないよう、登録差分と実効最短Wakeを分離する。
        var effectiveNext = desired.OrderBy(r => r.When).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var registeredNext = nextSuccess;
        var registeredNextText = registeredNext is null ? "none" : $"{registeredNext.When:MM/dd HH:mm:ss} {registeredNext.Name}";
        if (effectiveNext is null)
        {
            _log.Add("TaskScheduler", "Wake",
                $"Wakeタスク差分適用完了: class=summary registered={successCount} failed={failedCount} deleted={deletedCount} deleteFailed={deleteFailedCount} kept={keptCount} next=none registeredNext={registeredNextText} nextSource=effective_desired_shortest rule=wake_log_polish_quality_wording_noise_reduce");
        }
        else
        {
            _log.Add("TaskScheduler", "Wake",
                $"Wakeタスク差分適用完了: class=summary registered={successCount} failed={failedCount} deleted={deletedCount} deleteFailed={deleteFailedCount} kept={keptCount} next={effectiveNext.When:MM/dd HH:mm:ss} {effectiveNext.Name} registeredNext={registeredNextText} nextSource=effective_desired_shortest rule=wake_log_polish_quality_wording_noise_reduce");
        }

        if (failedCount > 0)
        {
            var firstFailed = results
                .Where(r => !r.Success)
                .OrderBy(r => r.Spec.When)
                .Select(r => r.Spec)
                .FirstOrDefault();
            var failedTarget = firstFailed is null ? "none" : $"{firstFailed.When:MM/dd HH:mm:ss} {firstFailed.Name} R{firstFailed.Reservation.Id} {CompactOneLine(firstFailed.Reservation.Title)}";

            // release_contract:
            // Wake registration failures are Task Scheduler diagnostics, not routine user-operation events.
            // Do not surface raw AccessDenied / kept / failed counters in the user log on every rebuild.
            // A user-facing wake event must be emitted only from a future WakeCoverage guarantee layer
            // when a concrete recording has no alternative wake coverage and user action is actually required.
            _log.Add("TaskScheduler", "WAKE_REGISTER_CRITICAL",
                $"result=FAILED failed={failedCount} deleteFailed={deleteFailedCount} firstFailed={failedTarget} userLogSuppressed=True suppressionReason=task_scheduler_diagnostic_not_user_actionable risk=sleep_resume_not_guaranteed action=internal_diagnose_wake_coverage rule=release_contract");
        }
        else if (deleteFailedCount > 0)
        {
            // release_contract:
            // 実証ログで、予定Wakeは kept/register 済みなのに、過去または差分外の WakeSlot 削除だけが
            // アクセス拒否になるケースを確認した。これは「次の復帰タスクが無い」危険とは別種なので、
            // 登録失敗と同じ WAKE_REGISTER_CRITICAL にはしない。desired 側の契約が満たせていれば
            // 計画署名は採用し、不要タスク削除失敗は別警告として次回検証周期で再試行する。
            _log.Add("TaskScheduler", "WAKE_DELETE_STALE_DENIED",
                $"result=WARNING deleteFailed={deleteFailedCount} failed=0 desired={desired.Count} kept={keptCount} registered={successCount} risk=stale_wake_task_may_remain recording_blocking=False sleep_resume_desired_tasks_preserved=True action=retry_on_next_validation_or_manual_cleanup_or_manual_cleanup rule=release_contract");
        }

        _lastPlanSignature = failedCount == 0 ? planSignature : null;
        if (failedCount == 0)
        {
            WriteActiveWakePlan(desiredGeneration, desired);
            CleanupStaleWakeSlotTasksAfterSuccessfulApply(desiredGeneration, desiredNames, reason: "post_register_success");
            _lastFullValidationUtc = DateTime.UtcNow;
        }
        }
        finally
        {
            System.Threading.Monitor.Exit(_wakeUpdateGate);
            if (TryConsumeWakeRebuildPending(out var pendingReason, out var pendingCount))
            {
                _log.Add("TaskScheduler", "WAKE_REBUILD_PENDING_FLUSHED",
                    $"latest={CompactOneLine(pendingReason)} mergedRequests={pendingCount} action=run_once rule=release_contract");
                UpdateWakeTaskCore($"pending_flush:{pendingReason}");
            }
        }
    }

    private int MarkWakeRebuildPending(string reason)
    {
        lock (_wakePendingGate)
        {
            _wakeRebuildPending = true;
            _wakeRebuildPendingReason = reason;
            _wakeRebuildPendingCount++;
            return _wakeRebuildPendingCount;
        }
    }

    private bool TryConsumeWakeRebuildPending(out string reason, out int count)
    {
        lock (_wakePendingGate)
        {
            if (!_wakeRebuildPending)
            {
                reason = string.Empty;
                count = 0;
                return false;
            }

            reason = _wakeRebuildPendingReason;
            count = _wakeRebuildPendingCount;
            _wakeRebuildPending = false;
            _wakeRebuildPendingReason = string.Empty;
            _wakeRebuildPendingCount = 0;
            return true;
        }
    }

    public void RequestWakeTaskRefreshSoon(string source, string action, TimeSpan? delay = null)
    {
        var effectiveDelay = delay ?? DeferredWakeDefaultDelay;
        var reason = $"source={source} action={action}";

        // release_contract:
        // Wake再構築の遅延集約は大量更新時のI/O抑制には有効だが、
        // 近接予約では「スリープに入る前にWakeタスクが存在しない」事故になる。
        // 直近Wakeが30分以内なら、低優先扱いのAdd/Updateであっても同期的に再構築する。
        if (TryRunImmediateWakeRefreshForNearWake(reason, effectiveDelay))
            return;

        var now = DateTime.UtcNow;
        var due = now.Add(effectiveDelay);
        int count;
        DateTime currentDue;
        lock (_deferredWakeGate)
        {
            // release_contract:
            // 連続ON/OFF操作中に、前のdueがちょうどTickで拾われて schtasks 削除/登録が走ると、
            // UIちらつき・LIVETestカクつき・操作中の見た目ズレを誘発する。
            // 常に最後の操作時刻を記録し、実行側でも一定の無操作期間を確認する。
            if (_deferredWakeFirstRequestUtc == DateTime.MinValue)
                _deferredWakeFirstRequestUtc = now;
            _deferredWakeDueUtc = due;
            _deferredWakeLastRequestUtc = now;
            _deferredWakeReason = reason;
            _deferredWakeRequestCount++;
            count = _deferredWakeRequestCount;
            currentDue = _deferredWakeDueUtc;
        }

        _log.Add("TaskScheduler", "WakeDebounce",
            $"Wakeタスク再構築を遅延予約: {reason} dueInMs={(int)effectiveDelay.TotalMilliseconds} requestCount={count} dueUtc={currentDue:O} quietMs={(int)DeferredWakeQuietPeriod.TotalMilliseconds} nearWindowMin={(int)DeferredWakeImmediateWindow.TotalMinutes} rule=release_contract");

        // release_contract:
        // Wake本体の再構築を遅延させる場合でも、一覧上の旧WakeSlot増殖はすぐ分かる。
        // 新規登録は行わず、現在のdesiredに含まれない残骸だけを安全掃除する。
        var desiredNow = BuildDesiredTasks(DateTime.Now, emitAudit: false);
        var generationNow = ResolveDesiredWakeGeneration(desiredNow);
        var desiredNamesNow = desiredNow.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CleanupWakeSlotOverflowIfNeeded(generationNow, desiredNamesNow, reason: "deferred_request_pre_cleanup");
    }

    private bool TryRunImmediateWakeRefreshForNearWake(string reason, TimeSpan requestedDelay)
    {
        var nowLocal = DateTime.Now;
        var nextWake = BuildDesiredTasks(nowLocal, emitAudit: false).OrderBy(t => t.When).FirstOrDefault();
        if (nextWake is null)
            return false;

        var nextDelay = nextWake.When - nowLocal;
        if (nextDelay > DeferredWakeImmediateWindow)
            return false;

        int clearedRequests;
        lock (_deferredWakeGate)
        {
            clearedRequests = _deferredWakeRequestCount;
            _deferredWakeDueUtc = DateTime.MinValue;
            _deferredWakeFirstRequestUtc = DateTime.MinValue;
            _deferredWakeLastRequestUtc = DateTime.MinValue;
            _deferredWakeReason = string.Empty;
            _deferredWakeRequestCount = 0;
        }

        _log.Add("TaskScheduler", "WakeDebounce",
            $"Wakeタスク再構築を即時実行: {reason} reason=near-wake next={nextWake.When:MM/dd HH:mm:ss} task={nextWake.Name} kind={nextWake.Kind} res=R{nextWake.Reservation.Id} delaySec={(int)Math.Max(0, nextDelay.TotalSeconds)} requestedDelayMs={(int)requestedDelay.TotalMilliseconds} clearedDeferredRequests={clearedRequests} action=update_now rule=release_contract");

        UpdateWakeTaskCore($"near-wake:{reason}");
        return true;
    }

    public bool HasPendingDeferredWakeTask()
    {
        lock (_deferredWakeGate)
        {
            return _deferredWakeDueUtc != DateTime.MinValue;
        }
    }

    public bool ApplyDeferredWakeTaskIfDue()
    {
        string reason;
        int count;
        var now = DateTime.UtcNow;
        lock (_deferredWakeGate)
        {
            if (_deferredWakeDueUtc == DateTime.MinValue || now < _deferredWakeDueUtc)
                return false;

            var quietFor = now - _deferredWakeLastRequestUtc;
            if (quietFor < DeferredWakeQuietPeriod)
            {
                _deferredWakeDueUtc = _deferredWakeLastRequestUtc.Add(DeferredWakeQuietPeriod);
                _log.Add("TaskScheduler", "WakeDebounce",
                    $"Wakeタスク遅延再構築を再延期: reason=quiet-period lastRequestUtc={_deferredWakeLastRequestUtc:O} quietMs={(int)quietFor.TotalMilliseconds} nextDueUtc={_deferredWakeDueUtc:O} requestCount={_deferredWakeRequestCount} rule=release_contract");
                return false;
            }

            var firstRequestUtc = _deferredWakeFirstRequestUtc;
            var heldFor = firstRequestUtc == DateTime.MinValue ? TimeSpan.Zero : now - firstRequestUtc;
            var nextWake = BuildDesiredTasks(DateTime.Now, emitAudit: false).OrderBy(t => t.When).FirstOrDefault();
            var nextWakeDelay = nextWake is null ? TimeSpan.MaxValue : nextWake.When - DateTime.Now;
            if (nextWakeDelay > DeferredWakeUrgentWindow && heldFor < DeferredWakeMaxHold)
            {
                _deferredWakeDueUtc = now.Add(DeferredWakeDefaultDelay);
                _log.Add("TaskScheduler", "WakeDebounce",
                    $"Wakeタスク遅延再構築を再延期: reason=far-next-wake next={(nextWake is null ? "none" : nextWake.When.ToString("MM/dd HH:mm:ss"))} nextDelayMin={(nextWakeDelay == TimeSpan.MaxValue ? -1 : (int)nextWakeDelay.TotalMinutes)} heldMs={(int)heldFor.TotalMilliseconds} nextDueUtc={_deferredWakeDueUtc:O} requestCount={_deferredWakeRequestCount} rule=release_contract");
                return false;
            }

            reason = _deferredWakeReason;
            count = _deferredWakeRequestCount;
            _deferredWakeDueUtc = DateTime.MinValue;
            _deferredWakeFirstRequestUtc = DateTime.MinValue;
            _deferredWakeLastRequestUtc = DateTime.MinValue;
            _deferredWakeReason = string.Empty;
            _deferredWakeRequestCount = 0;
        }

        _log.Add("TaskScheduler", "WakeDebounce",
            $"Wakeタスク遅延再構築を実行: {reason} mergedRequests={count} rule=release_contract");
        UpdateWakeTaskCore($"deferred:{reason}");
        return true;
    }

    /// <summary>
    /// 次回の Wake 情報(最も早く発火するWakeタスク)を返す。
    /// 予約がない・過去時刻の場合は null を返す。
    /// </summary>
    public WakeTaskInfo? GetNextWakeInfo()
    {
        var desired = BuildDesiredTasks(DateTime.Now, emitAudit: false);
        var next = desired.OrderBy(t => t.When).FirstOrDefault();
        if (next is null)
            return null;

        return new WakeTaskInfo(
            WakeAt:            next.When,
            ReservationId:     next.Reservation.Id,
            Title:             next.Reservation.Title,
            StartTime:         next.Reservation.StartTime,
            WakeMinutesBefore: _ini.WakeAdditionalSeconds / 60);
    }

    /// <summary>
    /// release_contract: プラグインSDK/読み取りAPI向けのWake計画スナップショット。
    /// Windowsタスクの直接操作情報ではなく、TvAIrが現在必要と判断している安全なWake計画だけを返す。
    /// </summary>
    public IReadOnlyList<WakeTaskPlanItem> GetWakePlanSnapshot(DateTime? from = null, DateTime? to = null, int limit = 100)
    {
        var now = DateTime.Now;
        var min = from ?? now;
        var max = to ?? now.AddDays(14);
        var safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);

        return BuildDesiredTasks(now, emitAudit: false)
            .Where(t => t.When >= min && t.When < max)
            .OrderBy(t => t.When)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(safeLimit)
            .Select(t => new WakeTaskPlanItem(
                At: t.When,
                Kind: t.Kind,
                ReservationId: t.Reservation.Id,
                Title: t.Reservation.Title,
                TaskName: t.Name))
            .ToList();
    }

    private int GetRecordingWakeTunerCount()
    {
        // release_contract:
        // 通常Wake運用の目標は録画チューナー数。Viewingチューナーは録画Wakeの収容数へ入れない。
        // Recording/Shared/空欄など、Viewing以外は録画用として扱う。
        var count = _ini.Tuners
            .Count(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase));

        return Math.Max(0, count);
    }

    private int GetWakeSlotLimit()
    {
        // release_contract:
        // 通常目標は既存Wake運用寄せで「録画チューナー数ぶんの起床時刻スロット」。
        // 13本(録画チューナー数×2+制御1)は常時目標ではなく、異常/非常時の上限としてログに残す。
        var recordingTunerCount = GetRecordingWakeTunerCount();
        return Math.Max(WakeSlotControlTaskCount, recordingTunerCount);
    }

    private int GetWakeSlotEmergencyLimit()
    {
        var recordingTunerCount = GetRecordingWakeTunerCount();
        return Math.Max(WakeSlotControlTaskCount, recordingTunerCount * 2 + WakeSlotControlTaskCount);
    }

    private List<WakeTaskSpec> BuildDesiredTasks(DateTime now, bool emitAudit = true)
    {
        var scheduled = _store.GetByStatus(ReservationStatus.Scheduled)
            .Where(r => !r.IsConflicted)
            .Where(r => r.IsEnabled)
            .Where(r => r.StartTime > now)
            .Where(r => !string.IsNullOrWhiteSpace(r.TunerName))
            .ToList();

        var preRecMin  = (_ini.EpgPreRecordMinutes is 5 or 10 or 15 or 20 ? _ini.EpgPreRecordMinutes : 15);
        var preMarSec  = Math.Max(0, _ini.PreStartMarginSeconds);
        var wakeAddSec = Math.Max(0, _ini.WakeAdditionalSeconds);

        // release_contract:
        // WakeTaskSpec は Task Scheduler 登録専用。予約/目的/予約もと/チェーン等のメタ属性は
        // WakeCoverageItem に分離し、Reservation 契約を要求する既存メソッドへ疑似Specを流し込まない。
        // PRE_EPG / REC / SYSTEM_EPG は Task Scheduler の登録単位にせず、WakeCoverage として
        // TvAIr内部に保持する。起床後の実処理判断は常駐TvAIrの共通割当/EPG経路へ戻す。
        // RECOVERYはTask Schedulerで予約ごとに膨らませず、録画中の本体監視/内部タイマー側へ寄せる。
        var coverageCandidates = new List<WakeCoverageItem>();

        void AddCoverage(string purpose, DateTime when, Reservation reservation)
        {
            if (when <= now) return;
            coverageCandidates.Add(new WakeCoverageItem(
                Purpose: purpose,
                When: when,
                Reservation: reservation,
                SourceLabel: NormalizeWakeSourceLabel(reservation.Source),
                Route: ResolveWakeCoverageRoute(reservation),
                ChainRoot: reservation.UserChainRootId,
                ChainPrev: reservation.UserChainPreviousId));
        }

        var userReservations = scheduled
            .Where(r => r.Source != ReservationSource.Epg)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        foreach (var r in userReservations)
        {
            var wakeRec = r.StartTime.AddSeconds(-preMarSec).AddSeconds(-wakeAddSec);
            AddCoverage("REC", wakeRec, r);

            if (_ini.EpgPreRecordMinutes > 0
                && !r.IsUserChain
                && (r.Source == ReservationSource.Manual
                    || r.Source == ReservationSource.KeywordSearch
                    || r.Source == ReservationSource.Keyword))
            {
                var wakeEpg = r.StartTime.AddMinutes(-preRecMin).AddSeconds(-wakeAddSec);
                AddCoverage("PRE_EPG", wakeEpg, r);
            }
        }

        var scheduledDailyEpg = scheduled
            .Where(IsDailyEpgScheduleEntry)
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.Id)
            .ToList();

        var nextDailyEpgGroup = scheduledDailyEpg
            .GroupBy(r => r.StartTime.AddSeconds(-wakeAddSec))
            .OrderBy(g => g.Key)
            .FirstOrDefault(g => g.Key > now);

        if (nextDailyEpgGroup is not null)
        {
            var representative = nextDailyEpgGroup.OrderBy(r => r.Id).First();
            AddCoverage("SYSTEM_EPG", nextDailyEpgGroup.Key, representative);
        }

        var targetWakeSlotLimit = GetWakeSlotLimit();
        var emergencyWakeSlotLimit = GetWakeSlotEmergencyLimit();

        // release_contract:
        // release_contractでは全未来Coverageを必須扱いし、録画チューナー数ぶんに収まらないものを
        // missingCoverage として出していた。これは「次に起こす」既存Wake運用寄せの意味ではなく、
        // 単なる未来予約の未登録一覧になってしまう。
        // ここでは、近い起床目的をWakeセッションへ束ね、録画チューナー数ぶんの直近Wakeだけを
        // requiredCoverage として扱う。範囲外の未来分は deferredCoverage として分離し、missing扱いしない。
        var orderedCoverage = coverageCandidates
            .Where(c => c.When > now)
            .OrderBy(c => c.When)
            .ThenBy(c => KindPriority(c.Purpose))
            .ThenBy(c => c.Reservation.StartTime)
            .ThenBy(c => c.Reservation.Id)
            .ToList();

        var wakeSessionGroups = BuildWakeCoverageSessionGroups(orderedCoverage, TimeSpan.FromMinutes(10));
        var slotGroups = wakeSessionGroups
            .Take(targetWakeSlotLimit)
            .ToList();

        var planGeneration = WakeSlotStableGeneration;
        var desired = new List<WakeTaskSpec>(slotGroups.Count);
        var coverageLines = new List<string>(slotGroups.Count);

        foreach (var group in slotGroups)
        {
            var representative = group.Items
                .OrderBy(c => KindPriority(c.Purpose))
                .ThenBy(c => c.Reservation.StartTime)
                .ThenBy(c => c.Reservation.Id)
                .First();

            var slotSpec = new WakeTaskSpec(
                Name: BuildWakeSlotTaskName(planGeneration, "WAKE", group.WakeAt),
                Kind: "WAKE",
                When: group.WakeAt,
                TunerName: "ALL",
                Reservation: representative.Reservation,
                CoverageCount: group.Items.Count);
            desired.Add(slotSpec);

            var covers = string.Join(",", group.Items
                .OrderBy(c => c.When)
                .ThenBy(c => KindPriority(c.Purpose))
                .ThenBy(c => c.Reservation.Id)
                .Select(c => $"R{c.Reservation.Id}:{c.Purpose}:{c.SourceLabel}:route={c.Route}:chainRoot={FormatNullableId(c.ChainRoot)}:chainPrev={FormatNullableId(c.ChainPrev)}:due={c.When:MM/dd HH:mm:ss}:{CompactOneLine(c.Reservation.Title)}"));
            coverageLines.Add($"slot={group.WakeAt:O}; task={slotSpec.Name}; requiredCoverage=True; covers={covers}");
        }

        if (emitAudit)
        {
            _lastWakeCoverageLines = coverageLines;
            var requiredCoverage = slotGroups.Sum(g => g.Items.Count);
            var deferredCoverage = Math.Max(0, orderedCoverage.Count - requiredCoverage);
            var missingCoverage = 0;
            _log.Add("TaskScheduler", "WAKE_PLAN_COVERAGE",
                $"result=OK class=audit mode=tvrock_style_minimal_wakeup requiredScope=next_wake_sessions coverageSlots={desired.Count} coverageItems={orderedCoverage.Count} requiredCoverage={requiredCoverage} coveredCoverage={requiredCoverage} missingCoverage={missingCoverage} deferredCoverage={deferredCoverage} deferredSessions={Math.Max(0, wakeSessionGroups.Count - slotGroups.Count)} targetTaskCount={targetWakeSlotLimit} emergencyLimit={emergencyWakeSlotLimit} recordingTunerCount={GetRecordingWakeTunerCount()} recoveryWakeTasks=0 recoveryPolicy=internal_timer_or_runtime_monitor taskKind=WAKE first={(desired.FirstOrDefault()?.When.ToString("MM/dd HH:mm:ss") ?? "none")} source=apply_path rule=release_contract");
        }

        return desired
            .OrderBy(s => s.When)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    private static List<WakeCoverageSessionGroup> BuildWakeCoverageSessionGroups(IReadOnlyList<WakeCoverageItem> orderedCoverage, TimeSpan mergeWindow)
    {
        var result = new List<WakeCoverageSessionGroup>();
        if (orderedCoverage.Count == 0)
            return result;

        var currentWakeAt = orderedCoverage[0].When;
        var currentItems = new List<WakeCoverageItem>();
        var lastDue = orderedCoverage[0].When;

        foreach (var item in orderedCoverage)
        {
            if (currentItems.Count > 0 && item.When - lastDue > mergeWindow)
            {
                result.Add(new WakeCoverageSessionGroup(currentWakeAt, currentItems));
                currentWakeAt = item.When;
                currentItems = new List<WakeCoverageItem>();
            }

            currentItems.Add(item);
            lastDue = item.When;
        }

        if (currentItems.Count > 0)
            result.Add(new WakeCoverageSessionGroup(currentWakeAt, currentItems));

        return result;
    }

    private static string BuildWakePlanGeneration(IEnumerable<WakeTaskSpec> specs)
    {
        // release_contract:
        // 計画ハッシュ由来の世代名は、予約追加/キャンセルのたびに同一内容タスクの名前まで変え、
        // 13本全削除→13本全登録を誘発していた。
        // Wakeの鮮度判定は wake-active-slots.txt のスロットID照合で行うため、
        // タスク名側の世代は安定値に固定し、差分適用を有効にする。
        _ = specs;
        return WakeSlotStableGeneration;
    }

    private static string ResolveDesiredWakeGeneration(IReadOnlyCollection<WakeTaskSpec> desired)
    {
        var first = desired.OrderBy(t => t.When).FirstOrDefault();
        return first is null ? WakeSlotGenerationFallback : ExtractWakeGenerationFromTaskName(first.Name);
    }

    private static string ExtractWakeGenerationFromTaskName(string? taskName)
    {
        if (string.IsNullOrWhiteSpace(taskName))
            return WakeSlotGenerationFallback;
        var name = taskName.Trim().TrimStart('\\');
        if (!name.StartsWith(WakeSlotTaskPrefix, StringComparison.OrdinalIgnoreCase))
            return WakeSlotGenerationFallback;
        var rest = name[WakeSlotTaskPrefix.Length..];
        var idx = rest.IndexOf("_20", StringComparison.Ordinal);
        if (idx > 0)
            return rest[..idx];
        var parts = rest.Split('_');
        return parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : WakeSlotGenerationFallback;
    }

    private static string BuildWakeSlotTaskName(string generation, string kind, DateTime when)
        => $"{WakeSlotTaskPrefix}{generation}_{when:yyyyMMdd_HHmmss}_WAKE";

    private static int KindPriority(string kind)
        => string.Equals(kind, "PRE_EPG", StringComparison.OrdinalIgnoreCase) ? 0
         : string.Equals(kind, "REC", StringComparison.OrdinalIgnoreCase) ? 1
         : string.Equals(kind, "SYSTEM_EPG", StringComparison.OrdinalIgnoreCase) ? 2
         : string.Equals(kind, "WAKE", StringComparison.OrdinalIgnoreCase) ? 3
         : 9;

    private static string NormalizeWakeSourceLabel(ReservationSource source)
        => source switch
        {
            ReservationSource.Manual => "番組表",
            ReservationSource.KeywordSearch or ReservationSource.Keyword => "自動検索",
            ReservationSource.Program => "プログラム",
            ReservationSource.Epg => "システム",
            _ => source.ToString()
        };

    private static string ResolveWakeCoverageRoute(Reservation reservation)
        => reservation.IsUserChain ? "user_chain"
         : reservation.Source == ReservationSource.Manual ? "program_guide"
         : reservation.Source is ReservationSource.KeywordSearch or ReservationSource.Keyword ? "auto_search"
         : reservation.Source == ReservationSource.Program ? "program"
         : reservation.Source == ReservationSource.Epg ? "system_epg"
         : reservation.Source.ToString();

    private static string FormatNullableId(int? id)
        => id.HasValue && id.Value > 0 ? $"R{id.Value}" : "-";

    private static bool IsDailyEpgScheduleEntry(Reservation r)
        => r.Source == ReservationSource.Epg
           && r.Title.StartsWith("EPG取得", StringComparison.Ordinal);

    private bool HasFutureScheduledDailyEpg(DateTime now)
        => _store.GetByStatus(ReservationStatus.Scheduled)
            .Any(r => r.IsEnabled
                      && !r.IsConflicted
                      && r.StartTime > now
                      && IsDailyEpgScheduleEntry(r));

    private static string BuildPlanSignature(IEnumerable<WakeTaskSpec> specs)
        => string.Join("|", specs
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"{t.Name}@{t.When:yyyyMMddHHmmss}"));

    private static string ShortHash(string? signature)
    {
        if (signature is null)
            return "null";
        if (signature.Length == 0)
            return "empty";

        var hash = 2166136261u;
        foreach (var ch in signature)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash.ToString("X8");
    }

    private static string ResolveTvAIrWakeExecutablePath()
    {
        var appLocalExe = Path.Combine(AppContext.BaseDirectory, "TvAIr.exe");
        if (File.Exists(appLocalExe))
            return appLocalExe;

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        try
        {
            var mainModule = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModule) && File.Exists(mainModule))
                return mainModule;
        }
        catch { }

        return appLocalExe;
    }

    private static string BuildWakeTaskArguments(WakeTaskSpec spec)
    {
        var kind = string.IsNullOrWhiteSpace(spec.Kind) ? "UNKNOWN" : spec.Kind.Trim();
        var generation = ExtractWakeGenerationFromTaskName(spec.Name);
        // release_contract: Task Scheduler の起動引数に予約IDを主キーとして持たせない。
        // 予約/目的メタ属性は runtime/wake-active-coverage.txt を正として保持し、
        // 起床後は既存TvAIrが予約DBから再評価する。
        return $"--wake-task {kind} --wake-at {spec.When:yyyyMMddHHmmss} --wake-generation {generation} --wake-slot-id {SanitizeTaskSegment(spec.Name)} --wake-reservation-id -";
    }

    private WakeTaskApplyResult RegisterOrUpdateTask(WakeTaskSpec spec)
    {
        // release_contract:
        // 世代付きWakeSlotは衝突回避を名前で担保する。登録前削除はアクセス拒否を誘発しやすいため、
        // 同名契約不一致の再登録以外では行わない。ここでは新規登録/上書き登録だけに寄せる。
        var userName = _ini.TaskUserName;
        var password = string.IsNullOrEmpty(_ini.TaskPasswordEncrypted)
            ? null
            : CredentialProtector.Decrypt(_ini.TaskPasswordEncrypted);

        var wakeExe = ResolveTvAIrWakeExecutablePath();
        var wakeArgs = BuildWakeTaskArguments(spec);
        var workDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        _log.Add("TaskScheduler", spec.Name,
            $"Wakeタスク登録開始 kind={spec.Kind} tuner={spec.TunerName} coverage={spec.CoverageCount} at={spec.When:MM/dd HH:mm:ss} user=(configured) logon=Password runLevel=LeastPrivilege action=wake_task_signal_contract exe={CompactOneLine(wakeExe)} rule=release_contract");

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            _log.Add("TaskScheduler", spec.Name,
                $"WakeタスクPassword登録スキップ kind={spec.Kind} coverage={spec.CoverageCount} reason=missing_credentials action=try_interactive_fallback rule=release_contract");
        }
        else
        {
            var passwordXml = BuildWakeTaskXml(spec, userName, "Password", wakeExe, wakeArgs, workDir);
            var passwordResult = RegisterTaskFromXmlWithPassword(spec.Name, passwordXml, userName, password);
            if (passwordResult.Success)
                return VerifyRegisteredTaskResult(spec, expectedLogon: "Password", fallbackUsed: false);

            _log.Add("TaskScheduler", spec.Name,
                $"WakeタスクPassword登録失敗 kind={spec.Kind} tuner={spec.TunerName} coverage={spec.CoverageCount} at={spec.When:MM/dd HH:mm:ss} error={CompactOneLine(passwordResult.Output)} action=try_interactive_fallback rule=release_contract");
        }

        var fallbackUser = GetCurrentUserNameForTask();
        var interactiveXml = BuildWakeTaskXml(spec, fallbackUser, "InteractiveToken", wakeExe, wakeArgs, workDir);
        var interactiveResult = RegisterTaskFromXmlInteractive(spec.Name, interactiveXml);
        if (!interactiveResult.Success)
        {
            _log.Add("TaskScheduler", spec.Name,
                $"Wakeタスク登録失敗 kind={spec.Kind} tuner={spec.TunerName} coverage={spec.CoverageCount} at={spec.When:MM/dd HH:mm:ss} passwordFallback=FAILED interactiveFallback=FAILED error={CompactOneLine(interactiveResult.Output)} risk=sleep_resume_not_guaranteed rule=release_contract");
            return new WakeTaskApplyResult(spec, false);
        }

        _log.Add("TaskScheduler", spec.Name,
            $"WakeタスクInteractiveFallback登録成功 kind={spec.Kind} tuner={spec.TunerName} coverage={spec.CoverageCount} at={spec.When:MM/dd HH:mm:ss} note=run_only_when_user_is_logged_on WakeToRun=true risk=lower_than_password_but_better_than_no_wake rule=release_contract");

        return VerifyRegisteredTaskResult(spec, expectedLogon: "InteractiveToken", fallbackUsed: true);
    }

    private WakeTaskApplyResult VerifyRegisteredTaskResult(WakeTaskSpec spec, string expectedLogon, bool fallbackUsed)
    {
        var verify = VerifyTask(spec.Name);
        var logonOk = string.Equals(verify.LogonType, expectedLogon, StringComparison.OrdinalIgnoreCase)
                      || (fallbackUsed && string.Equals(verify.LogonType, "InteractiveToken", StringComparison.OrdinalIgnoreCase))
                      || (!fallbackUsed && string.Equals(verify.LogonType, "Password", StringComparison.OrdinalIgnoreCase));

        var runLevelReadback = string.IsNullOrWhiteSpace(verify.RunLevel) || verify.RunLevel.Contains("missing", StringComparison.OrdinalIgnoreCase)
            ? "UNAVAILABLE"
            : verify.RunLevel;
        var runLevelEffective = runLevelReadback == "UNAVAILABLE" ? "ASSUMED_LEAST_PRIVILEGE" : runLevelReadback;
        var success = verify.QuerySucceeded && verify.WakeToRun && verify.StartWhenAvailable && logonOk;
        _log.Add("TaskScheduler", spec.Name,
            $"Wakeタスク登録 kind={spec.Kind} tuner={spec.TunerName} coverage={spec.CoverageCount} at={spec.When:MM/dd HH:mm:ss} verify WakeToRun={(verify.WakeToRun ? "OK" : "NG")} StartWhenAvailable={(verify.StartWhenAvailable ? "OK" : "NG")} LogonType={verify.LogonType} expectedLogon={expectedLogon} RunLevelRequested=LeastPrivilege RunLevelReadback={runLevelReadback} RunLevelEffective={runLevelEffective} Query={(verify.QuerySucceeded ? "OK" : "NG")} fallbackUsed={fallbackUsed} action=wake_task_signal_contract rule=release_contract");
        return new WakeTaskApplyResult(spec, success);
    }

    private static string BuildWakeTaskXml(WakeTaskSpec spec, string userName, string logonType, string wakeExe, string wakeArgs, string workDir)
    {
        var escapedUser = System.Security.SecurityElement.Escape(userName);
        var escapedWakeExe = System.Security.SecurityElement.Escape(wakeExe);
        var escapedWakeArgs = System.Security.SecurityElement.Escape(wakeArgs);
        var escapedWorkDir = System.Security.SecurityElement.Escape(workDir);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <TimeTrigger>
                  <StartBoundary>{spec.When:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{escapedUser}</UserId>
                  <LogonType>{logonType}</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <WakeToRun>true</WakeToRun>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedWakeExe}</Command>
                  <Arguments>{escapedWakeArgs}</Arguments>
                  <WorkingDirectory>{escapedWorkDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private SchtasksResult RegisterTaskFromXmlWithPassword(string taskName, string xml, string userName, string password)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"tvair_task_{SanitizeTaskSegment(taskName)}.xml");
        try
        {
            File.WriteAllText(tmpFile, xml, new UnicodeEncoding(false, true));
            var args = $"/Create /TN \"{taskName}\" /XML \"{tmpFile}\" /F /RU \"{userName}\" /RP \"{password}\"";
            return RunSchtasks(args);
        }
        catch (Exception ex)
        {
            return new SchtasksResult(false, ex.Message);
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    private SchtasksResult RegisterTaskFromXmlInteractive(string taskName, string xml)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"tvair_task_{SanitizeTaskSegment(taskName)}_interactive.xml");
        try
        {
            File.WriteAllText(tmpFile, xml, new UnicodeEncoding(false, true));
            var args = $"/Create /TN \"{taskName}\" /XML \"{tmpFile}\" /F";
            return RunSchtasks(args);
        }
        catch (Exception ex)
        {
            return new SchtasksResult(false, ex.Message);
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    private static bool LooksLikeTaskNotFound(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        return output.Contains("指定されたタスク名", StringComparison.OrdinalIgnoreCase)
               || output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
               || output.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
               || output.Contains("見つかりません", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCurrentUserNameForTask()
    {
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return $"{domain}\\{user}";
        return user;
    }

    private static string BuildTaskName(string prefix, string tunerToken, int reservationId, DateTime when)
        => $"{prefix}{tunerToken}_{reservationId}_{when:yyyyMMdd_HHmmss}";

    private static string BuildGroupedTaskName(string kind, DateTime when)
    {
        var prefix = string.Equals(kind, "SYSTEM_EPG", StringComparison.OrdinalIgnoreCase)
            ? "TvAIr_Wake_SystemEpg_"
            : (string.Equals(kind, "EPG", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "PRE_EPG", StringComparison.OrdinalIgnoreCase))
                ? "TvAIr_Wake_PreEpg_"
                : WakeRecTaskPrefix;
        return $"{prefix}{when:yyyyMMdd_HHmmss}";
    }

    private bool HasWakeCredentials()
        => !string.IsNullOrWhiteSpace(_ini.TaskUserName)
           && !string.IsNullOrWhiteSpace(_ini.TaskPasswordEncrypted);

    private static string SanitizeTaskSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        var sanitized = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private HashSet<string> GetManagedTaskNames(string activeGeneration)
    {
        var result = RunSchtasks("/Query /FO CSV /NH");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return names;

        foreach (var rawLine in result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var firstField = ExtractFirstCsvField(line);
            if (string.IsNullOrWhiteSpace(firstField))
                continue;

            var taskName = firstField.Trim().Trim('"').Trim();
            taskName = taskName.TrimStart('\\');

            // release_contract:
            // 新方式のWakeSlotだけをTvAIr管理対象として扱う。
            // 旧Primary/Backup/SystemEpg固定名は、権限不整合で削除・上書きできない場合があるため、
            // ここでは管理対象に含めず、新規WakeSlot登録を阻害させない。
            var activePrefix = WakeSlotTaskPrefix + activeGeneration + "_";
            if (taskName.StartsWith(activePrefix, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(taskName);
            }
        }

        return names;
    }


    private void AuditStaleWakeSlotNamesOutsideCurrentGeneration(string activeGeneration)
    {
        var result = RunSchtasks("/Query /FO CSV /NH");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return;

        var staleCount = 0;
        var sample = new List<string>();
        foreach (var rawLine in result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var firstField = ExtractFirstCsvField(rawLine.Trim());
            if (string.IsNullOrWhiteSpace(firstField))
                continue;
            var taskName = firstField.Trim().Trim('"').Trim().TrimStart('\\');
            if (!taskName.StartsWith(WakeSlotTaskPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var activePrefix = WakeSlotTaskPrefix + activeGeneration + "_";
            if (taskName.StartsWith(activePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            staleCount++;
            if (sample.Count < 3)
                sample.Add(taskName);
        }

        if (staleCount > 0)
        {
            _log.Add("TaskScheduler", "WAKE_STALE_GENERATION_AUDIT",
                $"result=INFO stale={staleCount} currentGeneration={activeGeneration} sample={CompactOneLine(string.Join(",", sample))} action=ignore_for_current_plan deleteInMainSync=False recording_blocking=False rule=release_contract");
        }
    }

    private void CleanupStaleWakeSlotTasksAfterSuccessfulApply(string activeGeneration, HashSet<string> desiredNames, string reason)
    {
        // release_contract:
        // 旧世代WakeSlotのうち current_process でも削除できないものは、通常同期で毎回Delete/Disableを試すと
        // アクセス拒否と資格情報NGを増幅し、現行Wake登録を重くする。現行世代 desired を唯一の管理対象とし、
        // 旧世代は wake-active-generation / wake-active-slots により実行時に無効化される orphan として隔離する。
        // 自動削除対象は、現行世代内の desired 外と旧固定名風だけに限定する。
        var allWakeSlots = GetAllWakeSlotTaskNames();
        if (allWakeSlots.Count == 0)
            return;

        var activePrefix = WakeSlotTaskPrefix + activeGeneration + "_";
        var legacyFixed = new List<string>();
        var staleGeneration = new List<string>();
        var currentExtra = new List<string>();
        var preserved = 0;

        foreach (var taskName in allWakeSlots.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (desiredNames.Contains(taskName))
            {
                preserved++;
                continue;
            }

            if (taskName.StartsWith(activePrefix, StringComparison.OrdinalIgnoreCase))
            {
                currentExtra.Add(taskName);
                continue;
            }

            var suffix = taskName.Length > WakeSlotTaskPrefix.Length ? taskName[WakeSlotTaskPrefix.Length..] : string.Empty;
            if (!suffix.StartsWith("g", StringComparison.OrdinalIgnoreCase))
                legacyFixed.Add(taskName);
            else
                staleGeneration.Add(taskName);
        }

        var targets = legacyFixed
            .Concat(currentExtra)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log.Add("TaskScheduler", "WAKE_CLEANUP_SCAN",
            $"activeGeneration={activeGeneration} activeDesired={desiredNames.Count} preservedActive={preserved} legacyFixed={legacyFixed.Count} staleGeneration={staleGeneration.Count} staleGenerationQuarantined={staleGeneration.Count} currentExtra={currentExtra.Count} deleteTargets={targets.Count} reason={reason} recording_blocking=False rule=wake_stale_orphan_quarantine_guard");

        if (staleGeneration.Count > 0)
        {
            var sample = string.Join(",", staleGeneration.Take(3));
            _log.Add("TaskScheduler", "WAKE_STALE_ORPHAN_QUARANTINE",
                $"result=INFO activeGeneration={activeGeneration} orphanedStale={staleGeneration.Count} sample={CompactOneLine(sample)} action=exclude_from_active_count_and_do_not_retry_delete currentWakeGuard=active_generation_and_slot_id recording_blocking=False rule=wake_stale_orphan_quarantine_guard");
        }

        if (targets.Count == 0)
        {
            _log.Add("TaskScheduler", "WAKE_CLEANUP_PRESERVE_ACTIVE",
                $"preserved={preserved} activeGeneration={activeGeneration} orphanedStale={staleGeneration.Count} reason={reason} rule=wake_stale_orphan_quarantine_guard");
            return;
        }

        var deletedLegacy = 0;
        var deletedExtra = 0;
        var failedLegacy = 0;
        var failedExtra = 0;
        var failedLegacySamples = new List<string>();

        foreach (var taskName in targets)
        {
            if (desiredNames.Contains(taskName))
                continue;

            var targetKind = currentExtra.Contains(taskName, StringComparer.OrdinalIgnoreCase)
                ? "current_extra"
                : "legacy_fixed";

            var result = DeleteTask(taskName, suppressMaintenanceLog: string.Equals(targetKind, "current_extra", StringComparison.OrdinalIgnoreCase));
            var deleted = result.Success || LooksLikeTaskNotFound(result.Output);
            if (deleted)
            {
                if (targetKind == "current_extra") deletedExtra++;
                else deletedLegacy++;
            }
            else
            {
                // release_contract: current-generation extra tasks are non-blocking as long as desired WakeSlots are preserved.
                // Do not emit per-task identity diagnostics for those repeated access-denied failures in normal logs.
                if (targetKind == "current_extra")
                {
                    failedExtra++;
                }
                else
                {
                    AuditWakeTaskIdentity(taskName, targetKind, activeGeneration, reason);
                    failedLegacy++;
                    if (failedLegacySamples.Count < 3)
                        failedLegacySamples.Add($"{targetKind}:{taskName}:{CompactOneLine(result.Output)}");
                }
            }
        }

        var deletedTotal = deletedLegacy + deletedExtra;
        var failedTotal = failedLegacy + failedExtra;
        var cleanupResult = failedTotal == 0 ? "OK" : "PARTIAL_NON_BLOCKING";
        var cleanupDetail = failedLegacy == 0
            ? " failedDetails=suppressed"
            : $" failedSample={CompactOneLine(string.Join(" | ", failedLegacySamples))}";
        _log.Add("TaskScheduler", "WAKE_CLEANUP_DELETE",
            $"result={cleanupResult} activeGeneration={activeGeneration} preservedActive={preserved} orphanedStale={staleGeneration.Count} deleted={deletedTotal} failed={failedTotal} deletedLegacyFixed={deletedLegacy} deletedStaleGeneration=0 deletedCurrentExtra={deletedExtra} failedLegacyFixed={failedLegacy} failedStaleGeneration=0 failedCurrentExtra={failedExtra} currentExtraPolicy=nonblocking_desired_slots_preserved maintenanceDeniedPolicy=summary_only_for_current_extra recording_blocking=False sleep_resume_desired_tasks_preserved=True reason={reason}{(failedTotal == 0 ? string.Empty : cleanupDetail)} rule=wake_current_extra_nonblocking_policy");
        if (failedExtra > 0 && failedLegacy == 0 && preserved == desiredNames.Count)
        {
            _log.Add("TaskScheduler", "WAKE_CURRENT_EXTRA_NONBLOCKING_SUMMARY",
                $"result=OK_NONBLOCKING desired={desiredNames.Count} preservedActive={preserved} currentExtra={currentExtra.Count} failedCurrentExtra={failedExtra} recording_blocking=False action=keep_desired_and_suppress_per_task_maintenance_denied reason={reason} rule=wake_current_extra_nonblocking_policy");
        }
        if (failedTotal == 0 && preserved == desiredNames.Count)
        {
            _log.Add("TaskScheduler", "WAKE_CLEANUP_SUMMARY",
                $"result=OK_CONVERGED desired={desiredNames.Count} active={preserved} extra=0 recording_blocking=False reason={reason} rule=wake_current_extra_nonblocking_policy");
        }
    }

    private void AuditWakeTaskIdentity(string taskName, string targetKind, string activeGeneration, string reason)
    {
        var xmlResult = RunSchtasks($"/Query /TN \"{taskName}\" /XML");
        var runtime = QueryTaskRuntimeState(taskName);
        var principalUser = "(query failed)";
        var logonType = "(query failed)";
        var runLevel = "(query failed)";
        var author = "(query failed)";
        if (xmlResult.Success && !string.IsNullOrWhiteSpace(xmlResult.Output))
        {
            principalUser = ExtractXmlValue(xmlResult.Output, "UserId") ?? "(missing)";
            logonType = ExtractXmlValue(xmlResult.Output, "LogonType") ?? "(missing)";
            runLevel = ExtractXmlValue(xmlResult.Output, "RunLevel") ?? "(missing)";
            author = ExtractXmlValue(xmlResult.Output, "Author") ?? "(missing)";
        }

        _log.Add("TaskScheduler", "WAKE_TASK_IDENTITY_AUDIT",
            $"task={taskName} target={targetKind} activeGeneration={activeGeneration} reason={reason} " +
            $"currentProcessUser={CompactOneLine(GetCurrentUserNameForTask())} isElevated={IsCurrentProcessElevated()} configuredUser={(string.IsNullOrWhiteSpace(_ini.TaskUserName) ? "(missing)" : "(configured)")} " +
            $"principalUser={CompactOneLine(principalUser)} logonType={CompactOneLine(logonType)} runLevel={CompactOneLine(runLevel)} author={CompactOneLine(author)} " +
            $"state={CompactOneLine(runtime.Status)} nextRun={CompactOneLine(runtime.NextRun)} lastRun={CompactOneLine(runtime.LastRun)} lastResult={CompactOneLine(runtime.LastResult)} " +
            $"queryXml={(xmlResult.Success ? "OK" : "NG")} rule=release_contract");
    }

    private HashSet<string> GetAllWakeSlotTaskNames()
    {
        var result = RunSchtasks("/Query /FO CSV /NH");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return names;

        foreach (var rawLine in result.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var firstField = ExtractFirstCsvField(rawLine.Trim());
            if (string.IsNullOrWhiteSpace(firstField))
                continue;

            var taskName = firstField.Trim().Trim('"').Trim().TrimStart('\\');
            if (taskName.StartsWith(WakeSlotTaskPrefix, StringComparison.OrdinalIgnoreCase))
                names.Add(taskName);
        }

        return names;
    }

    private void CleanupWakeSlotOverflowIfNeeded(string activeGeneration, HashSet<string> desiredNames, string reason)
    {
        // release_contract:
        // allWakeSlots には削除不能な旧世代 orphan が含まれるため、上限超過判定にそのまま使わない。
        // リリース前Wake品質の正は、activeGeneration 配下で desired と照合できる managedActiveWakeSlots が
        // 「録画チューナー総数×2＋1」に収まること。旧世代は実行時slotId照合で無効化される別枠として隔離する。
        var allWakeSlots = GetAllWakeSlotTaskNames();
        if (allWakeSlots.Count == 0)
            return;

        var activePrefix = WakeSlotTaskPrefix + activeGeneration + "_";
        var activeWakeSlots = allWakeSlots
            .Where(name => name.StartsWith(activePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activeExtra = activeWakeSlots
            .Where(name => !desiredNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var staleOrphan = allWakeSlots
            .Where(name => !name.StartsWith(activePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recordingTunerCount = GetRecordingWakeTunerCount();
        var expectedLimit = GetWakeSlotLimit();
        var emergencyLimit = GetWakeSlotEmergencyLimit();
        var activeOverflow = activeWakeSlots.Count > emergencyLimit;
        var hasActivePlanExtra = activeExtra.Count > 0;
        if (!activeOverflow && !hasActivePlanExtra && staleOrphan.Count == 0)
            return;

        if (staleOrphan.Count > 0 && !activeOverflow && !hasActivePlanExtra)
        {
            var sample = string.Join(",", staleOrphan.Take(3));
            _log.Add("TaskScheduler", "WAKE_STALE_ORPHAN_QUARANTINE",
                $"result=INFO recordingTunerCount={recordingTunerCount} targetTaskCount={expectedLimit} emergencyLimit={emergencyLimit} allWakeSlots={allWakeSlots.Count} managedActiveWakeSlots={activeWakeSlots.Count} desired={desiredNames.Count} orphanedStale={staleOrphan.Count} sample={CompactOneLine(sample)} activeGeneration={activeGeneration} reason={reason} action=ignore_for_active_wake_count currentWakeGuard=active_generation_and_slot_id formula=recording_tuners_target_emergency_limit rule=wake_stale_orphan_quarantine_guard");
            return;
        }

        var desiredActivePreserved = activeWakeSlots.Count - activeExtra.Count >= desiredNames.Count;
        // release_contract:
        // activeExtra は「現行desiredは守れているが、同世代の余剰が残っている」監査であり、
        // overflow や plan changed と同じ警告名にしない。録画阻害ではないものは常に非阻害名へ寄せる。
        var currentExtraNonBlocking = activeExtra.Count > 0 && !activeOverflow;
        var eventName = currentExtraNonBlocking
            ? "WAKE_CURRENT_EXTRA_NONBLOCKING_POLICY"
            : (activeOverflow ? "WAKE_SLOT_OVERFLOW_DETECTED" : "WAKE_PLAN_CHANGED_CLEANUP");
        var cleanupReason = currentExtraNonBlocking
            ? $"current_extra_nonblocking_{reason}"
            : (activeOverflow ? $"active_overflow_{reason}" : $"active_plan_changed_{reason}");
        var action = currentExtraNonBlocking ? "safe_cleanup_active_extra_only_nonblocking" : "safe_cleanup_active_only";
        _log.Add("TaskScheduler", eventName,
            $"result={(currentExtraNonBlocking ? "INFO_NONBLOCKING" : "INFO")} recordingTunerCount={recordingTunerCount} targetTaskCount={expectedLimit} emergencyLimit={emergencyLimit} allWakeSlots={allWakeSlots.Count} managedActiveWakeSlots={activeWakeSlots.Count} desired={desiredNames.Count} desiredActivePreserved={desiredActivePreserved} activeExtra={activeExtra.Count} orphanedStale={staleOrphan.Count} activeGeneration={activeGeneration} reason={reason} actualOverflow={activeOverflow} generationRotate=False action={action} recording_blocking=False formula=recording_tuners_target_emergency_limit rule=release_contract");

        CleanupStaleWakeSlotTasksAfterSuccessfulApply(activeGeneration, desiredNames, reason: cleanupReason);
    }

    private static string ActiveWakeGenerationFilePath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime");
        return Path.Combine(dir, "wake-active-generation.txt");
    }

    private static string ActiveWakeSlotsFilePath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime");
        return Path.Combine(dir, "wake-active-slots.txt");
    }

    private void WriteActiveWakePlan(string generation, IReadOnlyCollection<WakeTaskSpec> desired)
    {
        try
        {
            var genFile = ActiveWakeGenerationFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(genFile)!);
            File.WriteAllText(genFile, generation.Trim(), Encoding.UTF8);

            var slotFile = ActiveWakeSlotsFilePath();
            var lines = desired
                .OrderBy(t => t.When)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => SanitizeTaskSegment(t.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(slotFile, lines, Encoding.UTF8);

            var coverageFile = ActiveWakeCoverageFilePath();
            File.WriteAllLines(coverageFile, _lastWakeCoverageLines, Encoding.UTF8);
        }
        catch { }
    }

    private static string ActiveWakeCoverageFilePath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime");
        return Path.Combine(dir, "wake-active-coverage.txt");
    }

    private static string ReadActiveWakeGenerationFallback()
    {
        try
        {
            var file = ActiveWakeGenerationFilePath();
            return File.Exists(file) ? File.ReadAllText(file).Trim() : WakeSlotGenerationFallback;
        }
        catch { return WakeSlotGenerationFallback; }
    }

    private static string BuildWakeCredentialProbeReason(
        int specsToRegister,
        int missingDesired,
        int contractMismatchDesired,
        int unexpectedExisting,
        bool planChanged,
        bool validationExpired)
    {
        if (specsToRegister <= 0)
            return "no_registration_required";
        if (missingDesired > 0 && contractMismatchDesired > 0)
            return "missing_and_contract_mismatch_require_registration";
        if (missingDesired > 0)
            return "missing_desired_requires_registration";
        if (contractMismatchDesired > 0)
            return "contract_mismatch_requires_reregistration";
        if (planChanged)
            return "plan_changed_requires_registration";
        if (validationExpired)
            return "validation_expired_requires_registration";
        if (unexpectedExisting > 0)
            return "unexpected_existing_with_registration";
        return "registration_required";
    }

    private bool ProbeWakeCredentialContract(
        string reason,
        int specsToRegister,
        int missingDesired,
        int contractMismatchDesired,
        int unexpectedExisting,
        bool planChanged,
        bool validationExpired)
    {
        var probeName = $"{WakeProbeTaskPrefix}{DateTime.Now:yyyyMMdd_HHmmss}";
        var probeReservation = new Reservation
        {
            Id = 0,
            Title = "WakeCredentialProbe",
            Source = ReservationSource.Manual,
            StartTime = DateTime.Now.AddMinutes(10),
            EndTime = DateTime.Now.AddMinutes(11),
            TunerName = "ALL"
        };
        var spec = new WakeTaskSpec(probeName, "PROBE", DateTime.Now.AddMinutes(2), "ALL", probeReservation);
        var userName = _ini.TaskUserName;
        var password = string.IsNullOrEmpty(_ini.TaskPasswordEncrypted)
            ? null
            : CredentialProtector.Decrypt(_ini.TaskPasswordEncrypted);
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            _log.Add("TaskScheduler", "WAKE_CREDENTIAL_PROBE",
                $"result=FAILED reason=missing_credentials specsToRegister={specsToRegister} action=keep_existing_generation");
            return false;
        }

        var wakeExe = ResolveTvAIrWakeExecutablePath();
        var wakeArgs = BuildWakeTaskArguments(spec);
        var workDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var xml = BuildWakeTaskXml(spec, userName, "Password", wakeExe, wakeArgs, workDir);
        var register = RegisterTaskFromXmlWithPassword(probeName, xml, userName, password);
        if (!register.Success)
        {
            _log.Add("TaskScheduler", "WAKE_CREDENTIAL_PROBE",
                $"result=FAILED phase=register reason={reason} error={CompactOneLine(register.Output)} action=keep_existing_generation");
            return false;
        }

        var verify = VerifyTask(probeName);
        var delete = DeleteTask(probeName);
        var ok = verify.QuerySucceeded && verify.WakeToRun && verify.StartWhenAvailable && delete.Success;
        if (!ok)
        {
            _log.Add("TaskScheduler", "WAKE_CREDENTIAL_PROBE",
                $"result=FAILED phase=verify reason={reason} query={(verify.QuerySucceeded ? "OK" : "NG")} WakeToRun={(verify.WakeToRun ? "OK" : "NG")} StartWhenAvailable={(verify.StartWhenAvailable ? "OK" : "NG")} LogonType={verify.LogonType} delete={(delete.Success ? "OK" : "NG")} action=keep_existing_generation");
        }
        return ok;
    }

    private static string ExtractFirstCsvField(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            return comma >= 0 ? line[..comma] : line;
        }

        var sb = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }
                break;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }


    private void AuditManagedWakeRuntimeState(HashSet<string> existing, HashSet<string> desiredNames, DateTime now, bool force)
    {
        if (existing.Count == 0)
            return;

        var utcNow = DateTime.UtcNow;
        if (!force && utcNow - _lastRuntimeAuditUtc < FullValidationInterval)
            return;

        _lastRuntimeAuditUtc = utcNow;

        foreach (var taskName in existing.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseManagedWakeTaskTime(taskName, out var scheduledAt))
                continue;

            var overdue = now - scheduledAt;
            if (overdue < WakeOverdueGrace)
                continue;

            var runtime = QueryTaskRuntimeState(taskName);
            var stillDesired = desiredNames.Contains(taskName);
            var looksNeverRun = runtime.LastResult.Contains("0x41303", StringComparison.OrdinalIgnoreCase)
                                || runtime.LastResult.Contains("まだ実行", StringComparison.OrdinalIgnoreCase)
                                || runtime.LastRun.Contains("99/11/30", StringComparison.OrdinalIgnoreCase)
                                || runtime.LastRun.Contains("1999", StringComparison.OrdinalIgnoreCase)
                                || runtime.LastRun.Equals("N/A", StringComparison.OrdinalIgnoreCase)
                                || runtime.LastRun.Equals("なし", StringComparison.OrdinalIgnoreCase);

            _log.Add("TaskScheduler", "WAKE_RUNTIME_AUDIT",
                $"task={taskName} scheduled={scheduledAt:MM/dd HH:mm:ss} overdueSec={(int)overdue.TotalSeconds} inDesired={stillDesired} status={runtime.Status} next={runtime.NextRun} lastRun={runtime.LastRun} lastResult={runtime.LastResult} suspectNeverRun={looksNeverRun} action={(stillDesired ? "keep" : "delete_if_unexpected")} rule=release_contract");
        }
    }

    private static bool TryParseManagedWakeTaskTime(string taskName, out DateTime scheduledAt)
    {
        scheduledAt = default;
        // WakeSlot名は TvAIr_WakeSlot_gXXXX_yyyyMMdd_HHmmss_KIND_Rid のため、
        // 末尾一致ではなく時刻直後の区切りを見て抽出する。
        var match = Regex.Match(taskName, @"_(\d{8})_(\d{6})(?:_|$)");
        if (!match.Success)
            return false;

        var value = match.Groups[1].Value + match.Groups[2].Value;
        return DateTime.TryParseExact(
            value,
            "yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out scheduledAt);
    }

    private WakeTaskRuntimeState QueryTaskRuntimeState(string taskName)
    {
        var result = RunSchtasks($"/Query /TN \"{taskName}\" /V /FO CSV");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return new WakeTaskRuntimeState("(query failed)", "(query failed)", "(query failed)", CompactOneLine(result.Output));

        var lines = result.Output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count < 2)
            return new WakeTaskRuntimeState("(missing)", "(missing)", "(missing)", CompactOneLine(result.Output));

        var headers = SplitCsvLine(lines[0]);
        var values = SplitCsvLine(lines[^1]);
        string Pick(params string[] patterns)
        {
            for (var i = 0; i < headers.Count && i < values.Count; i++)
            {
                var h = headers[i];
                if (patterns.Any(p => h.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    return string.IsNullOrWhiteSpace(values[i]) ? "(empty)" : CompactOneLine(values[i]);
            }
            return "(missing)";
        }

        var status = Pick("Status", "状態");
        var nextRun = Pick("Next Run Time", "次回", "次の実行");
        var lastRun = Pick("Last Run Time", "前回の実行", "最終実行");
        var lastResult = Pick("Last Result", "前回の結果", "最終結果", "結果");
        if (lastResult == "(missing)")
        {
            var joined = CompactOneLine(string.Join(" | ", values));
            var m = Regex.Match(joined, @"0x[0-9A-Fa-f]{5,8}");
            lastResult = m.Success ? m.Value : joined;
        }

        return new WakeTaskRuntimeState(status, nextRun, lastRun, lastResult);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        if (line.Length == 0)
            return values;

        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }
        values.Add(sb.ToString().Trim());
        return values;
    }

    private static string CompactOneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "…";
    }

    private void DeleteAllManagedTasks()
    {
        foreach (var taskName in GetManagedTaskNames(ReadActiveWakeGenerationFallback()).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            DeleteTask(taskName);
    }

    private SchtasksResult DeleteTask(string taskName, bool suppressMaintenanceLog = false)
    {
        // release_contract:
        // ローカルWakeSlot削除は current_process のみを本線にする。
        // Delete失敗後の資格情報付きDelete/Disableは、/S localhost /U /P のローカル不可エラーを増幅するため通常同期では行わない。
        var args = $"/Delete /TN \"{taskName}\" /F";
        var result = RunSchtasksForLocalTaskMaintenance(args, "Delete", taskName, suppressMaintenanceLog);
        if (result.Success || LooksLikeTaskNotFound(result.Output))
            return result;

        return result;
    }

    private SchtasksResult DisableTaskWithWakeCredentials(string taskName, string operation)
    {
        var args = $"/Change /TN \"{taskName}\" /DISABLE";
        return RunSchtasksForLocalTaskMaintenance(args, operation, taskName);
    }

    private SchtasksResult RunSchtasksForLocalTaskMaintenance(string arguments, string operation, string taskName, bool suppressMaintenanceLog = false)
    {
        var userName = _ini.TaskUserName;
        var currentUser = GetCurrentUserNameForTask();
        var elevated = IsCurrentProcessElevated();
        var configured = string.IsNullOrWhiteSpace(userName) ? "(missing)" : "(configured)";

        // release_contract:
        // ローカルの /Delete /Change は現在プロセストークンだけで実行する。
        // /S localhost /U /P はローカルタスク削除の権限昇格ではなく、
        // 「ユーザー資格情報がローカル コンピューターでは使用できません」を誘発する別失敗経路なので使わない。
        var direct = RunSchtasks(arguments);
        if (direct.Success || LooksLikeTaskNotFound(direct.Output))
        {
            if (!suppressMaintenanceLog)
            {
                _log.Add("TaskScheduler", "WAKE_TASK_MAINTENANCE",
                    $"class=diagnostic operation={operation} task={taskName} path=current_process currentProcessUser={CompactOneLine(currentUser)} isElevated={elevated} result=OK configuredUser={configured} configuredCredentialFallback=SKIPPED_LOCAL_TASK rule=viewer_control_directcontent_injection_audit");
            }
            return direct;
        }

        if (!suppressMaintenanceLog)
        {
            _log.Add("TaskScheduler", "WAKE_TASK_MAINTENANCE",
                $"class=diagnostic operation={operation} task={taskName} path=current_process currentProcessUser={CompactOneLine(currentUser)} isElevated={elevated} result=NG error={CompactOneLine(direct.Output)} configuredUser={configured} configuredCredentialFallback=SKIPPED_LOCAL_TASK rule=viewer_control_directcontent_injection_audit");
        }
        return direct;
    }

    private static string AddLocalCredentialSwitches(string arguments, string userName, string password)
    {
        // schtasks の /S /U /P は共通オプションとして Delete/Change/Query/Create に渡せる。
        // 既存コマンドのサブコマンド直後に挿入し、/TN 以降の意味を変えない。
        var trimmed = arguments.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
            return $"{trimmed} /S localhost /U \"{userName}\" /P \"{password}\"";

        var head = trimmed[..firstSpace];
        var rest = trimmed[(firstSpace + 1)..];
        return $"{head} /S localhost /U \"{userName}\" /P \"{password}\" {rest}";
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void TryDeleteLegacyFixedTasks()
    {
        // release_contract:
        // 旧固定名Wakeタスクは、作成者/権限が現在のTvAIr実行コンテキストと食い違うと
        // Delete/Create がアクセス拒否になり、近接予約のWake登録を巻き込んで失敗させる。
        // 録画失敗回避を最優先し、ここでは削除を試みない。新規WakeSlot名前空間だけを使う。
        var activeGeneration = ReadActiveWakeGenerationFallback();
        var legacyDetected = RunSchtasks($"/Query /TN \"{LegacyWakeTaskName}\"").Success
            || RunSchtasks($"/Query /TN \"{LegacyWakeEpgTaskName}\"").Success
            || RunSchtasks($"/Query /TN \"{LegacyWakeRecTaskName}\"").Success;
        if (legacyDetected)
        {
            _log.Add("TaskScheduler", "WakeLegacy",
                $"legacyFixedDetected=True activeGeneration={activeGeneration} activePrefix={WakeSlotTaskPrefix}{activeGeneration}_ legacyPrefix=TvAIr_WakeSlot_yyyyMMdd reason=avoid_access_denied_blocking_recording rule=wake_legacy_fixed_task_guard");
        }
    }

    private ExistingTaskContractResult VerifyExistingTaskContract(WakeTaskSpec spec)
    {
        var result = RunSchtasks($"/Query /TN \"{spec.Name}\" /XML");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return new ExistingTaskContractResult(false, "query_failed");

        var xml = result.Output;
        var expectedExe = ResolveTvAIrWakeExecutablePath();
        var expectedArgs = BuildWakeTaskArguments(spec);
        var expectedWorkDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expectedStart = spec.When.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var command = ExtractXmlValue(xml, "Command") ?? string.Empty;
        var arguments = ExtractXmlValue(xml, "Arguments") ?? string.Empty;
        var workingDirectory = ExtractXmlValue(xml, "WorkingDirectory") ?? string.Empty;
        var startBoundary = ExtractXmlValue(xml, "StartBoundary") ?? string.Empty;
        var wakeToRun = ExtractXmlValue(xml, "WakeToRun") ?? string.Empty;
        var startWhenAvailable = ExtractXmlValue(xml, "StartWhenAvailable") ?? string.Empty;
        var executionTimeLimit = ExtractXmlValue(xml, "ExecutionTimeLimit") ?? string.Empty;
        var multipleInstancesPolicy = ExtractXmlValue(xml, "MultipleInstancesPolicy") ?? string.Empty;
        var logonType = ExtractXmlValue(xml, "LogonType") ?? string.Empty;
        var runLevel = ExtractXmlValue(xml, "RunLevel") ?? string.Empty;

        var reasons = new List<string>();
        if (!PathEquals(command, expectedExe))
            reasons.Add($"Command:{CompactOneLine(command)}!=expected");
        if (!string.Equals(arguments.Trim(), expectedArgs, StringComparison.Ordinal))
            reasons.Add("Arguments:mismatch");
        if (!DirectoryEquals(workingDirectory, expectedWorkDir))
            reasons.Add($"WorkingDirectory:{CompactOneLine(workingDirectory)}!=expected");
        if (!StartBoundaryEquals(startBoundary, spec.When))
            reasons.Add($"StartBoundary:{CompactOneLine(startBoundary)}!={expectedStart}");
        if (!string.Equals(wakeToRun, "true", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"WakeToRun:{ValueOrMissing(wakeToRun)}");
        if (!string.Equals(startWhenAvailable, "true", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"StartWhenAvailable:{ValueOrMissing(startWhenAvailable)}");
        if (!string.Equals(executionTimeLimit, "PT0S", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"ExecutionTimeLimit:{ValueOrMissing(executionTimeLimit)}");
        if (!string.Equals(multipleInstancesPolicy, "IgnoreNew", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"MultipleInstancesPolicy:{ValueOrMissing(multipleInstancesPolicy)}");
        if (!IsAcceptedWakeLogonType(logonType))
            reasons.Add($"LogonType:{ValueOrMissing(logonType)}");
        // schtasks /Query /XML may omit RunLevel even when LeastPrivilege was requested.
        // Treat a missing value as acceptable; only an explicit non-LeastPrivilege value is a contract mismatch.
        if (!string.IsNullOrWhiteSpace(runLevel)
            && !string.Equals(runLevel, "LeastPrivilege", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"RunLevel:{ValueOrMissing(runLevel)}");

        return reasons.Count == 0
            ? new ExistingTaskContractResult(true, "ok")
            : new ExistingTaskContractResult(false, string.Join(",", reasons));
    }


    private static bool IsAcceptedWakeLogonType(string? logonType)
        => string.Equals(logonType, "Password", StringComparison.OrdinalIgnoreCase)
           || string.Equals(logonType, "InteractiveToken", StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            return false;
        try
        {
            actual = Path.GetFullPath(Environment.ExpandEnvironmentVariables(actual.Trim().Trim('"')));
            expected = Path.GetFullPath(Environment.ExpandEnvironmentVariables(expected.Trim().Trim('"')));
        }
        catch
        {
            actual = actual.Trim().Trim('"');
            expected = expected.Trim().Trim('"');
        }
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DirectoryEquals(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            return false;
        return PathEquals(
            actual.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            expected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static bool StartBoundaryEquals(string actual, DateTime expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        if (DateTime.TryParse(actual, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            return Math.Abs((parsed - expected).TotalSeconds) < 1;

        var expectedText = expected.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(actual.Trim(), expectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValueOrMissing(string value)
        => string.IsNullOrWhiteSpace(value) ? "(missing)" : CompactOneLine(value);

    private TaskVerificationResult VerifyTask(string taskName)
    {
        var result = RunSchtasks($"/Query /TN \"{taskName}\" /XML");
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return new TaskVerificationResult(
                QuerySucceeded: false,
                WakeToRun: false,
                LogonType: "(query failed)",
                RunLevel: "(query failed)",
                StartWhenAvailable: false);
        }

        var xml = result.Output;
        var wakeToRun = xml.Contains("<WakeToRun>true</WakeToRun>", StringComparison.OrdinalIgnoreCase);
        var logonType = ExtractXmlValue(xml, "LogonType") ?? "(missing)";
        var runLevel  = ExtractXmlValue(xml, "RunLevel") ?? "(missing)";
        var startWhenAvailable = xml.Contains("<StartWhenAvailable>true</StartWhenAvailable>", StringComparison.OrdinalIgnoreCase);

        return new TaskVerificationResult(true, wakeToRun, logonType, runLevel, startWhenAvailable);
    }

    private static string? ExtractXmlValue(string xml, string elementName)
    {
        var match = Regex.Match(xml, $@"<{elementName}>(.*?)</{elementName}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static SchtasksResult RunSchtasks(string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return new SchtasksResult(false, "Process.Start returned null.");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(15_000);

            var success = proc.ExitCode == 0;
            var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return new SchtasksResult(success, output.Trim());
        }
        catch (Exception ex)
        {
            return new SchtasksResult(false, ex.Message);
        }
    }

    private sealed record WakeCoverageItem(
        string Purpose,
        DateTime When,
        Reservation Reservation,
        string SourceLabel,
        string Route,
        int? ChainRoot,
        int? ChainPrev);

    private sealed record WakeCoverageSessionGroup(
        DateTime WakeAt,
        List<WakeCoverageItem> Items);

    private sealed record WakeTaskSpec(
        string Name,
        string Kind,
        DateTime When,
        string TunerName,
        Reservation Reservation,
        int CoverageCount = 1);

    private sealed record WakeTaskApplyResult(WakeTaskSpec Spec, bool Success);

    private sealed record SchtasksResult(bool Success, string Output);

    private sealed record TaskVerificationResult(
        bool QuerySucceeded,
        bool WakeToRun,
        string LogonType,
        string RunLevel,
        bool StartWhenAvailable);

    private sealed record ExistingTaskContractResult(bool Matches, string Reason);

    private sealed record WakeTaskRuntimeState(
        string Status,
        string NextRun,
        string LastRun,
        string LastResult);
}

/// <summary>プラグイン/読み取りAPI用のWake計画項目。</summary>
public sealed record WakeTaskPlanItem(
    DateTime At,
    string Kind,
    int? ReservationId,
    string Title,
    string TaskName);

/// <summary>次回Wakeタスク情報(API応答用)</summary>
public sealed record WakeTaskInfo(
    DateTime WakeAt,
    int      ReservationId,
    string   Title,
    DateTime StartTime,
    int      WakeMinutesBefore);
