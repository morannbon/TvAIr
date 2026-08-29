using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// チューナーリソースの一元管理。
///
/// 管理単位 : TunerProfile 1本 = 物理チューナー1本。
/// 占有種別 : EPG / Recording / Viewing の3種。
///
/// ・Recording / EPG の実行可否は各owner側の契約で決定し、TunerPool自身は実行中ownerを用途都合でpreemptしない。
/// ・Viewing Roleは視聴専用で、Recording / EPGから奪わない。
/// ・各用途は割り当てられたRoleの物理Tunerだけを使用する。
/// </summary>
public sealed class TunerPool : IDisposable
{
    // ─── 内部スロット ────────────────────────────────────────────

    internal sealed class Slot
    {
        public string Name              { get; }   // チューナー表示名（TunerProfile.Name）
        public string BonDriverFileName { get; }
        public string Did               { get; }   // 物理チューナー識別子 (A/B/C…)
        public string Group             { get; }
        public string Role              { get; }
        public string LogicalViewerSlotId { get; }
        public int    SlotIndex         { get; }

        public TunerUsageKind UsageKind    { get; private set; } = TunerUsageKind.Free;
        public int?           ReservationId { get; private set; }
        public int?           ProcessId     { get; private set; }
        public DateTime?      PlannedEndTime{ get; private set; }
        public Guid?          PoolLeaseId { get; private set; }
        public long           OccupancyGeneration { get; private set; }
        public TunerLeaseState LeaseState { get; private set; } = TunerLeaseState.Free;
        /// <summary>直近の Release が実行された時刻。Acquire 時のクールダウン計算用。</summary>
        public DateTime?      LastReleasedAt{ get; private set; }
        public bool IsFree => UsageKind == TunerUsageKind.Free;
        // release_contract: 視聴不可侵判定は設定Roleを正とする。
        // BonDriver名だけでは判定しない。
        public bool IsViewingReserved => string.Equals(Role, "Viewing", StringComparison.OrdinalIgnoreCase);

        public Slot(string name, string bonDriverFileName, string did, string group, string role, string logicalViewerSlotId, int slotIndex)
        {
            Name              = name;
            BonDriverFileName = bonDriverFileName;
            Did               = did;
            Group             = group;
            Role              = IniSettingsService.NormalizeTunerRole(role);
            LogicalViewerSlotId = (logicalViewerSlotId ?? string.Empty).Trim();
            SlotIndex         = slotIndex;
        }

        public TunerLeaseIdentity Occupy(TunerUsageKind kind, int? reservationId, int? processId, DateTime? plannedEndTime)
        {
            OccupancyGeneration++;
            PoolLeaseId = Guid.NewGuid();
            LeaseState = TunerLeaseState.Active;
            UsageKind      = kind;
            ReservationId  = reservationId;
            ProcessId      = processId;
            PlannedEndTime = plannedEndTime;
            return new TunerLeaseIdentity(PoolLeaseId.Value, OccupancyGeneration);
        }

        public bool Matches(TunerLeaseIdentity identity)
            => LeaseState == TunerLeaseState.Active
               && PoolLeaseId == identity.PoolLeaseId
               && OccupancyGeneration == identity.OccupancyGeneration;

        public void SetProcessId(int pid) => ProcessId = pid;

        public void UpdatePlannedEndTime(DateTime plannedEndTime)
        {
            PlannedEndTime = plannedEndTime;
        }

        public void Release(bool revoked = false)
        {
            UsageKind      = TunerUsageKind.Free;
            ReservationId  = null;
            ProcessId      = null;
            PlannedEndTime = null;
            PoolLeaseId = null;
            LeaseState = revoked ? TunerLeaseState.Revoked : TunerLeaseState.Free;
            LastReleasedAt = DateTime.Now;
        }

        /// <summary>このスロットのTVTest起動用BonDriver引数 (/DIDを含む)</summary>
        public string ToBonDriverArg()
            => string.IsNullOrWhiteSpace(Did) ? BonDriverFileName : $"{BonDriverFileName} /DID {Did}";
    }

    // ─── フィールド ──────────────────────────────────────────────

    private readonly List<Slot> _slots = new();
    private readonly LogRepository _log;
    private readonly IniSettingsService _ini;
    private readonly object _gate = new();
    private long _snapshotVersion;
    private TunerPoolLifecycleState _lifecycleState = TunerPoolLifecycleState.Running;
    private TaskCompletionSource<long> _stateChanged = CreateStateChangeSignal();

    private static TaskCompletionSource<long> CreateStateChangeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PublishStateChangeUnsafe()
    {
        _snapshotVersion++;
        var completed = _stateChanged;
        _stateChanged = CreateStateChangeSignal();
        completed.TrySetResult(_snapshotVersion);
    }

    private bool CanAcquireUnsafe(string operation, out string? rejectionLog)
    {
        if (_lifecycleState == TunerPoolLifecycleState.Running)
        {
            rejectionLog = null;
            return true;
        }

        rejectionLog =
            $"result=POOL_NOT_RUNNING operation={operation} lifecycle={_lifecycleState} version={_snapshotVersion}";
        return false;
    }

    internal bool IsLeaseCurrent(Slot slot, TunerLeaseIdentity identity)
    {
        lock (_gate)
            return _lifecycleState == TunerPoolLifecycleState.Running && slot.Matches(identity);
    }

    // LEASE_IDENTITY_OBSERVATION_INVARIANT:
    // Dispose済みか、Pool lifecycleがRunningかとは独立して、同じlease identityが
    // スロット正本へ残っているかを確認する。cleanup完了・DID再利用可否はこの判定を使う。
    internal bool IsLeaseIdentityCurrent(Slot slot, TunerLeaseIdentity identity)
    {
        lock (_gate)
            return slot.Matches(identity);
    }

    private string BuildStaleLeaseLogUnsafe(Slot slot, TunerLeaseIdentity identity, string operation)
        => $"operation={operation} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
           $"expectedLeaseId={identity.PoolLeaseId} expectedGeneration={identity.OccupancyGeneration} " +
           $"actualLeaseId={(slot.PoolLeaseId.HasValue ? slot.PoolLeaseId.Value.ToString() : "-")} actualGeneration={slot.OccupancyGeneration} " +
           $"leaseState={slot.LeaseState} usage={slot.UsageKind} reservationId={(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} version={_snapshotVersion}";

