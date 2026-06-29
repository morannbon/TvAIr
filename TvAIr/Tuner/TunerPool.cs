using TvAIr.Core;

namespace TvAIr.Tuner;

/// <summary>
/// チューナーリソースの一元管理。
///
/// 管理単位 : TunerProfile 1本 = 物理チューナー1本。
/// 占有種別 : EPG / Recording / Viewing の3種。
/// 優先順位 : Recording > Viewing > EPG
///
/// ・EPG は録画開始の WakeMinutesBefore 分前になると強制解放される。
/// ・空きがあれば Recording / Viewing は即座に確保できる。
/// ・全スロット埋まりかつ Viewing が競合する場合は ForceReleaseViewing を使う。
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
        public int    SlotIndex         { get; }

        public TunerUsageKind UsageKind    { get; private set; } = TunerUsageKind.Free;
        public int?           ReservationId { get; private set; }
        public int?           ProcessId     { get; private set; }
        public DateTime?      PlannedEndTime{ get; private set; }
        /// <summary>直近の Release が実行された時刻。Acquire 時のクールダウン計算用。</summary>
        public DateTime?      LastReleasedAt{ get; private set; }
        public bool IsFree => UsageKind == TunerUsageKind.Free;
        // release_contract: 視聴不可侵判定は設定Roleを正とする。
        // BonDriver名だけでは判定しない。
        public bool IsViewingReserved => string.Equals(Role, "Viewing", StringComparison.OrdinalIgnoreCase);

        public Slot(string name, string bonDriverFileName, string did, string group, string role, int slotIndex)
        {
            Name              = name;
            BonDriverFileName = bonDriverFileName;
            Did               = did;
            Group             = group;
            Role              = IniSettingsService.NormalizeTunerRole(role);
            SlotIndex         = slotIndex;
        }

        public void Occupy(TunerUsageKind kind, int? reservationId, int? processId, DateTime? plannedEndTime)
        {
            UsageKind      = kind;
            ReservationId  = reservationId;
            ProcessId      = processId;
            PlannedEndTime = plannedEndTime;
        }

        public void SetProcessId(int pid) => ProcessId = pid;

        public void UpdatePlannedEndTime(DateTime plannedEndTime)
        {
            PlannedEndTime = plannedEndTime;
        }

        public void Release()
        {
            UsageKind      = TunerUsageKind.Free;
            ReservationId  = null;
            ProcessId      = null;
            PlannedEndTime = null;
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

    public TunerPool(
        IReadOnlyList<TunerProfile> profiles,
        IniSettingsService ini,
        LogRepository log)
    {
        _ini = ini;
        _log = log;

        var idx = 0;
        var rejected = 0;
        foreach (var p in profiles)
        {
            var normalizedGroup = TunerDisplayName.NormalizeGroup(p.Group);
            var normalizedRole = IniSettingsService.NormalizeTunerRole(p.Role);
            var normalizedBonDriver = TunerIsolationPolicy.NormalizeBonDriverForRole(p.BonDriverFileName, normalizedGroup, normalizedRole);
            if (!TunerDisplayName.IsKnownGroup(normalizedGroup) || !IniSettingsService.IsKnownTunerRole(normalizedRole) || string.IsNullOrWhiteSpace(normalizedBonDriver))
            {
                rejected++;
                continue;
            }
            _slots.Add(new Slot(p.Name, normalizedBonDriver, p.Did, normalizedGroup, normalizedRole, idx++));
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

    private void TraceTuner(string title, string message)
        => _log.Add("TUNER_TRACE", title, "[TUNER] " + message + " status=" + GetStatusSummaryUnsafe());

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
        lock (_gate)
        {
            TraceTuner(group, $"stage=acquire_recording_enter group={group} reservationId={reservationId} plannedEnd={plannedEndTime:MM/dd HH:mm:ss}");
            var slot = FindFree(group);
            if (slot is null)
            {
                TraceTuner(group, $"stage=acquire_recording_fail group={group} reservationId={reservationId} reason=no_free_slot");
                return null;
            }
            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Recording, reservationId, null, plannedEndTime);
            _snapshotVersion++;
            _log.Add("TunerPool", group,
                $"Recording 確保: slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                $"reservationId={reservationId} elapsedSinceRelease={FormatElapsed(elapsedMs)}");
            TraceTuner(group, $"stage=acquire_recording_ok group={group} reservationId={reservationId} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)}");
            return new TunerLease(slot, this, elapsedMs);
        }
    }

    /// <summary>
    /// 指定チューナー名のスロットを優先して録画用に確保する。
    /// 該当スロットが空きでなければ null を返す（呼び出し元でフォールバック）。
    /// </summary>
    public TunerLease? AcquireForRecordingByName(
        string tunerName, int reservationId, DateTime plannedEndTime)
    {
        lock (_gate)
        {
            TraceTuner(tunerName, $"stage=acquire_recording_by_name_enter tuner={tunerName} reservationId={reservationId} plannedEnd={plannedEndTime:MM/dd HH:mm:ss}");
            var slot = _slots.FirstOrDefault(
                s => string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase) && s.IsFree && !s.IsViewingReserved);
            if (slot is null)
            {
                TraceTuner(tunerName, $"stage=acquire_recording_by_name_fail tuner={tunerName} reservationId={reservationId} reason=not_free_or_viewing_reserved");
                return null;
            }
            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Recording, reservationId, null, plannedEndTime);
            _snapshotVersion++;
            _log.Add("TunerPool", slot.Group,
                $"Recording 確保(指定): slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                $"reservationId={reservationId} elapsedSinceRelease={FormatElapsed(elapsedMs)}");
            TraceTuner(slot.Group, $"stage=acquire_recording_by_name_ok tuner={tunerName} reservationId={reservationId} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)}");
            return new TunerLease(slot, this, elapsedMs);
        }
    }

    /// <summary>
    /// 外部視聴状態を監査し、明示検出できた衝突だけを扱う。
    /// DID不明の外部TVTestを理由に録画候補を推測でずらさない。
    /// </summary>
    public TunerLease? AcquireForRecordingWithExternalGuard(
        string group,
        int reservationId,
        DateTime plannedEndTime,
        bool reserveUnknownExternalBuffer,
        string reason)
    {
        lock (_gate)
        {
            TraceTuner(group, $"stage=acquire_recording_external_guard_enter group={group} reservationId={reservationId} plannedEnd={plannedEndTime:MM/dd HH:mm:ss} reserveUnknownExternalBuffer={reserveUnknownExternalBuffer} reason={reason}");
            var slot = OrderedRecordableFreeSlots(group).FirstOrDefault();
            if (reserveUnknownExternalBuffer)
            {
                _log.Add("TUNER_EXTERNAL_GUARD", group,
                    $"result=NO_REROUTE reservationId={reservationId} assigned={(slot is null ? "-" : slot.Name + "/" + slot.Did)} reason={reason} rule=explicit_detected_state_only_no_unknown_did_buffer");
            }
            if (slot is null)
            {
                TraceTuner(group, $"stage=acquire_recording_external_guard_fail group={group} reservationId={reservationId} reason=no_free_slot reserveUnknownExternalBuffer={reserveUnknownExternalBuffer}");
                return null;
            }
            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Recording, reservationId, null, plannedEndTime);
            _snapshotVersion++;
            _log.Add("TunerPool", group,
                $"Recording 確保: slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                $"reservationId={reservationId} elapsedSinceRelease={FormatElapsed(elapsedMs)} externalGuard={reserveUnknownExternalBuffer}");
            TraceTuner(group, $"stage=acquire_recording_external_guard_ok group={group} reservationId={reservationId} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)} reserveUnknownExternalBuffer={reserveUnknownExternalBuffer}");
            return new TunerLease(slot, this, elapsedMs);
        }
    }

    /// <summary>
    /// 事前割当チューナーを明示指定で確保する。
    /// DID不明の外部TVTestを理由に別録画用スロットへ逃がさない。
    /// </summary>
    public TunerLease? AcquireForRecordingByNameWithExternalGuard(
        string tunerName,
        int reservationId,
        DateTime plannedEndTime,
        bool reserveUnknownExternalBuffer,
        string reason)
    {
        lock (_gate)
        {
            TraceTuner(tunerName, $"stage=acquire_recording_by_name_external_guard_enter tuner={tunerName} reservationId={reservationId} plannedEnd={plannedEndTime:MM/dd HH:mm:ss} reserveUnknownExternalBuffer={reserveUnknownExternalBuffer} reason={reason}");
            var requested = _slots.FirstOrDefault(
                s => string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase) && s.IsFree && !s.IsViewingReserved);
            var occupiedRequested = _slots.FirstOrDefault(
                s => string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase) && !s.IsFree);
            if (requested is null && occupiedRequested is not null)
            {
                _log.Add("ACTIVE_RECORDING_DID_GUARD", occupiedRequested.Group,
                    $"result=REQUESTED_BUSY tuner={tunerName} did={occupiedRequested.Did} usage={occupiedRequested.UsageKind} owner={(occupiedRequested.ReservationId.HasValue ? "R" + occupiedRequested.ReservationId.Value : "-")} pid={(occupiedRequested.ProcessId.HasValue ? occupiedRequested.ProcessId.Value.ToString() : "-")} requester=R{reservationId} action=do_not_reuse_busy_did rule=release_contract");
            }
            Slot? slot = requested;
            if (requested is not null && reserveUnknownExternalBuffer)
            {
                _log.Add("TUNER_EXTERNAL_GUARD", requested.Group,
                    $"result=NO_REROUTE reservationId={reservationId} requested={requested.Name}/{requested.Did} assigned={requested.Name}/{requested.Did} reason={reason} rule=explicit_detected_state_only_no_unknown_did_buffer");
            }
            if (slot is null)
            {
                TraceTuner(tunerName, $"stage=acquire_recording_by_name_external_guard_fail tuner={tunerName} reservationId={reservationId} reason=not_free_or_viewing_reserved");
                return null;
            }
            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Recording, reservationId, null, plannedEndTime);
            _snapshotVersion++;
            _log.Add("TunerPool", slot.Group,
                $"Recording 確保(指定/外部視聴ガード): slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                $"reservationId={reservationId} requested={tunerName} elapsedSinceRelease={FormatElapsed(elapsedMs)} externalGuard={reserveUnknownExternalBuffer}");
            TraceTuner(slot.Group, $"stage=acquire_recording_by_name_external_guard_ok tuner={tunerName} reservationId={reservationId} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)} reserveUnknownExternalBuffer={reserveUnknownExternalBuffer}");
            return new TunerLease(slot, this, elapsedMs);
        }
    }


    /// <summary>
    /// 録画前プリチューン用に、実録画予定チューナー名のEPG leaseを優先確保する。
    /// 録画時と同じ物理DIDで事前選局・PAT/PMT/対象SID確認を行うための入口。
    /// </summary>
    public TunerLease? AcquireForEpgByName(
        string tunerName,
        string group,
        DateTime plannedEndTime,
        IReadOnlySet<(string BonDriverFileName, string Did)>? excludeTunerKeys = null,
        string reason = "pre_record_pretune")
    {
        lock (_gate)
        {
            TraceTuner(tunerName, $"stage=acquire_epg_by_name_enter tuner={tunerName} group={group} plannedEnd={plannedEndTime:MM/dd HH:mm:ss} reason={reason}");
            var slot = _slots.FirstOrDefault(s =>
                string.Equals(s.Name, tunerName, StringComparison.OrdinalIgnoreCase)
                && MatchGroup(s, group)
                && s.IsFree
                && !s.IsViewingReserved
                && !IsExcludedForEpg(s, excludeTunerKeys));

            if (slot is null)
            {
                TraceTuner(tunerName, $"stage=acquire_epg_by_name_fail tuner={tunerName} group={group} reason=not_free_or_excluded_or_viewing_reserved");
                return null;
            }

            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Epg, null, null, plannedEndTime);
            _snapshotVersion++;
            _log.Add("TunerPool", group,
                $"EPG 確保(録画前プリチューン指定): slot={slot.SlotIndex} name={slot.Name} did={slot.Did} " +
                $"requested={tunerName} elapsedSinceRelease={FormatElapsed(elapsedMs)} reason={reason}");
            TraceTuner(group, $"stage=acquire_epg_by_name_ok tuner={tunerName} slot={slot.SlotIndex} name={slot.Name} did={slot.Did} elapsedSinceRelease={FormatElapsed(elapsedMs)} reason={reason}");
            return new TunerLease(slot, this, elapsedMs);
        }
    }

    private static bool IsExcludedForEpg(Slot s, IReadOnlySet<(string BonDriverFileName, string Did)>? excludeTunerKeys)
    {
        if (excludeTunerKeys is null || excludeTunerKeys.Count == 0) return false;
        foreach (var ex in excludeTunerKeys)
        {
            if (string.Equals(s.BonDriverFileName, ex.BonDriverFileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Did, ex.Did, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// EPG 用にチューナーを確保する。空きがなければ null を返す。
    /// excludeTunerKeys を渡すと、(BonDriverFileName, Did) が一致するスロットを
    /// 候補から除外する。LIVE視聴中の TVTest が使っている物理チューナーを避ける用途。
    /// </summary>
    public TunerLease? AcquireForEpg(
        string group,
        DateTime plannedEndTime,
        IReadOnlySet<(string BonDriverFileName, string Did)>? excludeTunerKeys = null)
    {
        lock (_gate)
        {
            var slot = FindFreeForEpg(group, excludeTunerKeys);
            if (slot is null) return null;
            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Epg, null, null, plannedEndTime);
            _snapshotVersion++;
            _log.Add("TunerPool", group,
                $"EPG 確保: slot={slot.SlotIndex} did={slot.Did} " +
                $"elapsedSinceRelease={FormatElapsed(elapsedMs)}");
            return new TunerLease(slot, this, elapsedMs);
        }
    }

    /// <summary>
    /// EPG用の空きスロット検索。LIVE視聴中チューナーを除外できる版。
    /// excludeTunerKeys に含まれる (BonDriver, Did) のスロットはスキップする。
    /// </summary>
    private Slot? FindFreeForEpg(
        string group,
        IReadOnlySet<(string BonDriverFileName, string Did)>? excludeTunerKeys)
    {
        bool IsExcluded(Slot s)
        {
            if (excludeTunerKeys is null || excludeTunerKeys.Count == 0) return false;
            foreach (var ex in excludeTunerKeys)
            {
                if (string.Equals(s.BonDriverFileName, ex.BonDriverFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Did, ex.Did, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        return _slots
            .Where(s => MatchGroup(s, group) && s.IsFree && !s.IsViewingReserved && !string.IsNullOrWhiteSpace(s.BonDriverFileName) && !IsExcluded(s))
            .OrderBy(s => s.LastReleasedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// 視聴用にチューナーを確保する。空きがなければ null を返す。
    /// </summary>
    public TunerLease? AcquireForViewing(string group, int? processId = null, int viewerProfileFrameIndex = 0)
    {
        lock (_gate)
        {
            var normalizedGroup = NormalizeViewingGroup(group);
            var exactGroupSlots = _slots
                .Where(s => NormalizeViewingGroup(s.Group) == normalizedGroup && s.IsViewingReserved)
                .OrderBy(s => string.IsNullOrWhiteSpace(s.Name) ? "~" : s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => string.IsNullOrWhiteSpace(s.Did) ? "~" : s.Did, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.SlotIndex)
                .ToList();
            var hybridSlots = _slots
                .Where(s => NormalizeViewingGroup(s.Group) == "HYBRID" && s.IsViewingReserved)
                .OrderBy(s => string.IsNullOrWhiteSpace(s.Name) ? "~" : s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => string.IsNullOrWhiteSpace(s.Did) ? "~" : s.Did, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.SlotIndex)
                .ToList();
            var orderedViewingSlots = _slots
                .Where(s => MatchGroup(s, group) && s.IsViewingReserved)
                .OrderBy(s => s.LastReleasedAt ?? DateTime.MinValue)
                .ThenBy(s => s.SlotIndex)
                .ToList();

            Slot? slot;
            if (viewerProfileFrameIndex > 0)
            {
                slot = exactGroupSlots.Skip(viewerProfileFrameIndex - 1).FirstOrDefault()
                    ?? hybridSlots.Skip(viewerProfileFrameIndex - 1).FirstOrDefault();
                if (slot is null || !slot.IsFree)
                {
                    _log.Add("TunerPool", group,
                        $"Viewing 確保失敗: viewerProfileFrame={viewerProfileFrameIndex} reason=tvtest_frame_slot_unavailable pid={processId} " +
                        $"exactCandidates={string.Join(",", exactGroupSlots.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/{s.UsageKind}"))} " +
                        $"hybridCandidates={string.Join(",", hybridSlots.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/{s.UsageKind}"))} rule=release_contract");
                    return null;
                }
            }
            else
            {
                slot = orderedViewingSlots
                    .Where(s => s.IsFree)
                    .OrderBy(s => s.LastReleasedAt ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (slot is null) return null;
            }

            var elapsedMs = GetElapsedSinceReleaseMs(slot);
            slot.Occupy(TunerUsageKind.Viewing, null, processId, null);
            _snapshotVersion++;
            _log.Add("TunerPool", group,
                $"Viewing 確保: slot={slot.SlotIndex} did={slot.Did} pid={processId} viewerProfileFrame={(viewerProfileFrameIndex > 0 ? viewerProfileFrameIndex.ToString() : "auto")} " +
                $"elapsedSinceRelease={FormatElapsed(elapsedMs)} rule=release_contract");
            return new TunerLease(slot, this, elapsedMs);
        }
    }

    /// <summary>
    /// 録画が迫っている予約リストを受け取り、WakeMinutesBefore 分以内に始まる
    /// 録画のグループで動いている EPG スロットを強制解放する。
    /// </summary>
    public void PreemptEpgForUpcomingRecordings(IReadOnlyList<UpcomingRecording> upcoming)
    {
        lock (_gate)
        {
            var threshold = TimeSpan.FromMinutes(_ini.WakeMinutesBefore);
            var now = DateTime.Now;
            foreach (var rec in upcoming)
            {
                if (rec.StartTime - now > threshold) continue;
                var targets = _slots
                    .Where(s => MatchGroup(s, rec.Group) && s.UsageKind == TunerUsageKind.Epg)
                    .ToList();
                if (targets.Count <= 0) continue;

                _log.Add("EPG_PREEMPT_NOTICE", rec.Group,
                    $"reservationId={rec.ReservationId} service={TrimForLog(rec.ServiceName, 32)} title={ReservationTitleDisplayContract.ForLog(rec.Title, 48)} start={rec.StartTime:HH:mm:ss} epgSlots={targets.Count} " +
                    $"targets={string.Join(",", targets.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/pid={(s.ProcessId.HasValue ? s.ProcessId.Value.ToString() : "-")}"))} " +
                    "rule=release_contract action=recording_scheduler_preempt_by_tuner_pool_pid");
            }
        }
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


    /// <summary>
    /// 録画開始直前のEPG退避で、TVTestプロセス終了を確認したPIDに対応するEPGスロットを即時解放する。
    /// 通常のEPG workerのfinally任せにすると、録画開始側の空き確認と競争して本番録画を落とすため、
    /// 録画優先プリエンプトでは録画側から明示的にTunerPool状態を閉じる。
    /// </summary>
    public bool ForceReleaseEpgByProcessId(string group, int processId, string? did, string? bonDriverFileName, string owner, string label)
    {
        if (processId <= 0 && string.IsNullOrWhiteSpace(did)) return false;
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => MatchGroup(s, group)
                && s.UsageKind == TunerUsageKind.Epg
                && s.ProcessId == processId)
                ?? _slots.FirstOrDefault(s => MatchGroup(s, group)
                    && s.UsageKind == TunerUsageKind.Epg
                    && string.Equals(s.Did, did, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(bonDriverFileName) || string.Equals(s.BonDriverFileName, bonDriverFileName, StringComparison.OrdinalIgnoreCase)));
            if (slot is null)
            {
                _log.Add("REC_TUNER_FORCE_RELEASE", group,
                    $"result=MISS pid={processId} did={SafeValue(did)} bonDriver={SafeValue(bonDriverFileName)} owner={SafeValue(owner)} label={SafeValue(label)} status={GetStatusSummaryUnsafe()}");
                return false;
            }

            var before = $"#{slot.SlotIndex}:{slot.Name}/{slot.Did}/{slot.Group}/{slot.UsageKind}/pid={(slot.ProcessId.HasValue ? slot.ProcessId.Value.ToString() : "-")}";
            slot.Release();
            _snapshotVersion++;
            _log.Add("REC_TUNER_FORCE_RELEASE", group,
                $"result=OK pid={processId} did={SafeValue(did)} bonDriver={SafeValue(bonDriverFileName)} released={before} owner={SafeValue(owner)} label={SafeValue(label)} version={_snapshotVersion} status={GetStatusSummaryUnsafe()} rule=release_contract");
            return true;
        }
    }

    /// <summary>
    /// 録画優先プリエンプトの最終段で、PIDが未反映/終了直後などのEPG leaseだけを解放する。
    /// PID付きスロットは停止処理の対象にすべきなのでここでは触らない。
    /// </summary>
    public int ForceReleasePidlessEpgSlots(string group, string owner, string label)
    {
        lock (_gate)
        {
            var targets = _slots
                .Where(s => MatchGroup(s, group)
                    && s.UsageKind == TunerUsageKind.Epg
                    && !s.ProcessId.HasValue)
                .OrderBy(s => s.SlotIndex)
                .ToList();
            if (targets.Count <= 0) return 0;

            var released = string.Join(",", targets.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/pid=-"));
            foreach (var slot in targets)
            {
                slot.Release();
                _snapshotVersion++;
            }

            _log.Add("REC_TUNER_FORCE_RELEASE", group,
                $"result=OK_PIDLESS count={targets.Count} released={released} owner={SafeValue(owner)} label={SafeValue(label)} version={_snapshotVersion} status={GetStatusSummaryUnsafe()} rule=release_contract");
            return targets.Count;
        }
    }

    /// <summary>視聴中スロットを強制解放する（録画割り込み時）。</summary>
    public bool ForceReleaseViewing(string group)
    {
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(
                s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Viewing && s.IsViewingReserved);
            if (slot is null) return false;
            slot.Release();
            _snapshotVersion++;
            _log.Add("TunerPool", group, $"Viewing 強制解放（録画割り込み） version={_snapshotVersion}");
            return true;
        }
    }

    /// <summary>指定グループに空きスロットがあるか。</summary>
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

    public int CountEpgUsableFreeSlots(string group, IReadOnlySet<(string BonDriverFileName, string Did)>? excludeTunerKeys = null)
    {
        lock (_gate)
            return _slots.Count(s => MatchGroup(s, group) && s.IsFree && !s.IsViewingReserved && !string.IsNullOrWhiteSpace(s.BonDriverFileName) && !IsExcludedForEpg(s, excludeTunerKeys));
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

    /// <summary>現在のスロット状態をログ出力しやすい1行文字列で返す。</summary>
    public string GetStatusSummary()
    {
        lock (_gate) return $"version={_snapshotVersion} " + GetStatusSummaryUnsafe();
    }

    /// <summary>視聴中スロットが存在するか（競合判定用）。</summary>
    public bool HasViewingSlot(string group)
    {
        lock (_gate)
            return _slots.Any(s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Viewing);
    }

    /// <summary>現在のスロット状態スナップショットを返す。</summary>
    public IReadOnlyList<TunerSlotStatus> GetStatus()
    {
        lock (_gate)
            return _slots.Select(ToStatusUnsafe).ToList();
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

    internal void Release(Slot slot)
    {
        lock (_gate)
        {
            var kind = slot.UsageKind;
            var rid = slot.ReservationId;
            var pid = slot.ProcessId;
            _log.Add("TUNER_TRACE", slot.Group, $"[TUNER] stage=release_enter slot={slot.SlotIndex} name={slot.Name} did={slot.Did} kind={kind} reservationId={(rid.HasValue ? rid.Value.ToString() : "-")} pid={(pid.HasValue ? pid.Value.ToString() : "-")} status={GetStatusSummaryUnsafe()}");
            slot.Release();
            _snapshotVersion++;
            _log.Add("TunerPool", slot.Group,
                $"{kind} 解放: slot={slot.SlotIndex} name={slot.Name} did={slot.Did} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}");
            _log.Add("TUNER_TRACE", slot.Group, $"[TUNER] stage=release_exit slot={slot.SlotIndex} name={slot.Name} did={slot.Did} previousKind={kind} previousReservationId={(rid.HasValue ? rid.Value.ToString() : "-")} previousPid={(pid.HasValue ? pid.Value.ToString() : "-")} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}");
        }
    }

    internal void UpdateProcessId(Slot slot, int pid)
    {
        lock (_gate)
        {
            _log.Add("TUNER_TRACE", slot.Group, $"[TUNER] stage=set_process_id slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} pid={pid} previousPid={(slot.ProcessId.HasValue ? slot.ProcessId.Value.ToString() : "-")} status={GetStatusSummaryUnsafe()}");
            slot.SetProcessId(pid);
            _snapshotVersion++;
            _log.Add("TUNER_TRACE", slot.Group, $"[TUNER] stage=set_process_id_done slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} pid={pid} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}");
        }
    }

    internal void UpdatePlannedEndTime(Slot slot, DateTime plannedEndTime, string reason)
    {
        lock (_gate)
        {
            var before = slot.PlannedEndTime;
            _log.Add("TUNER_TRACE", slot.Group, $"[TUNER] stage=update_planned_end slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} before={(before.HasValue ? before.Value.ToString("MM/dd HH:mm:ss") : "-")} after={plannedEndTime:MM/dd HH:mm:ss} reason={reason} status={GetStatusSummaryUnsafe()}");
            slot.UpdatePlannedEndTime(plannedEndTime);
            _snapshotVersion++;
            _log.Add("TUNER_TRACE", slot.Group, $"[TUNER] stage=update_planned_end_done slot={slot.SlotIndex} name={slot.Name} did={slot.Did} reservationId={(slot.ReservationId.HasValue ? slot.ReservationId.Value.ToString() : "-")} after={plannedEndTime:MM/dd HH:mm:ss} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}");
        }
    }

    // ─── 内部処理 ────────────────────────────────────────────────

    private int PreemptEpgSlots(string group)
    {
        var targets = _slots
            .Where(s => MatchGroup(s, group) && s.UsageKind == TunerUsageKind.Epg)
            .ToList();
        if (targets.Count > 0)
        {
            _log.Add("TUNER_TRACE", group, $"[TUNER] stage=preempt_epg_enter group={group} targets={string.Join(",", targets.Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}"))} status={GetStatusSummaryUnsafe()}");
        }
        foreach (var s in targets)
        {
            s.Release();
            _snapshotVersion++;
        }
        if (targets.Count > 0)
        {
            _log.Add("TUNER_TRACE", group, $"[TUNER] stage=preempt_epg_exit group={group} released={targets.Count} version={_snapshotVersion} status={GetStatusSummaryUnsafe()}");
        }
        return targets.Count;
    }

    private IEnumerable<Slot> OrderedRecordableFreeSlots(string group)
        => _slots
            .Where(s => MatchGroup(s, group) && s.IsFree && !s.IsViewingReserved)
            .OrderBy(s => s.LastReleasedAt ?? DateTime.MinValue)
            .ThenBy(s => s.SlotIndex);

    private Slot? FindFreeWithUnknownExternalGuard(
        string group,
        bool reserveUnknownExternalBuffer,
        string reason,
        int reservationId,
        string? requestedTunerName)
    {
        var selected = OrderedRecordableFreeSlots(group).FirstOrDefault();
        if (reserveUnknownExternalBuffer)
        {
            _log.Add("TUNER_EXTERNAL_GUARD", group,
                $"result=NO_REROUTE reservationId={reservationId} assigned={(selected is null ? "-" : selected.Name + "/" + selected.Did)} requested={SafeValue(requestedTunerName)} reason={reason} rule=explicit_detected_state_only_no_unknown_did_buffer");
        }
        return selected;
    }

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
            .Select(s => $"#{s.SlotIndex}:{s.Name}/{s.Did}/{s.Group}/{s.Role}/{s.UsageKind}/R{(s.ReservationId.HasValue ? s.ReservationId.Value.ToString() : "-")}");
        return string.Join(", ", parts);
    }


    private TunerSlotStatus ToStatusUnsafe(Slot s) => new(
        s.Name, s.BonDriverFileName, s.Did, s.Group, s.Role, s.SlotIndex,
        s.UsageKind, s.ReservationId, s.ProcessId, s.PlannedEndTime, s.LastReleasedAt, _snapshotVersion);


    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _slots.Where(s => !s.IsFree))
            {
                s.Release();
                _snapshotVersion++;
            }
        }
    }
}

