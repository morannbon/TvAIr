using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Tuner;

namespace TvAIr.Schedule;

/// <summary>
/// System EPGのうち、録画前EPG確認（PreRec）とDailyの「次責務」を決める計画正本。
///
/// INVARIANTS:
/// - System EPGの計画・責務・Projection・ON/OFF組合せ・PreRec選択はこのサービスを正本とする。
/// - 責務選択を別経路で再計算・補正せず、呼出側はここで確定したGroup/Start/End/PriorityNameをそのまま投影する。
/// - Daily全体へ一旦収束させず、表示優先度(T1→T2→T3 / S1→S2→S3)ごとに次責務を順次探索する。②だけは放送波単位で最大1件とする。
/// - 仕様変更時はSystem EPG表示、PreRec実行、責務advance、Daily fallbackを同一契約として横断確認する。
///
/// CONTRACT:
/// - 表示上のSystem EPG責務は録画用優先度ごとに最大1件。GR / BSCSは独立に評価する。
/// - PriorityNameは「ユーザーに見せる責務優先度」であり、PreRec実行時の物理Tuner固定ではない。
///   実PreRec workerは従来どおり実行時に同一波の空き録画Tunerを選ぶ。
/// - 各放送波の判定順は①→②→③。
///   ①: 現在から3時間以内（または既に実行窓内）の通常PreRec候補を、表示優先度の先頭から順次割当する。
///   ②: その放送波に①が一件も無い場合だけ、有効予約リストの先頭1件だけを暫定PreRec候補にする。
///      ②はGR最大1件 / BSCS最大1件。先頭を飛ばして後続予約を拾わない。
///   ③: Daily。①/②のPreRec取得枠が、その優先度のDaily開始までに完了できない場合はDailyへfallbackする。
/// - ②対象が①の3時間窓へ入った時点では、その放送波を通常①として再評価し、必要数だけ先頭優先度から展開する。
/// - 責務完了後は次責務を再評価する。通常PreRec完了はその波の順次サーチを前進させ、
///   Daily terminalはDailyを持っていた優先度群が同時に次責務へ進む。全体を一旦Dailyへ収束させる設計にはしない。
/// - PreRec OFF / Daily ON時は全録画優先度をDaily表示fallbackとする。両方OFFならSystem EPG表示責務なし。
/// - Daily lifecycle / 実行計画 / 永続SystemDailyEpg行はEpgSchedulerの正本を維持する。
/// </summary>
public sealed class SystemEpgResponsibilityPlanService
{
    private readonly IniSettingsService _ini;
    private readonly IReadOnlyList<TunerProfile> _tunerProfiles;

    public SystemEpgResponsibilityPlanService(
        IniSettingsService ini,
        IReadOnlyList<TunerProfile> tunerProfiles)
    {
        _ini = ini;
        _tunerProfiles = tunerProfiles;
    }

    public SystemEpgResponsibilityPlan Build(IReadOnlyList<Reservation> reservations, DateTime now)
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

        var nextDailyStart = ResolveNextDailyStart(now);
        if (recordingTuners.Count == 0)
            return new SystemEpgResponsibilityPlan(
                Array.Empty<SystemEpgPreRecordResponsibility>(),
                recordingTuners,
                nextDailyStart,
                Array.Empty<string>());