    public TunerPool(
        IReadOnlyList<TunerProfile> profiles,
        IniSettingsService ini,
        LogRepository log)
    {
        _ini = ini;
        _log = log;

        var effectiveProfiles = TunerRuntimeProfileSource.Build(ini, profiles);
        var idx = 0;
        var rejected = 0;
        foreach (var p in effectiveProfiles)
        {
            var normalizedGroup = TunerDisplayName.NormalizeGroup(p.Group);
            var normalizedRole = IniSettingsService.NormalizeTunerRole(p.Role);
            var normalizedBonDriver = TunerIsolationPolicy.NormalizeBonDriverForRole(p.BonDriverFileName, normalizedGroup, normalizedRole);
            if (!TunerDisplayName.IsKnownGroup(normalizedGroup) || !IniSettingsService.IsKnownTunerRole(normalizedRole) || string.IsNullOrWhiteSpace(normalizedBonDriver))
            {
                rejected++;
                continue;
            }
            _slots.Add(new Slot(p.Name, normalizedBonDriver, p.Did, normalizedGroup, normalizedRole, p.LogicalViewerSlotId, idx++));
        }

        _log.Add("TunerPool", "Init",
            $"スロット初期化: {_slots.Count}本 rejected={rejected} policy=settings_to_logical_resource / " +
            string.Join(", ", _slots.Select(p =>
                string.IsNullOrWhiteSpace(p.Did)
                    ? $"{p.BonDriverFileName}/{p.Role}"
                    : $"{p.BonDriverFileName}/DID {p.Did}/{p.Role}")));
        _log.Add("TUNER_MAP", "RoleBinding",
            string.Join(" | ", _slots.Select(p =>
                $"slot={p.SlotIndex} name={p.Name} DID={p.Did} group={p.Group} role={p.Role} bonDriver={p.BonDriverFileName}")));

    }

    // ─── 公開 API ────────────────────────────────────────────────

    /// <summary>
    /// 録画用にチューナーを確保する。
    /// 同一グループの EPG スロットを先に強制解放してから空きを探す。
    /// 空きがなければ null を返す。
    /// 空きが複数ある場合は「LastReleasedAt が最も古いスロット」を優先することで、
    /// 直近まで使われていた物理チューナーの連続Open/Closeを避ける。
    /// </summary>
    public TunerLease? AcquireForRecording(
        string group, int reservationId, DateTime plannedEndTime)
    {
        TunerLease? lease = null;
        string? resultLog = null;
        string? traceEnter = null;
        string? traceExit = null;
        string? rejectionLog = null;

        lock (_gate)
        {
            if (!CanAcquireUnsafe("acquire_recording", out rejectionLog))
            {
                // ログはPoolロック外で出す。
            }
            else
            {

            traceEnter = $"[TUNER] stage=acquire_recording_enter group={group} reservationId={reservationId} plannedEnd={plannedEndTime:MM/dd HH:mm:ss} status={GetStatusSummaryUnsafe()}";
            var slot = FindFree(group);
            if (slot is null)
            {
                traceExit = $"[TUNER] stage=acquire_recording_fail group={group} reservationId={reservationId} reason=no_free_slot status={GetStatusSummaryUnsafe()}";
            }
            else
            {
                var elapsedMs = GetElapsedSinceReleaseMs(slot);
                var leaseIdentity = slot.Occupy(TunerUsageKind.Recording, reservationId, null, plannedEndTime);
                PublishStateChangeUnsafe();
                resultLog =
                    $"Recording 確保: slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                    $"reservationId={reservationId} elapsedSinceRelease={FormatElapsed(elapsedMs)}";
                traceExit =
                    $"[TUNER] stage=acquire_recording_ok group={group} reservationId={reservationId} slot={slot.SlotIndex} " +
                    $"name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)} status={GetStatusSummaryUnsafe()}";
                lease = new TunerLease(slot, this, elapsedMs, leaseIdentity);
            }
            }
        }

        if (rejectionLog is not null) _log.Add("TUNER_LEASE_REJECTED", group, rejectionLog);
        if (traceEnter is not null) _log.Add("TUNER_TRACE", group, traceEnter);
        if (resultLog is not null) _log.Add("TunerPool", group, resultLog);
        if (traceExit is not null) _log.Add("TUNER_TRACE", group, traceExit);
        return lease;
    }

