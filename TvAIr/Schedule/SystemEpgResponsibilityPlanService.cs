using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Tuner;
using TvAIr.Channel;

namespace TvAIr.Schedule;

/// <summary>
/// System EPGのうち、録画前EPG確認（PreRec）とDailyの「次責務」を決める計画正本。
///
/// INVARIANTS:
/// - System EPGの計画・責務・Projection・ON/OFF組合せ・PreRec選択はこのサービスを正本とする。
/// - Prepare は候補計算のみ、Publish は共通割当ルートによる子予約登録後の確定のみを行う。
/// - Build は保存済み責務の参照専用であり、予約一覧やWakeからSQLiteの担当を書き換えない。
/// - ①の所有者はSQLiteに確定して保持する。個の完了で他の未完了責務を詰め直さない。②は波単位で最大1件。
/// - 仕様変更時はSystem EPG表示、PreRec実行、責務advance、Daily fallbackを同一契約として横断確認する。
///
/// CONTRACT:
/// - 表示上のSystem EPG責務は録画用優先度ごとに最大1件。GR / BSCSは独立に評価する。
/// - PriorityNameは「ユーザーに見せる責務優先度」であり、PreRec実行時の物理Tuner固定ではない。
///   実PreRec workerは従来どおり実行時に同一波の空き録画Tunerを選ぶ。
/// - 各放送波の判定順は①→②→③。
///   ①: 現在から3時間以内（または既に実行窓内）の通常PreRec候補を、各優先度の既存所有関係を維持しつつ空き枠へ割当する。
///   ②: その放送波に①が一件も無い場合だけ、有効予約リストの先頭1件だけを暫定PreRec候補にする。
///      ②はGR最大1件 / BSCS最大1件。先頭を飛ばして後続予約を拾わない。
///   ③: Daily。①/②のPreRec取得枠が、その優先度のDaily開始までに完了できない場合はDailyへfallbackする。
/// - ②対象が①の3時間窓へ入った時点では、その担当を維持して①へ昇格する。
/// - 責務完了後は完了した個だけを前進させる。他の個が所有する未完了PreRecは維持し、
///   Daily terminalはDailyを持っていた優先度群が同時に次責務へ進む。全体を一旦Dailyへ収束させる設計にはしない。
/// - PreRec OFF / Daily ON時は全録画優先度をDaily表示fallbackとする。両方OFFならSystem EPG表示責務なし。
/// - ④: Daily未配置なら①/②を再評価し、残る優先枠はDaily保留責務を持つ。保留は取得区間ではない。
/// - Daily lifecycle / 実行計画 / 永続SystemDailyEpg行はEpgSchedulerの正本を維持する。
/// </summary>
public sealed class SystemEpgResponsibilityPlanService
{
    public static bool IsPreRecordEnabled(IniSettingsService settings) => settings.EpgPreRecordMinutes > 0;

    private readonly object _dailyGate = new();
    private readonly Dictionary<DateTime, DailyEpgRunState> _dailyStates = new();
    private readonly IniSettingsService _ini;
    private readonly Database _db;
    private readonly object _ownershipGate = new();

    // EpgScheduler alone plans/commits Daily. Responsibility selection consumes that same state.
    internal void UpdateDailyState(DailyEpgRunState state)
    {
        lock (_dailyGate)
        {
            foreach (var date in _dailyStates.Keys.Where(d => d < DateTime.Today || d > DateTime.Today.AddDays(1)).ToArray())
                _dailyStates.Remove(date);
            _dailyStates[state.Date.Date] = state;
        }
    }