// ─── 補助型 ──────────────────────────────────────────────────────

public enum TunerUsageKind { Free, Epg, Recording, Viewing }

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
    private readonly TunerPool.Slot _slot;
    private readonly TunerPool _pool;
    private bool _released;

    internal TunerLease(TunerPool.Slot slot, TunerPool pool, double? elapsedSinceReleaseMs)
    {
        _slot = slot;
        _pool = pool;
        ElapsedSinceReleaseMs = elapsedSinceReleaseMs;
    }

    public string Name              => _slot.Name;
    public string BonDriverFileName => _slot.BonDriverFileName;
    public string Did               => _slot.Did;
    public string Group             => _slot.Group;
    public int    SlotIndex         => _slot.SlotIndex;

    /// <summary>
    /// この Lease を取得した時点で、同一スロットの前回Releaseから経過していたミリ秒。
    /// 一度もReleaseされていないスロットでは null。
    /// 呼び出し元（ReservationScheduler 等）がクールダウン判定に使用する。
    /// </summary>
    public double? ElapsedSinceReleaseMs { get; }

    /// <summary>TVTest起動後にプロセスIDを記録する。</summary>
    public void SetProcessId(int pid) => _pool.UpdateProcessId(_slot, pid);

    /// <summary>録画中時間追従で、同一チューナースロットの終了予定を更新する。</summary>
    public void UpdatePlannedEndTime(DateTime plannedEndTime, string reason) => _pool.UpdatePlannedEndTime(_slot, plannedEndTime, reason);

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _pool.Release(_slot);
    }
}