    /// <summary>
    /// 指定チューナー名のスロットを優先して録画用に確保する。
    /// 該当スロットが空きでなければ null を返す（呼び出し元でフォールバック）。
    /// </summary>
    public TunerLease? AcquireForRecordingByName(
        string tunerName, int reservationId, DateTime plannedEndTime)
    {
        TunerLease? lease = null;
        string? group = null;
        string? resultLog = null;
        string? traceEnter = null;
        string? traceExit = null;
        string? rejectionLog = null;

        lock (_gate)
        {
            if (!CanAcquireUnsafe("acquire_recording_by_name", out rejectionLog))
            {
                // ログはPoolロック外で出す。
            }
            else
            {

            traceEnter = $"[TUNER] stage=acquire_recording_by_name_enter tuner={tunerName} reservationId={reservationId} plannedEnd={plannedEndTime:MM/dd HH:mm:ss} status={GetStatusSummaryUnsafe()}";
            var slot = _slots.FirstOrDefault(
                candidate => string.Equals(candidate.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                    && candidate.IsFree
                    && !candidate.IsViewingReserved);
            if (slot is null)
            {
                traceExit = $"[TUNER] stage=acquire_recording_by_name_fail tuner={tunerName} reservationId={reservationId} reason=not_free_or_viewing_reserved status={GetStatusSummaryUnsafe()}";
            }
            else
            {
                group = slot.Group;
                var elapsedMs = GetElapsedSinceReleaseMs(slot);
                var leaseIdentity = slot.Occupy(TunerUsageKind.Recording, reservationId, null, plannedEndTime);
                PublishStateChangeUnsafe();
                resultLog =
                    $"Recording 確保(指定): slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                    $"reservationId={reservationId} elapsedSinceRelease={FormatElapsed(elapsedMs)}";
                traceExit =
                    $"[TUNER] stage=acquire_recording_by_name_ok tuner={tunerName} reservationId={reservationId} slot={slot.SlotIndex} " +
                    $"name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)} status={GetStatusSummaryUnsafe()}";
                lease = new TunerLease(slot, this, elapsedMs, leaseIdentity);
            }
            }
        }

        if (rejectionLog is not null) _log.Add("TUNER_LEASE_REJECTED", tunerName, rejectionLog);
        if (traceEnter is not null) _log.Add("TUNER_TRACE", tunerName, traceEnter);
        if (resultLog is not null) _log.Add("TunerPool", group ?? tunerName, resultLog);
        if (traceExit is not null) _log.Add("TUNER_TRACE", group ?? tunerName, traceExit);
        return lease;
    }

    /// <summary>
    /// TvAIr本体再起動後も生存している録画workerを、既存の物理チューナー占有として再Attachする。
    /// 新規Acquireとは異なり、worker jobから検証済みのTuner名/DID/BonDriver/PIDを完全一致で要求する。
    /// </summary>
    public TunerLease? AttachExistingRecording(
        string tunerName,
        string did,
        string bonDriverFileName,
        int reservationId,
        int processId,
        DateTime plannedEndTime)
    {
        TunerLease? lease = null;
        string logTitle = tunerName;
        string? logMessage = null;
        string? rejectionLog = null;

        lock (_gate)
        {
            if (!CanAcquireUnsafe("attach_existing_recording", out rejectionLog))
            {
                // ログはPoolロック外で出す。
            }
            else
            {

            var normalizedBon = Path.GetFileName(bonDriverFileName ?? string.Empty);
            var slot = _slots.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals((candidate.Did ?? string.Empty).Trim(), (did ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(candidate.BonDriverFileName), normalizedBon, StringComparison.OrdinalIgnoreCase));
            if (slot is null)
            {
                logMessage =
                    $"result=REJECTED reason=physical_identity_not_found reservationId={reservationId} pid={processId} did={did} bonDriver={normalizedBon}";
            }
            else if (!slot.IsFree)
            {
                logTitle = slot.Group;
                logMessage =
                    $"result=REJECTED reason=slot_not_free slot={slot.SlotIndex} name={slot.Name} reservationId={reservationId} pid={processId} " +
                    $"currentUsage={slot.UsageKind} currentReservationId={(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} " +
                    $"currentPid={(slot.ProcessId.HasValue ? slot.ProcessId.Value.ToString() : "-")}";
            }
            else
            {
                logTitle = slot.Group;
                var identity = slot.Occupy(TunerUsageKind.Recording, reservationId, processId, plannedEndTime);
                PublishStateChangeUnsafe();
                logMessage =
                    $"result=ATTACHED slot={slot.SlotIndex} name={slot.Name} did={slot.Did} bonDriver={slot.BonDriverFileName} " +
                    $"reservationId={reservationId} pid={processId} poolLeaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} " +
                    $"plannedEnd={plannedEndTime:MM/dd HH:mm:ss}";
                lease = new TunerLease(slot, this, GetElapsedSinceReleaseMs(slot), identity);
            }
            }
        }

        if (rejectionLog is not null) _log.Add("TUNER_LEASE_REJECTED", tunerName, rejectionLog);
        if (logMessage is not null) _log.Add("TUNER_ATTACH_EXISTING", logTitle, logMessage);
        return lease;
    }

    /// <summary>
    /// 呼び出し側が実行時に安全性を確認して選んだ物理Tuner名でEPG leaseを確保する。
    /// 録画前EPG確認ではプリチューンを意味せず、本録画予定Tunerとの一致も要求しない。
    /// </summary>
    public TunerLease? AcquireForEpgByName(
        string tunerName,
        string group,
        DateTime plannedEndTime,
        string reason = "epg_runtime_selected_tuner")
    {
        TunerLease? lease = null;
        string? resultLog = null;
        string? traceEnter = null;
        string? traceExit = null;
        string? rejectionLog = null;

        lock (_gate)
        {
            if (!CanAcquireUnsafe("acquire_epg_by_name", out rejectionLog))
            {
                // ログはPoolロック外で出す。
            }
            else
            {

            traceEnter = $"[TUNER] stage=acquire_epg_by_name_enter tuner={tunerName} group={group} plannedEnd={plannedEndTime:MM/dd HH:mm:ss} reason={reason} status={GetStatusSummaryUnsafe()}";
            var slot = _slots.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                && MatchGroup(candidate, group)
                && candidate.IsFree
                && !candidate.IsViewingReserved);

            if (slot is null)
            {
                traceExit = $"[TUNER] stage=acquire_epg_by_name_fail tuner={tunerName} group={group} reason=not_free_or_viewing_reserved status={GetStatusSummaryUnsafe()}";
            }
            else
            {
                var elapsedMs = GetElapsedSinceReleaseMs(slot);
                var leaseIdentity = slot.Occupy(TunerUsageKind.Epg, null, null, plannedEndTime);
                PublishStateChangeUnsafe();
                resultLog =
                    $"EPG 確保(実行時Tuner指定): slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                    $"requested={tunerName} elapsedSinceRelease={FormatElapsed(elapsedMs)} reason={reason}";
                traceExit =
                    $"[TUNER] stage=acquire_epg_by_name_ok tuner={tunerName} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                    $"elapsedSinceRelease={FormatElapsed(elapsedMs)} reason={reason} status={GetStatusSummaryUnsafe()}";
                lease = new TunerLease(slot, this, elapsedMs, leaseIdentity);
            }
            }
        }

        if (rejectionLog is not null) _log.Add("TUNER_LEASE_REJECTED", group, rejectionLog);
        if (traceEnter is not null) _log.Add("TUNER_TRACE", tunerName, traceEnter);
        if (resultLog is not null) _log.Add("TunerPool", group, resultLog);
        if (traceExit is not null) _log.Add("TUNER_TRACE", group, traceExit);
        return lease;
    }

    /// <summary>
    /// EPG 用にチューナーを確保する。空きがなければ null を返す。
    /// 候補はTvAIr自身のTunerPool状態だけから決定する。
    /// </summary>
    public TunerLease? AcquireForEpg(
        string group,
        DateTime plannedEndTime)
    {
        TunerLease? lease = null;
        string? resultLog = null;
        string? rejectionLog = null;

        lock (_gate)
        {
            if (!CanAcquireUnsafe("acquire_epg", out rejectionLog))
            {
                // ログはPoolロック外で出す。
            }
            else
            {

            var slot = FindFreeForEpg(group);
            if (slot is not null)
            {
                var elapsedMs = GetElapsedSinceReleaseMs(slot);
                var leaseIdentity = slot.Occupy(TunerUsageKind.Epg, null, null, plannedEndTime);
                PublishStateChangeUnsafe();
                resultLog =
                    $"EPG 確保: slot={slot.SlotIndex} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)}";
                lease = new TunerLease(slot, this, elapsedMs, leaseIdentity);
            }
            }
        }

        if (rejectionLog is not null) _log.Add("TUNER_LEASE_REJECTED", group, rejectionLog);
        if (resultLog is not null) _log.Add("TunerPool", group, resultLog);
        return lease;
    }