    private SystemEpgDailyResponsibility? GetDailyResponsibility(DateTime now)
    {
        if (!_ini.EpgEnabled) return null;
        lock (_dailyGate)
        {
            var state = _dailyStates.Values
                .Where(s => s.Date.Date >= now.Date && s.Config.Enabled
                    && s.Config.CanonicalHour == _ini.EpgHour && s.Config.CanonicalMinute == _ini.EpgMinute
                    && s.Config.Depth == SettingsDefaults.NormalizeEpgDepth(_ini.EpgDepth)
                    && s.State is DailyEpgRunPhase.Planned or DailyEpgRunPhase.Running)
                .OrderBy(s => s.Date).FirstOrDefault();
            if (state is null) return null;
            var pending = state.Placement == DailyEpgPlacementState.Unplaced;
            return new(state.CanonicalStart, state.PlannedStart, state.PlannedEnd, state.MovementDeadline, pending, state.State == DailyEpgRunPhase.Running);
        }
    }
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;
    private readonly ChannelServiceAccessPolicy _serviceAccess;

    public SystemEpgResponsibilityPlanService(
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles,
        ChannelServiceAccessPolicy serviceAccess,
        Database db)
    {
        _db = db;
        _ini = ini;
        _tunerProfiles = tunerProfiles;
        _serviceAccess = serviceAccess;
    }

    // All consumers (reservation list and wake planning included) read the last
    // committed logical owners. Reading must never allocate or delete an owner.
    public SystemEpgResponsibilityPlan Build(IReadOnlyList<Reservation> reservations, DateTime now)
        => BuildCore(reservations, now, advanceOwnership: false);

    // Calculation never mutates SQLite. The common allocation route alone
    // prepares a proposed ownership set and publishes it after its PreRec
    // reservation rows have converged.
    internal SystemEpgResponsibilityPlan Prepare(IReadOnlyList<Reservation> reservations, DateTime now)
        => BuildCore(reservations, now, advanceOwnership: true);