        if (_ini.EpgPreRecordMinutes <= 0)
        {
            var allDailyFallbackTuners = _ini.EpgEnabled
                ? recordingTuners.Select(t => t.Name).ToArray()
                : Array.Empty<string>();
            return new SystemEpgResponsibilityPlan(
                Array.Empty<SystemEpgPreRecordResponsibility>(),
                recordingTuners,
                nextDailyStart,
                allDailyFallbackTuners);
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

        var activeDailyRowsByPriority = _ini.EpgEnabled
            ? reservations
                .Where(IsDailyEpgRow)
                .Where(r => r.Status is ReservationStatus.Scheduled or ReservationStatus.Recording)
                .Where(r => r.EndTime > now)
                .Where(r => !string.IsNullOrWhiteSpace(r.TunerName))
                .GroupBy(r => r.TunerName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderBy(r => r.Status == ReservationStatus.Recording ? 0 : 1)
                        .ThenBy(r => r.StartTime)
                        .ThenBy(r => r.Id)
                        .First(),
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Reservation>(StringComparer.OrdinalIgnoreCase);

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
            .Where(x => x.IsActiveIntent || x.Start > now)
            .ToList();

        if (candidates.Count == 0)
        {
            var noCandidateDailyFallbackTuners = _ini.EpgEnabled
                ? recordingTuners.Select(t => t.Name).ToArray()
                : Array.Empty<string>();
            return new SystemEpgResponsibilityPlan(
                Array.Empty<SystemEpgPreRecordResponsibility>(),
                recordingTuners,
                nextDailyStart,
                noCandidateDailyFallbackTuners);
        }

        var nearHorizon = now.AddHours(3);
        var selected = new List<SystemEpgPreRecordResponsibility>();

        foreach (var group in recordingTuners
                     .Select(t => NormalizeGroup(t.Group))
                     .Where(g => !string.IsNullOrWhiteSpace(g))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var groupPriorities = recordingTuners
                .Where(t => string.Equals(NormalizeGroup(t.Group), group, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => LogicalPriorityOrder(t.Name))
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (groupPriorities.Count == 0)
                continue;

            var groupCandidates = candidates
                .Where(x => string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (groupCandidates.Count == 0)
                continue;

            // ① 通常PreRec。
            // 同一波で①が成立している間は②を作らない。表示優先度は物理Allocationから独立し、
            // T1→T2→T3 / S1→S2→S3 の順で必要数だけ展開する。
            var stage1 = groupCandidates
                .Where(x => x.IsActiveIntent || x.Parent.StartTime <= nearHorizon)
                .OrderByDescending(x => x.IsActiveIntent)
                .ThenBy(x => x.Parent.StartTime)
                .ThenBy(x => x.Parent.Id)
                .GroupBy(x => x.Parent.Id)
                .Select(g => g.First())
                .ToList();

            if (stage1.Count > 0)
            {
                var count = Math.Min(stage1.Count, groupPriorities.Count);
                for (var i = 0; i < count; i++)
                {
                    var candidate = stage1[i];
                    var priority = groupPriorities[i].Name;
                    if (CanOwnPreRecordBeforeDaily(candidate, priority, activeDailyRowsByPriority))
                    {
                        selected.Add(new SystemEpgPreRecordResponsibility(
                            candidate.Parent.Id,
                            candidate.Group,
                            candidate.Start,
                            candidate.End,
                            priority));
                    }
                }
                continue;
            }

            // ② 暫定PreRec。
            // ①が波内に一件も無い時だけ、予約リストの有効先頭1件を候補にする。
            // ②の所有者は波内の最優先表示枠。③より後ろへ食い込むなら③へfallbackする。
            var stage2 = groupCandidates
                .OrderBy(x => x.Parent.StartTime)
                .ThenBy(x => x.Parent.Id)
                .FirstOrDefault();
            if (stage2 is null)
                continue;

            var stage2Priority = groupPriorities[0].Name;
            if (CanOwnPreRecordBeforeDaily(stage2, stage2Priority, activeDailyRowsByPriority))
            {
                selected.Add(new SystemEpgPreRecordResponsibility(
                    stage2.Parent.Id,
                    stage2.Group,
                    stage2.Start,
                    stage2.End,
                    stage2Priority));
            }
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
            dailyFallbackTuners);
    }

    private static bool CanOwnPreRecordBeforeDaily(
        ParentWindow candidate,
        string priorityName,
        IReadOnlyDictionary<string, Reservation> activeDailyRowsByPriority)
    {
        if (!activeDailyRowsByPriority.TryGetValue(priorityName, out var dailyRow))
            return true;

        // 実行中PreRecは既に開始済みの現在責務なので維持する。
        if (candidate.IsActiveIntent)
            return true;

        // Dailyが実行中なら、その優先度の現在責務はDaily。
        if (dailyRow.Status == ReservationStatus.Recording)
            return false;

        // PreRecは取得枠全体がDaily開始までに完結できる時だけ先行させる。
        return candidate.End <= dailyRow.StartTime;
    }

    public DateTime? ResolveNextStage1Boundary(IReadOnlyList<Reservation> reservations, DateTime now)
    {
        if (_ini.EpgPreRecordMinutes <= 0)
            return null;

        var completedParents = reservations
            .Where(IsPreRecordRow)
            .Where(r => r.Status == ReservationStatus.Completed)
            .Where(r => r.SourceRuleId.HasValue)
            .Select(r => r.SourceRuleId!.Value)
            .ToHashSet();

        return reservations
            .Where(r => r.Source is ReservationSource.Manual or ReservationSource.KeywordSearch or ReservationSource.Keyword)
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .Where(r => r.IsEnabled && !r.IsConflicted)
            .Where(r => !r.IsUserChain || !r.UserChainPreviousId.HasValue)
            .Where(r => !completedParents.Contains(r.Id))
            .Select(r => r.StartTime.AddHours(-3))
            .Where(boundary => boundary > now)
            .OrderBy(boundary => boundary)
            .Select(boundary => (DateTime?)boundary)
            .FirstOrDefault();
    }

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

    private static string ResolveGroup(Reservation reservation, IReadOnlyList<TunerProfile> recordingTuners)
        => NormalizeGroup(ReservationTunerGroupResolver.Resolve(reservation, recordingTuners));

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
    IReadOnlyList<string> DailyFallbackTunerNames)
{
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