    /// <summary>
    /// EPG用の空きスロット検索。TvAIr自身のTunerPoolだけを正本にする。
    /// </summary>
    private Slot? FindFreeForEpg(string group)
    {
        return _slots
            .Where(s => MatchGroup(s, group) && s.IsFree && !s.IsViewingReserved && !string.IsNullOrWhiteSpace(s.BonDriverFileName))
            .OrderBy(s => s.LastReleasedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// 視聴用にチューナーを確保する。空きがなければ null を返す。
    /// </summary>
    public TunerLease? AcquireForViewing(string logicalViewerSlotId, string group, int? processId = null)
    {
        if (string.IsNullOrWhiteSpace(logicalViewerSlotId)) return null;

        TunerLease? lease = null;
        string? logMessage = null;
        string? rejectionLog = null;
        var normalizedLogicalId = logicalViewerSlotId.Trim();

        lock (_gate)
        {
            if (!CanAcquireUnsafe("acquire_viewing", out rejectionLog))
            {
                // ログはPoolロック外で出す。
            }
            else
            {

            var slot = _slots.FirstOrDefault(candidate =>
                candidate.IsViewingReserved
                && string.Equals(candidate.LogicalViewerSlotId, normalizedLogicalId, StringComparison.OrdinalIgnoreCase));

            if (slot is null)
            {
                logMessage =
                    $"Viewing 確保失敗: logicalViewerSlotId={normalizedLogicalId} reason=viewer_slot_not_found pid={processId} rule=release_contract";
            }
            else if (!MatchGroup(slot, group))
            {
                logMessage =
                    $"Viewing 確保失敗: logicalViewerSlotId={normalizedLogicalId} slot={slot.SlotIndex} did={slot.Did} " +
                    $"reason=viewer_slot_group_mismatch slotGroup={slot.Group} pid={processId} rule=release_contract";
            }
            else if (!slot.IsFree)
            {
                logMessage =
                    $"Viewing 確保失敗: logicalViewerSlotId={normalizedLogicalId} slot={slot.SlotIndex} did={slot.Did} " +
                    $"reason=viewer_slot_unavailable usage={slot.UsageKind} pid={processId} rule=release_contract";
            }
            else
            {
                var elapsedMs = GetElapsedSinceReleaseMs(slot);
                var leaseIdentity = slot.Occupy(TunerUsageKind.Viewing, null, processId, null);
                PublishStateChangeUnsafe();
                logMessage =
                    $"Viewing 確保: slot={slot.SlotIndex} did={slot.Did} pid={processId} logicalViewerSlotId={normalizedLogicalId} " +
                    $"elapsedSinceRelease={FormatElapsed(elapsedMs)} rule=release_contract";
                lease = new TunerLease(slot, this, elapsedMs, leaseIdentity);
            }
            }
        }

        if (rejectionLog is not null) _log.Add("TUNER_LEASE_REJECTED", group, rejectionLog);
        if (logMessage is not null) _log.Add("TunerPool", group, logMessage);
        return lease;
    }

    private static string TrimForLog(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= max ? normalized : normalized[..max] + "…";
    }

    public int CountEpgSlots(string group)
    {
        lock (_gate)
            return _slots.Count(s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Epg);
    }

    public int CountEpgSlotsByName(string tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return 0;
        lock (_gate)
            return _slots.Count(s => string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                && s.UsageKind == TunerUsageKind.Epg);
    }


    /// <summary>
    /// EpgCapture owner が存在しないことを確認した後だけ使う、PID未反映EPG leaseの世代一致回収。
    /// TunerNameだけで解放せず、PoolLeaseId + OccupancyGeneration が現在のslot正本と一致する場合に限る。
    /// これにより、確認後に同じ物理Tunerへ別世代EPG ownerが入っても新leaseを誤解放しない。
    /// </summary>
    public bool ForceReleaseOwnerlessPidlessEpgLease(
        string tunerName,
        Guid poolLeaseId,
        long occupancyGeneration,
        string owner,
        string label)
    {
        if (string.IsNullOrWhiteSpace(tunerName) || poolLeaseId == Guid.Empty || occupancyGeneration <= 0)
            return false;

        bool released = false;
        string logMessage;
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                && candidate.UsageKind == TunerUsageKind.Epg
                && !candidate.ProcessId.HasValue
                && candidate.PoolLeaseId == poolLeaseId
                && candidate.OccupancyGeneration == occupancyGeneration
                && candidate.LeaseState == TunerLeaseState.Active);

            if (slot is null)
            {
                logMessage =
                    $"result=MISS tuner={SafeValue(tunerName)} expectedLeaseId={poolLeaseId} expectedGeneration={occupancyGeneration} " +
                    $"owner={SafeValue(owner)} label={SafeValue(label)} status={GetStatusSummaryUnsafe()} rule=epg_owner_identity_contract";
            }
            else
            {
                var before = $"#{slot.SlotIndex}:{slot.Name}/{slot.Did}/pid=-/L{poolLeaseId:N}/G{occupancyGeneration}";
                slot.Release(revoked: true);
                PublishStateChangeUnsafe();
                released = true;
                logMessage =
                    $"result=OK_OWNERLESS_PIDLESS released={before} owner={SafeValue(owner)} label={SafeValue(label)} " +
                    $"version={_snapshotVersion} status={GetStatusSummaryUnsafe()} rule=epg_owner_identity_contract";
            }
        }

        _log.Add("EPG_TUNER_STALE_LEASE_RELEASE", tunerName, logMessage);
        return released;
    }

    public int ForceReleasePidlessEpgSlots(string group, string owner, string label)
    {
        int releasedCount = 0;
        string? logMessage = null;

        lock (_gate)
        {
            var targets = _slots
                .Where(slot => MatchGroup(slot, group)
                    && slot.UsageKind == TunerUsageKind.Epg
                    && !slot.ProcessId.HasValue)
                .OrderBy(slot => slot.SlotIndex)
                .ToList();
            if (targets.Count > 0)
            {
                var released = string.Join(",", targets.Select(slot => $"#{slot.SlotIndex}:{slot.Name}/{slot.Did}/pid=-"));
                foreach (var slot in targets)
                {
                    slot.Release(revoked: true);
                    PublishStateChangeUnsafe();
                }

                releasedCount = targets.Count;
                logMessage =
                    $"result=OK_PIDLESS count={targets.Count} released={released} owner={SafeValue(owner)} label={SafeValue(label)} " +
                    $"version={_snapshotVersion} status={GetStatusSummaryUnsafe()} rule=release_contract";
            }
        }

        if (logMessage is not null) _log.Add("REC_TUNER_FORCE_RELEASE", group, logMessage);
        return releasedCount;
    }

    public bool HasFreeSlot(string group)
    {
        lock (_gate)
            return FindFree(group) is not null;
    }

    /// <summary>指定チューナー名のスロットが空いているか。チェーン予約の同一チューナー継承確認用。</summary>
    public bool HasFreeSlotByName(string tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return false;
        lock (_gate)
        {
            return _slots.Any(s =>
                string.Equals(s.Name, tunerName.Trim(), StringComparison.OrdinalIgnoreCase)
                && s.IsFree
                && !s.IsViewingReserved);
        }
    }

    public bool IsViewingReservedTuner(string tunerName)
    {
        if (string.IsNullOrWhiteSpace(tunerName)) return false;
        lock (_gate)
            return _slots.Any(s => string.Equals(s.Name, tunerName.Trim(), StringComparison.OrdinalIgnoreCase) && s.IsViewingReserved);
    }

    public int CountRecordableFreeSlots(string group)
    {
        lock (_gate)
            return _slots.Count(s => MatchGroup(s, group) && s.IsFree && !s.IsViewingReserved && !string.IsNullOrWhiteSpace(s.BonDriverFileName));
    }

    public int CountEpgUsableFreeSlots(string group)
    {
        lock (_gate)
            return _slots.Count(s => MatchGroup(s, group) && s.IsFree && !s.IsViewingReserved && !string.IsNullOrWhiteSpace(s.BonDriverFileName));
    }