    internal bool Publish(SystemEpgResponsibilityPlan prepared)
    {
        if (!prepared.IsPrepared)
            throw new InvalidOperationException("Only a prepared System EPG plan can be published.");

        lock (_ownershipGate)
        {
            using var con = _db.Open();
            using var tx = con.BeginTransaction();
            var prior = new Dictionary<string, (string Group, int ParentId)>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT priority_name, group_name, parent_reservation_id FROM system_epg_priority_ownership";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    prior[reader.GetString(0)] = (reader.GetString(1), reader.GetInt32(2));
            }
            var owners = prepared.ProposedOwners;
            var changed = false;
            // Delete changed owners before insert, so UNIQUE(parent_id) is
            // preserved when a completed priority advances to a new parent.
            foreach (var previous in prior)
            {
                if (owners.TryGetValue(previous.Key, out var current) && current == previous.Value)
                    continue;
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM system_epg_priority_ownership WHERE priority_name=$name";
                cmd.Parameters.AddWithValue("$name", previous.Key);
                changed |= cmd.ExecuteNonQuery() != 0;
            }
            foreach (var current in owners)
            {
                if (prior.TryGetValue(current.Key, out var previous) && previous == current.Value)
                    continue;
                using var cmd = con.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO system_epg_priority_ownership(priority_name,group_name,parent_reservation_id) VALUES ($name,$group,$parent)";
                cmd.Parameters.AddWithValue("$name", current.Key);
                cmd.Parameters.AddWithValue("$group", current.Value.Group);
                cmd.Parameters.AddWithValue("$parent", current.Value.ParentId);
                changed |= cmd.ExecuteNonQuery() != 0;
            }
            tx.Commit();
            return changed;
        }
    }

    // A reader must not observe ownership saved before its executable PreRec rows
    // have been registered/retired. This is a synchronization gate, not a second
    // responsibility store; SQLite remains the only persisted ownership source.
    public T ReadCommitted<T>(Func<T> reader)
    {
        lock (_ownershipGate) return reader();
    }

    internal T CommitWithReservationProjection<T>(Func<T> writer)
    {
        lock (_ownershipGate) return writer();
    }

    private SystemEpgResponsibilityPlan BuildCore(
        IReadOnlyList<Reservation> reservations, DateTime now, bool advanceOwnership)
    {
        var recordingTuners = _tunerProfiles
            .Where(t => !string.Equals(IniSettingsService.NormalizeTunerRole(t.Role), "Viewing", StringComparison.OrdinalIgnoreCase))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => NormalizeGroup(t.Group), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => LogicalPriorityOrder(t.Name))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var daily = GetDailyResponsibility(now);
        var nextDailyStart = daily?.Start ?? ResolveNextDailyStart(now);
        if (recordingTuners.Count == 0)
        {
            // Empty ownership is committed only after executable PreRec rows are reconciled.
            return new SystemEpgResponsibilityPlan(
                Array.Empty<SystemEpgPreRecordResponsibility>(),
                recordingTuners,
                nextDailyStart,
                Array.Empty<string>(), daily) { IsPrepared = advanceOwnership };
        }

        if (!IsPreRecordEnabled(_ini))
        {
            // Empty ownership is committed only after executable PreRec rows are reconciled.
            var allDailyFallbackTuners = _ini.EpgEnabled
                ? recordingTuners.Select(t => t.Name).ToArray()
                : Array.Empty<string>();
            return new SystemEpgResponsibilityPlan(
                Array.Empty<SystemEpgPreRecordResponsibility>(),
                recordingTuners,
                nextDailyStart,
                allDailyFallbackTuners, daily) { IsPrepared = advanceOwnership };
        }

        var preMin = SettingsDefaults.ResolveEnabledEpgPreRecordMinutes(_ini.EpgPreRecordMinutes);
        var duration = TimeSpan.FromMinutes(Math.Max(1,
            (int)Math.Ceiling(EpgDurationPolicy.BaseSecondsForDepth(_ini.EpgDepth) / 60.0)));

        var completedParents = reservations
            .Where(IsPreRecordRow)
            .Where(r => r.Status == ReservationStatus.Completed)
            .Where(r => r.SourceRuleId.HasValue)
            .Select(r => r.SourceRuleId!.Value)
            .ToHashSet();

        var activeIntentParents = reservations
            .Where(IsPreRecordRow)
            .Where(r => r.Status is ReservationStatus.Scheduled or ReservationStatus.Recording)
            .Where(r => r.SourceRuleId.HasValue)
            .Where(r => r.Status == ReservationStatus.Recording || (r.StartTime <= now && r.EndTime > now))
            .Select(r => r.SourceRuleId!.Value)
            .ToHashSet();

        var channelSnapshot = _serviceAccess.Capture();
        var candidates = reservations
            .Where(r => r.Source is ReservationSource.Manual or ReservationSource.KeywordSearch or ReservationSource.Keyword)
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .Where(r => r.IsEnabled && !r.IsConflicted)
            .Where(r => !r.IsUserChain || !r.UserChainPreviousId.HasValue)
            .Where(r => !completedParents.Contains(r.Id))
            .Select(r =>
            {
                var group = ResolveGroup(r, recordingTuners);
                var start = r.StartTime.AddMinutes(-preMin);
                var end = start.Add(duration);
                return new ParentWindow(
                    r,
                    group,
                    start,
                    end,
                    activeIntentParents.Contains(r.Id));
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Group))
            .Where(x => channelSnapshot.IsOperationalForRecording(x.Parent, recordingTuners))
            .Where(x => x.IsActiveIntent || x.Start > now)
            .ToList();

        if (candidates.Count == 0)
        {
            // No outstanding PreRec responsibility remains. Retire its persisted
            // owners as one canonical update rather than reviving stale ownership.
            // Empty ownership is committed only after executable PreRec rows are reconciled.
            var noCandidateDailyFallbackTuners = _ini.EpgEnabled
                ? recordingTuners.Select(t => t.Name).ToArray()
                : Array.Empty<string>();
            return new SystemEpgResponsibilityPlan(
                Array.Empty<SystemEpgPreRecordResponsibility>(),
                recordingTuners,
                nextDailyStart,
                noCandidateDailyFallbackTuners, daily) { IsPrepared = advanceOwnership };
        }

        var nearHorizon = now.AddHours(3);
        var selected = new List<SystemEpgPreRecordResponsibility>();

        // SQLite is the single authoritative identity for logical owners. Only the
        // writer may advance them. A reader filters persisted owners, but never
        // allocates new parents or anticipates the writer's next decision.
        Dictionary<string, (string Group, int ParentId)>? proposed = null;
        lock (_ownershipGate)
        {
            using var con = _db.Open();
            var prior = new Dictionary<string, (string Group, int ParentId)>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT priority_name, group_name, parent_reservation_id FROM system_epg_priority_ownership";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    prior[reader.GetString(0)] = (reader.GetString(1), reader.GetInt32(2));
            }
            var owners = new Dictionary<string, (string Group, int ParentId)>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in recordingTuners.Select(t => NormalizeGroup(t.Group))
                         .Where(g => !string.IsNullOrWhiteSpace(g))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var groupPriorities = recordingTuners
                    .Where(t => string.Equals(NormalizeGroup(t.Group), group, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => LogicalPriorityOrder(t.Name))
                    .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (groupPriorities.Count == 0) continue;

                // Preserve owners in other configured physical groups first.
                // CATV can tune a GR/BSCS rebroadcast, but must not steal that
                // parent's already-committed individual responsibility.
                var reservedOutsideGroup = prior
                    .Where(p => !string.Equals(p.Value.Group, group, StringComparison.OrdinalIgnoreCase))
                    .Where(p => recordingTuners.Any(t =>
                        string.Equals(t.Name, p.Key, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(NormalizeGroup(t.Group), p.Value.Group, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Value.ParentId).ToHashSet();
                var groupCandidates = candidates
                    .Where(x => !owners.Values.Any(v => v.ParentId == x.Parent.Id))
                    .Where(x => !reservedOutsideGroup.Contains(x.Parent.Id))
                    .Where(x => groupPriorities.Any(t => channelSnapshot.CanTune(t, x.Parent)))
                    .ToList();
                var stage1 = groupCandidates
                    .Where(x => x.IsActiveIntent || x.Parent.StartTime <= nearHorizon)
                    .OrderByDescending(x => x.IsActiveIntent)
                    .ThenBy(x => x.Parent.StartTime)
                    .ThenBy(x => x.Parent.Id)
                    .GroupBy(x => x.Parent.Id).Select(g => g.First()).ToList();
                // ② is a single wave-wide responsibility; never one per idle tuner.
                var target = stage1.Count > 0 ? stage1 : groupCandidates
                    .OrderBy(x => x.Parent.StartTime)
                    .ThenBy(x => x.Parent.Id)
                    .Take(1).ToList();

                // First preserve each owner's own still-eligible responsibility.
                foreach (var tuner in groupPriorities)
                {
                    if (!prior.TryGetValue(tuner.Name, out var previous)
                        || !string.Equals(previous.Group, group, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var candidate = (advanceOwnership ? target : groupCandidates)
                        .FirstOrDefault(x => x.Parent.Id == previous.ParentId);
                    if (candidate is null || !channelSnapshot.CanTune(tuner, candidate.Parent)) continue;
                    owners[tuner.Name] = (group, candidate.Parent.Id);
                }

                // Advance only on an allocation-route commit. UI/Wake reads
                // intentionally do not predict assignments for vacant priorities.
                if (advanceOwnership)
                {
                    foreach (var candidate in target)
                    {
                        if (owners.Values.Any(v => v.ParentId == candidate.Parent.Id)) continue;
                        var tuner = groupPriorities.FirstOrDefault(t => !owners.ContainsKey(t.Name)
                            && channelSnapshot.CanTune(t, candidate.Parent));
                        if (tuner is null) continue;
                        owners[tuner.Name] = (group, candidate.Parent.Id);
                    }
                }

                // While the synchronized Daily run is active, logical PreRec owners
                // remain recorded but do not supersede any of the six ③ responsibilities.
                if (daily?.IsRunning == true) continue;
                foreach (var tuner in groupPriorities)
                {
                    if (!owners.TryGetValue(tuner.Name, out var owner)) continue;
                    var candidate = groupCandidates.First(x => x.Parent.Id == owner.ParentId);
                    // A read-only consumer may expose ①/② only when an actual
                    // persisted child is visible in the same reservation snapshot.
                    // When Daily ends before its PreRec refresh commits, the
                    // priority remains on the real Daily fallback rather than
                    // a synthetic/missing PreRec. Prepare can select a candidate
                    // without a child because the route creates it before Publish.
                    if (!advanceOwnership && !reservations.Any(r => IsPreRecordRow(r)
                        && r.SourceRuleId == owner.ParentId
                        && (r.Status is ReservationStatus.Scheduled or ReservationStatus.Recording)
                        && (r.Status == ReservationStatus.Recording || r.EndTime > now)))
                        continue;
                    if (!CanOwnPreRecordBeforeDaily(candidate, daily))
                        continue;
                    selected.Add(new SystemEpgPreRecordResponsibility(
                        candidate.Parent.Id, candidate.Group, candidate.Start, candidate.End, tuner.Name));
                }
            }

            if (advanceOwnership)
                proposed = owners;
        }

        var preRecordPriorityNames = selected
            .Select(x => x.PriorityName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dailyFallbackTuners = _ini.EpgEnabled
            ? recordingTuners
                .Where(t => !preRecordPriorityNames.Contains(t.Name))
                .Select(t => t.Name)
                .ToArray()
            : Array.Empty<string>();

        return new SystemEpgResponsibilityPlan(
            selected
                .OrderBy(x => NormalizeGroup(x.Group), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => LogicalPriorityOrder(x.PriorityName))
                .ThenBy(x => x.Start)
                .ThenBy(x => x.ParentReservationId)
                .ToList(),
            recordingTuners,
            nextDailyStart,
            dailyFallbackTuners, daily)
        {
            IsPrepared = advanceOwnership,
            ProposedOwners = proposed ?? new Dictionary<string, (string Group, int ParentId)>(StringComparer.OrdinalIgnoreCase)
        };
    }

    // Daily placement is the sole scheduling authority. A missing projection row
    // must not convert an occupied Daily slot into an available PreRec slot.
    // Daily reservation integrity is checked at the projection boundary.
    private static bool CanOwnPreRecordBeforeDaily(
        ParentWindow candidate,
        SystemEpgDailyResponsibility? daily)
    {
        if (daily is not { IsPending: false, Start: not null, End: not null })
            return true;

        // A running PreRec intent is not interrupted by a planning refresh.
        if (candidate.IsActiveIntent)
            return true;

        if (daily.IsRunning)
            return false;

        // PreRec may run only when its complete interval precedes Daily.
        return candidate.End <= daily.Start.Value;
    }

    // Both absolute wake scheduling and crossing detection consume the same
    // eligible reservation boundary definition. A wake arriving immediately
    // after the three-hour threshold must not silently drop that threshold.
    private static IEnumerable<DateTime> ResolveStage1Boundaries(
        IReadOnlyList<Reservation> reservations)
    {
        var completedParents = reservations
            .Where(IsPreRecordRow)
            .Where(r => r.Status == ReservationStatus.Completed)
            .Where(r => r.SourceRuleId.HasValue)
            .Select(r => r.SourceRuleId!.Value).ToHashSet();

        return reservations
            .Where(r => r.Source is ReservationSource.Manual or ReservationSource.KeywordSearch or ReservationSource.Keyword)
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .Where(r => r.IsEnabled && !r.IsConflicted)
            .Where(r => !r.IsUserChain || !r.UserChainPreviousId.HasValue)
            .Where(r => !completedParents.Contains(r.Id))
            .Select(r => r.StartTime.AddHours(-3))
            .Distinct()
            .OrderBy(boundary => boundary);
    }

    public DateTime? ResolveNextStage1Boundary(IReadOnlyList<Reservation> reservations, DateTime now)
        => !IsPreRecordEnabled(_ini) ? null : ResolveStage1Boundaries(reservations)
            .Where(boundary => boundary > now)
            .Select(boundary => (DateTime?)boundary).FirstOrDefault();

    public DateTime? ResolveCrossedStage1Boundary(
        IReadOnlyList<Reservation> reservations, DateTime fromExclusive, DateTime toInclusive)
        => !IsPreRecordEnabled(_ini) ? null : ResolveStage1Boundaries(reservations)
            .Where(boundary => boundary > fromExclusive && boundary <= toInclusive)
            .Select(boundary => (DateTime?)boundary).FirstOrDefault();

    private DateTime? ResolveNextDailyStart(DateTime now)
    {
        if (!_ini.EpgEnabled)
            return null;

        var hour = SettingsDefaults.NormalizeEpgHour(_ini.EpgHour);
        var minute = SettingsDefaults.NormalizeEpgMinute(_ini.EpgMinute);
        var scheduled = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, now.Kind);
        if (scheduled <= now)
            scheduled = scheduled.AddDays(1);

        // 予約一覧上の定時EPG行はWake/準備境界を含め1分前から始まる。
        return scheduled.AddMinutes(-1);
    }

    private static bool IsPreRecordRow(Reservation r)
        => ReservationIntentContract.IsPreRecordEpg(r);

    private static bool IsDailyEpgRow(Reservation r)
        => ReservationIntentContract.IsDailyEpg(r);

    private string ResolveGroup(Reservation reservation, IReadOnlyList<TunerProfile> recordingTuners)
        => NormalizeGroup(_serviceAccess.Capture().ResolveSchedulingGroup(reservation, recordingTuners));

    private static int LogicalPriorityOrder(string? tunerName)
    {
        var raw = (tunerName ?? string.Empty).Trim();
        if (raw.Length <= 1)
            return int.MaxValue;

        var digits = new string(raw.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    private static string NormalizeGroup(string? value)
    {
        var raw = (value ?? string.Empty).Trim().ToUpperInvariant();
        return raw switch
        {
            "BS" or "CS" or "BSCS" or "BS/CS" => "BSCS",
            "GR" => "GR",
            _ => raw
        };
    }

    private sealed record ParentWindow(
        Reservation Parent,
        string Group,
        DateTime Start,
        DateTime End,
        bool IsActiveIntent);
}

public sealed record SystemEpgResponsibilityPlan(
    IReadOnlyList<SystemEpgPreRecordResponsibility> PreRecordResponsibilities,
    IReadOnlyList<TunerProfile> RecordingTuners,
    DateTime? NextDailyStart,
    IReadOnlyList<string> DailyFallbackTunerNames,
    SystemEpgDailyResponsibility? Daily = null)
{
    internal bool IsPrepared { get; init; }
    internal IReadOnlyDictionary<string, (string Group, int ParentId)> ProposedOwners { get; init; }
        = new Dictionary<string, (string Group, int ParentId)>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<int> ParentIds
        => PreRecordResponsibilities.Select(x => x.ParentReservationId).ToHashSet();

    public IReadOnlySet<string> DailyFallbackTuners
        => DailyFallbackTunerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record SystemEpgPreRecordResponsibility(
    int ParentReservationId,
    string Group,
    DateTime Start,
    DateTime End,
    string PriorityName);

/// <summary>Read-only projection of EpgScheduler's committed Daily plan; pending is never an execution interval.</summary>
public sealed record SystemEpgDailyResponsibility(
    DateTime CanonicalStart, DateTime? Start, DateTime? End, DateTime Deadline, bool IsPending, bool IsRunning = false);