    /// <summary>
    /// チューナー設定正本とTunerPool実体の投影差分を診断する。
    /// 実行挙動は一切変えず、Wake/EPG/Plugin(viewer profile)が同じ前提を見ているかを確認するためのログ用。
    /// </summary>
    public string BuildProjectionDiagnosticSummary(string requestedGroup)
    {
        lock (_gate)
        {
            var group = NormalizeViewingGroup(requestedGroup);
            bool Supports(Slot s) => MatchGroup(s, group);
            var matching = _slots.Where(Supports).OrderBy(s => s.SlotIndex).ToList();
            var exact = _slots.Where(s => NormalizeViewingGroup(s.Group) == group).OrderBy(s => s.SlotIndex).ToList();
            var hybrid = _slots.Where(s => NormalizeViewingGroup(s.Group) == "HYBRID" && Supports(s)).OrderBy(s => s.SlotIndex).ToList();
            var recordable = matching.Where(s => !s.IsViewingReserved).OrderBy(s => s.SlotIndex).ToList();
            var recordableFree = recordable.Where(s => s.IsFree).OrderBy(s => s.SlotIndex).ToList();
            var viewingReserved = matching.Where(s => s.IsViewingReserved).OrderBy(s => s.SlotIndex).ToList();
            var busy = matching.Where(s => !s.IsFree).OrderBy(s => s.SlotIndex).ToList();
            var unsupported = _slots.Where(s => !Supports(s)).OrderBy(s => s.SlotIndex).ToList();

            static string ListSlots(IEnumerable<Slot> slots) => string.Join(",", slots.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/{s.Group}/{s.Role}/{s.UsageKind}"));

            return $"requestedGroup={group} totalSlots={_slots.Count} matching={matching.Count} exactGroup={exact.Count} hybridMatching={hybrid.Count} " +
                   $"recordableMatching={recordable.Count} recordableFree={recordableFree.Count} viewingReservedMatching={viewingReserved.Count} busyMatching={busy.Count} unsupported={unsupported.Count} " +
                   $"matchingSlots=[{ListSlots(matching)}] recordableFreeSlots=[{ListSlots(recordableFree)}] viewingReservedSlots=[{ListSlots(viewingReserved)}] busySlots=[{ListSlots(busy)}] " +
                   $"rule=release_contract";
        }
    }

    /// <summary>
    /// Viewer profile/プラグイン投影との食い違いを確認するため、視聴用slotだけをログ用に要約する。
    /// </summary>
    public string BuildViewerProjectionDiagnosticSummary()
    {
        lock (_gate)
        {
            var viewing = _slots
                .Where(s => s.IsViewingReserved)
                .OrderBy(s => s.SlotIndex)
                .ToList();
            var groups = string.Join(",", viewing
                .Select(s => NormalizeViewingGroup(s.Group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var slots = string.Join(",", viewing.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/{s.Group}/{s.Role}/{s.UsageKind}"));
            return $"viewerReserved={viewing.Count} viewerGroups=[{groups}] viewerSlots=[{slots}] rule=release_contract";
        }
    }

    /// <summary>
    /// 指定グループのEPG用スロットが空くまで、TunerPoolの状態変更シグナルで待つ。
    /// TvAIr自身のTunerPool状態だけを正本にする。
    /// </summary>
    public async Task<bool> WaitForEpgSlotAsync(
        string group,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await WaitForAvailabilityAsync(() => FindFreeForEpg(group) is not null, timeout, cancellationToken).ConfigureAwait(false)
            == TunerAvailabilityWaitResult.Available;

    /// <summary>
    /// 指定グループのEPG leaseがすべて解放されるまで、TunerPoolの状態変更シグナルで待つ。
    /// 録画前プリエンプト後のlease収束確認用。timeoutは待機上限。
    /// </summary>
    public async Task<bool> WaitForEpgSlotsClearedAsync(string group, TimeSpan timeout, CancellationToken cancellationToken)
        => await WaitForAvailabilityAsync(
            () => !_slots.Any(s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Epg),
            timeout,
            cancellationToken).ConfigureAwait(false) == TunerAvailabilityWaitResult.Available;

    public async Task<bool> WaitForEpgSlotClearedByNameAsync(string tunerName, TimeSpan timeout, CancellationToken cancellationToken)
        => await WaitForAvailabilityAsync(
            () => !_slots.Any(s => string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                && s.UsageKind == TunerUsageKind.Epg),
            timeout,
            cancellationToken).ConfigureAwait(false) == TunerAvailabilityWaitResult.Available;

    /// <summary>
    /// 指定グループの録画用スロットが空くまで、TunerPoolの状態変更シグナルで待つ。
    /// timeoutは待機上限であり、固定ポーリング周期ではない。
    /// </summary>
    public async Task<bool> WaitForFreeSlotAsync(string group, TimeSpan timeout, CancellationToken cancellationToken)
        => await WaitForFreeSlotDetailedAsync(group, timeout, cancellationToken).ConfigureAwait(false) == TunerAvailabilityWaitResult.Available;

    public Task<TunerAvailabilityWaitResult> WaitForFreeSlotDetailedAsync(string group, TimeSpan timeout, CancellationToken cancellationToken)
        => WaitForAvailabilityAsync(() => FindFree(group) is not null, timeout, cancellationToken);

    /// <summary>
    /// 指定名の録画用スロットが空くまで、TunerPoolの状態変更シグナルで待つ。
    /// </summary>
    public async Task<bool> WaitForFreeSlotByNameAsync(string tunerName, TimeSpan timeout, CancellationToken cancellationToken)
        => await WaitForFreeSlotByNameDetailedAsync(tunerName, timeout, cancellationToken).ConfigureAwait(false) == TunerAvailabilityWaitResult.Available;

    public Task<TunerAvailabilityWaitResult> WaitForFreeSlotByNameDetailedAsync(string tunerName, TimeSpan timeout, CancellationToken cancellationToken)
        => WaitForAvailabilityAsync(
            () => _slots.Any(s => string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase) && s.IsFree && !s.IsViewingReserved),
            timeout,
            cancellationToken);

    private async Task<TunerAvailabilityWaitResult> WaitForAvailabilityAsync(Func<bool> isAvailableUnsafe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            lock (_gate)
            {
                if (_lifecycleState == TunerPoolLifecycleState.Quiescing) return TunerAvailabilityWaitResult.PoolStopping;
                if (_lifecycleState == TunerPoolLifecycleState.Disposed) return TunerAvailabilityWaitResult.PoolDisposed;
                return isAvailableUnsafe() ? TunerAvailabilityWaitResult.Available : TunerAvailabilityWaitResult.TimedOut;
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<long> stateChangedTask;
            lock (_gate)
            {
                if (_lifecycleState == TunerPoolLifecycleState.Quiescing) return TunerAvailabilityWaitResult.PoolStopping;
                if (_lifecycleState == TunerPoolLifecycleState.Disposed) return TunerAvailabilityWaitResult.PoolDisposed;
                if (isAvailableUnsafe()) return TunerAvailabilityWaitResult.Available;
                stateChangedTask = _stateChanged.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return TunerAvailabilityWaitResult.TimedOut;

            try
            {
                await stateChangedTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    if (_lifecycleState == TunerPoolLifecycleState.Quiescing) return TunerAvailabilityWaitResult.PoolStopping;
                    if (_lifecycleState == TunerPoolLifecycleState.Disposed) return TunerAvailabilityWaitResult.PoolDisposed;
                    return isAvailableUnsafe() ? TunerAvailabilityWaitResult.Available : TunerAvailabilityWaitResult.TimedOut;
                }
            }
        }
    }

    /// <summary>現在のスロット状態をログ出力しやすい1行文字列で返す。</summary>
    public string GetStatusSummary()
    {
        lock (_gate) return $"lifecycle={_lifecycleState} version={_snapshotVersion} " + GetStatusSummaryUnsafe();
    }

    /// <summary>視聴中スロットが存在するか（競合判定用）。</summary>
    public bool HasViewingSlot(string group)
    {
        lock (_gate)
            return _slots.Any(s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Viewing);
    }

    /// <summary>設定上の視聴専用チューナー本数を返す。</summary>
    public int GetViewingCapacity()
    {
        lock (_gate)
            return _slots.Count(s => s.IsViewingReserved && !string.IsNullOrWhiteSpace(s.BonDriverFileName));
    }

    /// <summary>現在Viewingで占有中の視聴専用チューナー本数を返す。</summary>
    public int GetActiveViewingCount()
    {
        lock (_gate)
            return _slots.Count(s => s.IsViewingReserved && s.UsageKind == TunerUsageKind.Viewing);
    }

    /// <summary>現在のスロット状態スナップショットを返す。</summary>
    public IReadOnlyList<TunerSlotStatus> GetStatus()
    {
        lock (_gate)
            return _slots.Select(ToStatusUnsafe).ToList();
    }

    public long SnapshotVersion
    {
        get
        {
            lock (_gate) return _snapshotVersion;
        }
    }

    public bool TryExecuteAtSnapshotVersion<T>(long expectedSnapshotVersion, Func<T> action, out T result)
    {
        lock (_gate)
        {
            if (_lifecycleState != TunerPoolLifecycleState.Running || _snapshotVersion != expectedSnapshotVersion)
            {
                result = default!;
                return false;
            }

            result = action();
            return true;
        }
    }

    /// <summary>指定放送波でEPG使用中の管理下スロットがあるかを返す。PreRecordEpgAdmissionの実状態正本。</summary>
    public bool HasActiveEpgInGroup(string group, out string summary)
    {
        lock (_gate)
        {
            var active = _slots
                .Where(s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Epg)
                .OrderBy(s => s.SlotIndex)
                .ToList();
            summary = active.Count == 0
                ? "-"
                : string.Join(",", active.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/pid={(s.ProcessId.HasValue ? s.ProcessId.Value.ToString() : "-")}/plannedEnd={(s.PlannedEndTime.HasValue ? s.PlannedEndTime.Value.ToString("MM/dd HH:mm:ss") : "-")}"));
            return active.Count > 0;
        }
    }

    public string GetActiveEpgGroupSummary(string group)
    {
        return HasActiveEpgInGroup(group, out var summary) ? summary : "-";
    }

    /// <summary>Viewing ロールとして予約されている DID 一覧。録画/EPG直前の保護監査ログ用。</summary>
    public IReadOnlyList<string> GetProtectedViewingDids()
    {
        lock (_gate)
            return _slots
                .Where(s => s.IsViewingReserved)
                .Select(s => s.Did)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>指定 DID が Viewing ロールかどうか。万一の設定/割当退化を録画開始直前に遮断する。</summary>
    public bool IsViewingReservedDid(string? did)
    {
        if (string.IsNullOrWhiteSpace(did)) return false;
        lock (_gate)
            return _slots.Any(s => string.Equals(s.Did, did.Trim(), StringComparison.OrdinalIgnoreCase) && s.IsViewingReserved);
    }

    // ─── TunerLease から呼ばれる ─────────────────────────────────

    internal void Release(Slot slot, TunerLeaseIdentity identity)
    {
        string? staleLog = null;
        string? enterLog = null;
        string? releasedLog = null;
        string? exitLog = null;
        string group = slot.Group;

        lock (_gate)
        {
            if (!slot.Matches(identity))
            {
                staleLog = BuildStaleLeaseLogUnsafe(slot, identity, "release");
            }
            else
            {
                var kind = slot.UsageKind;
                var rid = slot.ReservationId;
                var pid = slot.ProcessId;
                var beforeStatus = GetStatusSummaryUnsafe();
                enterLog = $"[TUNER] stage=release_enter slot={slot.SlotIndex} name={slot.Name} did={slot.Did} kind={kind} reservationId={(rid.HasValue ? rid.Value.ToString() : "-")} pid={(pid.HasValue ? pid.Value.ToString() : "-")} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} status={beforeStatus}";
                slot.Release();
                PublishStateChangeUnsafe();
                var version = _snapshotVersion;
                var afterStatus = GetStatusSummaryUnsafe();
                releasedLog = $"{kind} 解放: slot={slot.SlotIndex} name={slot.Name} did={slot.Did} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} version={version} status={afterStatus}";
                exitLog = $"[TUNER] stage=release_exit slot={slot.SlotIndex} name={slot.Name} did={slot.Did} previousKind={kind} previousReservationId={(rid.HasValue ? rid.Value.ToString() : "-")} previousPid={(pid.HasValue ? pid.Value.ToString() : "-")} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} version={version} status={afterStatus}";
            }
        }

        if (staleLog is not null)
        {
            _log.Add("TUNER_STALE_LEASE_REJECTED", group, staleLog);
            return;
        }
        _log.Add("TUNER_TRACE", group, enterLog!);
        _log.Add("TunerPool", group, releasedLog!);
        _log.Add("TUNER_TRACE", group, exitLog!);
    }

    internal void UpdateProcessId(Slot slot, TunerLeaseIdentity identity, int pid)
    {
        string? staleLog = null;
        string? beforeLog = null;
        string? afterLog = null;
        string group = slot.Group;

        lock (_gate)
        {
            if (!slot.Matches(identity))
            {
                staleLog = BuildStaleLeaseLogUnsafe(slot, identity, "set_process_id");
            }
            else
            {
                var reservationId = slot.ReservationId;
                var previousPid = slot.ProcessId;
                var beforeStatus = GetStatusSummaryUnsafe();
                beforeLog = $"[TUNER] stage=set_process_id slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(reservationId.HasValue ? reservationId.Value.ToString() : "-")} pid={pid} previousPid={(previousPid.HasValue ? previousPid.Value.ToString() : "-")} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} status={beforeStatus}";
                slot.SetProcessId(pid);
                PublishStateChangeUnsafe();
                afterLog = $"[TUNER] stage=set_process_id_done slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(reservationId.HasValue ? reservationId.Value.ToString() : "-")} pid={pid} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}";
            }
        }

        if (staleLog is not null)
        {
            _log.Add("TUNER_STALE_LEASE_REJECTED", group, staleLog);
            return;
        }
        _log.Add("TUNER_TRACE", group, beforeLog!);
        _log.Add("TUNER_TRACE", group, afterLog!);
    }

    internal void UpdatePlannedEndTime(Slot slot, TunerLeaseIdentity identity, DateTime plannedEndTime, string reason)
    {
        string? staleLog = null;
        string? beforeLog = null;
        string? afterLog = null;
        string group = slot.Group;

        lock (_gate)
        {
            if (!slot.Matches(identity))
            {
                staleLog = BuildStaleLeaseLogUnsafe(slot, identity, "update_planned_end");
            }
            else
            {
                var before = slot.PlannedEndTime;
                var reservationId = slot.ReservationId;
                var beforeStatus = GetStatusSummaryUnsafe();
                beforeLog = $"[TUNER] stage=update_planned_end slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(reservationId.HasValue ? reservationId.Value.ToString() : "-")} before={(before.HasValue ? before.Value.ToString("MM/dd HH:mm:ss") : "-")} after={plannedEndTime:MM/dd HH:mm:ss} reason={reason} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} status={beforeStatus}";
                slot.UpdatePlannedEndTime(plannedEndTime);
                PublishStateChangeUnsafe();
                afterLog = $"[TUNER] stage=update_planned_end_done slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(reservationId.HasValue ? reservationId.Value.ToString() : "-")} after={plannedEndTime:MM/dd HH:mm:ss} leaseId={identity.PoolLeaseId} generation={identity.OccupancyGeneration} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}";
            }
        }

        if (staleLog is not null)
        {
            _log.Add("TUNER_STALE_LEASE_REJECTED", group, staleLog);
            return;
        }
        _log.Add("TUNER_TRACE", group, beforeLog!);
        _log.Add("TUNER_TRACE", group, afterLog!);
    }

    // ─── 内部処理 ────────────────────────────────────────────────




    /// <summary>
    /// 指定グループの空きスロットを返す。複数ある場合は LastReleasedAt が最も古いものを優先。
    /// これにより、直前まで録画/EPGで使っていた物理チューナーを即再利用することを避ける。
    /// 一度も使われていないスロット（LastReleasedAt==null）は最優先で選ばれる。
    /// </summary>
    private Slot? FindFree(string group, bool includeViewingReserved = false)
        => _slots
            .Where(s => MatchGroup(s, group) && s.IsFree && (includeViewingReserved || !s.IsViewingReserved))
            .OrderBy(s => s.LastReleasedAt ?? DateTime.MinValue)
            .FirstOrDefault();

    /// <summary>
    /// スロットの前回Releaseから現在までの経過ミリ秒を返す。
    /// 一度もReleaseされていない場合は null。
    /// </summary>
    private static double? GetElapsedSinceReleaseMs(Slot slot)
        => slot.LastReleasedAt is DateTime t
            ? (DateTime.Now - t).TotalMilliseconds
            : (double?)null;

    private static string FormatElapsed(double? ms)
        => ms is null ? "n/a" : $"{ms.Value:F0}ms";

    private static bool MatchGroup(Slot slot, string group)
    {
        var slotGroup = NormalizeViewingGroup(slot.Group);
        var reqGroup  = NormalizeViewingGroup(group);
        if (slotGroup == "HYBRID")
            return reqGroup is "GR" or "BSCS" or "HYBRID";
        return string.Equals(slotGroup, reqGroup, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeViewingGroup(string? group)
    {
        var raw = (group ?? string.Empty).Trim();
        var g = raw.ToUpperInvariant();
        return g switch
        {
            "GR" or "地上波" or "地デジ" or "GROUND" => "GR",
            "BS" or "CS" or "BSCS" or "BS/CS" => "BSCS",
            "HYBRID" or "GRBSCS" or "GR/BSCS" or "GR/BS/CS" or "地/BS/CS" or "地デジ/BS/CS" or "地上波/BS/CS" => "HYBRID",
            _ => raw
        };
    }

    private static string SafeValue(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private string GetStatusSummaryUnsafe()
    {
        var parts = _slots
            .OrderBy(s => s.SlotIndex)
            .Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/{s.Group}/{s.Role}/{s.UsageKind}/R{(s.ReservationId.HasValue ? s.ReservationId.Value.ToString() : "-")}/L{(s.PoolLeaseId.HasValue ? s.PoolLeaseId.Value.ToString("N")[..8] : "-")}/G{s.OccupancyGeneration}/{s.LeaseState}");
        return string.Join(", ", parts);
    }


    private TunerSlotStatus ToStatusUnsafe(Slot s) => new(
        s.Name, s.BonDriverFileName, s.Did, s.Group, s.Role, s.SlotIndex,
        s.UsageKind, s.ReservationId, s.ProcessId, s.PlannedEndTime, s.LastReleasedAt,
        s.PoolLeaseId, s.OccupancyGeneration, s.LeaseState, _lifecycleState, _snapshotVersion);


    public bool BeginQuiescing(string reason)
    {
        string? logMessage = null;
        lock (_gate)
        {
            if (_lifecycleState != TunerPoolLifecycleState.Running) return false;
            _lifecycleState = TunerPoolLifecycleState.Quiescing;
            PublishStateChangeUnsafe();
            logMessage = $"reason={SafeValue(reason)} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}";
        }
        _log.Add("TUNER_POOL_LIFECYCLE", "Quiescing", logMessage!);
        return true;
    }

    public void Dispose()
    {
        string? logMessage = null;
        lock (_gate)
        {
            if (_lifecycleState == TunerPoolLifecycleState.Disposed) return;
            _lifecycleState = TunerPoolLifecycleState.Disposed;
            foreach (var s in _slots.Where(s => !s.IsFree))
            {
                s.Release(revoked: true);
                PublishStateChangeUnsafe();
            }
            PublishStateChangeUnsafe();
            logMessage = $"version={_snapshotVersion} status={GetStatusSummaryUnsafe()}";
        }
        _log.Add("TUNER_POOL_LIFECYCLE", "Disposed", logMessage!);
    }

}

// ─── 補助型 ──────────────────────────────────────────────────────

public enum TunerUsageKind { Free, Epg, Recording, Viewing }
public enum TunerLeaseState { Free, Active, Revoked }
public enum TunerPoolLifecycleState { Running, Quiescing, Disposed }
public enum TunerAvailabilityWaitResult { Available, TimedOut, PoolStopping, PoolDisposed }
public readonly record struct TunerLeaseIdentity(Guid PoolLeaseId, long OccupancyGeneration);

/// <summary>スロット状態の読み取り専用スナップショット。</summary>
public sealed record TunerSlotStatus(
    string Name,
    string BonDriverFileName,
    string Did,
    string Group,
    string Role,
    int SlotIndex,
    TunerUsageKind UsageKind,
    int? ReservationId,
    int? ProcessId,
    DateTime? PlannedEndTime,
    DateTime? LastReleasedAt,
    Guid? PoolLeaseId,
    long OccupancyGeneration,
    TunerLeaseState LeaseState,
    TunerPoolLifecycleState PoolLifecycleState,
    long SnapshotVersion);

/// <summary>録画接近通知用の軽量値型。</summary>
public sealed record UpcomingRecording(
    int ReservationId,
    string Group,
    DateTime StartTime,
    string ServiceName,
    string Title);

/// <summary>
/// チューナースロット占有リース。using で囲むと Dispose 時に自動解放される。
/// </summary>
public sealed class TunerLease : IDisposable
{
    private const int ReleaseStateActive = 0;
    private const int ReleaseStateReleasing = 1;
    private const int ReleaseStateReleased = 2;
    private const int ConcurrentReleaseWaitTimeoutMs = 2_000;

    private readonly TunerPool.Slot _slot;
    private readonly TunerPool _pool;
    private readonly TunerLeaseIdentity _identity;
    private readonly object _releaseGate = new();
    private int _releaseState = ReleaseStateActive;

    internal TunerLease(TunerPool.Slot slot, TunerPool pool, double? elapsedSinceReleaseMs, TunerLeaseIdentity identity)
    {
        _slot = slot;
        _pool = pool;
        _identity = identity;
        ElapsedSinceReleaseMs = elapsedSinceReleaseMs;
    }

    public string Name              => _slot.Name;
    public string BonDriverFileName => _slot.BonDriverFileName;
    public string Did               => _slot.Did;
    public string Group             => _slot.Group;
    public string LogicalViewerSlotId => _slot.LogicalViewerSlotId;
    public int    SlotIndex         => _slot.SlotIndex;
    public Guid PoolLeaseId => _identity.PoolLeaseId;
    public long OccupancyGeneration => _identity.OccupancyGeneration;
    public bool IsCurrent =>
        Volatile.Read(ref _releaseState) == ReleaseStateActive
        && _pool.IsLeaseCurrent(_slot, _identity);

    // Dispose状態に関係なく、Pool正本に同じidentityが残っているかを返す。
    public bool IsIdentityCurrent => _pool.IsLeaseIdentityCurrent(_slot, _identity);

    /// <summary>
    /// この Lease を取得した時点で、同一スロットの前回Releaseから経過していたミリ秒。
    /// 一度もReleaseされていないスロットでは null。
    /// 呼び出し元（ReservationScheduler 等）がクールダウン判定に使用する。
    /// </summary>
    public double? ElapsedSinceReleaseMs { get; }

    /// <summary>TVTest起動後にプロセスIDを記録する。</summary>
    public void SetProcessId(int pid)
    {
        EnterReleaseGateOrThrow("set_process_id");
        try
        {
            EnsureActiveForMutationUnsafe("set_process_id");
            _pool.UpdateProcessId(_slot, _identity, pid);
        }
        finally
        {
            Monitor.Exit(_releaseGate);
        }
    }

    /// <summary>録画中時間追従で、同一チューナースロットの終了予定を更新する。</summary>
    public void UpdatePlannedEndTime(DateTime plannedEndTime, string reason)
    {
        EnterReleaseGateOrThrow("update_planned_end");
        try
        {
            EnsureActiveForMutationUnsafe("update_planned_end");
            _pool.UpdatePlannedEndTime(_slot, _identity, plannedEndTime, reason);
        }
        finally
        {
            Monitor.Exit(_releaseGate);
        }
    }

    public void Dispose()
    {
        EnterReleaseGateOrThrow("dispose");
        try
        {
            while (_releaseState == ReleaseStateReleasing)
            {
                if (Monitor.Wait(_releaseGate, ConcurrentReleaseWaitTimeoutMs))
                    continue;

                // LEASE_RELEASE_WAIT_RECHECK_INVARIANT:
                // timeout境界で先行releaseが状態を確定してもPulseを観測できない場合がある。
                // 待機失敗だけで解放失敗へせず、原子的な最終状態を再確認する。
                var stateAfterWait = Volatile.Read(ref _releaseState);
                if (stateAfterWait == ReleaseStateReleased)
                    return;
                if (stateAfterWait == ReleaseStateActive)
                    break; // 先行release失敗後。自分が次のrelease ownerとして再試行する。

                throw new TimeoutException($"Tuner lease release wait timed out: slot={SlotIndex} did={Did} leaseId={PoolLeaseId}");
            }

            if (_releaseState == ReleaseStateReleased)
                return;

            _releaseState = ReleaseStateReleasing;
        }
        finally
        {
            Monitor.Exit(_releaseGate);
        }

        Exception? releaseError = null;
        try
        {
            _pool.Release(_slot, _identity);
        }
        catch (Exception ex)
        {
            releaseError = ex;
        }

        var identityStillCurrent = true;
        try
        {
            identityStillCurrent = _pool.IsLeaseIdentityCurrent(_slot, _identity);
        }
        catch
        {
            // 観測不能時は安全側にActiveへ戻し、後続cleanupの再試行を許可する。
            identityStillCurrent = true;
        }

        // LEASE_RELEASE_COMPLETION_PUBLISH_INVARIANT:
        // Pool解放後の状態確定をreleaseGate再取得に依存させない。
        // 再取得がタイムアウトするとReleasingのまま永久残留し、以後のmutation／Disposeが全て失敗する。
        // 先に原子的にActiveまたはReleasedへ確定し、待機者へのPulseだけをベストエフォートで行う。
        Interlocked.Exchange(
            ref _releaseState,
            identityStillCurrent ? ReleaseStateActive : ReleaseStateReleased);

        if (Monitor.TryEnter(_releaseGate, ConcurrentReleaseWaitTimeoutMs))
        {
            try
            {
                Monitor.PulseAll(_releaseGate);
            }
            finally
            {
                Monitor.Exit(_releaseGate);
            }
        }

        // LEASE_RELEASE_POOL_TRUTH_INVARIANT:
        // Release呼出しが例外を返しても、Pool正本から同一identityが消えていれば解放は成立済みである。
        // その場合に例外を再送出すると外側wrapperがentryをActiveへ復帰させ、消滅済みleaseを管理上だけ残す。
        if (releaseError is not null && identityStillCurrent)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(releaseError).Throw();
    }

    private void EnterReleaseGateOrThrow(string operation)
    {
        if (!Monitor.TryEnter(_releaseGate, ConcurrentReleaseWaitTimeoutMs))
            throw new TimeoutException($"Tuner lease gate wait timed out: operation={operation} slot={SlotIndex} did={Did} leaseId={PoolLeaseId}");
    }

    private void EnsureActiveForMutationUnsafe(string operation)
    {
        if (_releaseState != ReleaseStateActive || !_pool.IsLeaseCurrent(_slot, _identity))
            throw new InvalidOperationException($"Tuner lease is not active: operation={operation} slot={SlotIndex} did={Did} leaseId={PoolLeaseId}");
    }
}
