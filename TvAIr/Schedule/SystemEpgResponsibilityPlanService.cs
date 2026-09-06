using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Tuner;

namespace TvAIr.Schedule;

/// <summary>
/// System EPGのうち、録画前EPG確認（PreRec）の次責務候補を決める計画正本。
///
/// DEVELOPER_APPROVAL_REQUIRED:
/// - System EPGの計画・責務・Projection・ON/OFF組合せ・PreRec選択契約は保護領域。
/// - 実装変更、再構築、責務分割/統合、補正経路追加、既存正常経路の置換は、必ず事前の開発者明示承認を得ること。
/// - 「次」「続けて」「進めて」等の通常の進行指示は、この保護領域を変更する承認とはみなさない。
/// - 不具合調査でSystem EPG周辺が疑わしく見えても、ログと実機でこの正本自体の欠陥が確定するまでは変更しない。
/// - 他案件の修正に便乗してSystem EPGを触らない。既存の正常動作を維持することを最優先する。
///
/// CONTRACT:
/// - 「直近」は二軸で扱う。
///   1) 時間軸: 現在から3時間以内の予約イベント群。
///   2) 予約イベント軸: 時間軸候補が一件も無い場合に限り、予約リスト全体の先頭1件。
///      その先頭は10時間先でも「直近予約」とするが、各物理Tunerごとの先頭を拾ってはならない。
/// - PreRec ON時は、時間軸候補を現在Allocationの物理録画Tunerへ投影し、同一Tunerでは最も近い1件だけを持つ。
///   時間軸候補が無い場合だけ、予約リスト全体の先頭1件をPreRec責務にする。
/// - このPlanはPreRec候補選択だけを表示側へ提供する。Dailyの実行計画・永続行・予約一覧の最終表示選択は所有しない。
/// - 予約一覧はDaily ONなら永続化済みDaily行を基本候補として保持し、このPlanが選んだPreRec候補と比較して、
///   各録画Tunerの現在/次のSystem EPGを1件投影する。
/// - PriorityName(T1.. / S1..) は親予約の現在のAllocation結果をそのまま使う。
///   System EPG側で遠い将来の物理Tunerを独自に再割当・固定しない。
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
            .ToList();

        var nextDailyStart = ResolveNextDailyStart(now);
        if (_ini.EpgPreRecordMinutes <= 0 || recordingTuners.Count == 0)
            return new SystemEpgResponsibilityPlan(Array.Empty<SystemEpgPreRecordResponsibility>(), recordingTuners, nextDailyStart);

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

        var tunerByName = recordingTuners
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var dailyRunningTuners = reservations
            .Where(IsDailyEpgRow)
            .Where(r => r.Status == ReservationStatus.Recording)
            .Where(r => !string.IsNullOrWhiteSpace(r.TunerName))
            .Select(r => r.TunerName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = reservations
            .Where(r => r.Source is ReservationSource.Manual or ReservationSource.KeywordSearch or ReservationSource.Keyword)
            .Where(r => r.Status == ReservationStatus.Scheduled)
            .Where(r => r.IsEnabled && !r.IsConflicted)
            .Where(r => !r.IsUserChain || !r.UserChainPreviousId.HasValue)
            .Where(r => !completedParents.Contains(r.Id))
            .Select(r =>
            {
                var group = ResolveGroup(r, recordingTuners);
                var priorityName = ResolvePriorityName(r, group, recordingTuners);
                var start = r.StartTime.AddMinutes(-preMin);
                var end = start.Add(duration);
                return new ParentWindow(
                    r,
                    group,
                    start,
                    end,
                    activeIntentParents.Contains(r.Id),
                    priorityName);
            })
            // Allocation未確定の親をSystem側で勝手にTunerへ固定しない。
            // 次回Allocation確定後の再評価で、時間軸直近または予約リスト全体先頭の候補として選択する。
            .Where(x => !string.IsNullOrWhiteSpace(x.PriorityName) && tunerByName.ContainsKey(x.PriorityName))
            .Where(x => x.IsActiveIntent || x.Start > now)
            .ToList();

        if (candidates.Count == 0)
            return new SystemEpgResponsibilityPlan(Array.Empty<SystemEpgPreRecordResponsibility>(), recordingTuners, nextDailyStart);

        // SYSTEM_EPG_NEAREST_DUAL_AXIS_INVARIANT:
        // 「直近」は二軸であり、各Tunerの未来先頭を独立に先取りする意味ではない。
        // まず現在から3時間以内の予約イベント群を時間軸候補とする。
        // 時間軸候補が一件も無い場合だけ、予約リスト全体の先頭1件を遠距離fallback候補とする。
        // この選択結果はPreRec候補だけを表す。Dailyとの表示順序はReservationPresentationServiceが
        // 永続化済みDaily行と比較して決めるため、ここでDaily表示を置換・抑制しない。
        var activeCandidates = candidates
            .Where(x => x.IsActiveIntent)
            .OrderBy(x => x.Start)
            .ThenBy(x => x.Parent.StartTime)
            .ThenBy(x => x.Parent.Id)
            .ToList();

        var nearHorizon = now.AddHours(3);
        var nearCandidates = candidates
            .Where(x => !x.IsActiveIntent)
            .Where(x => x.Parent.StartTime <= nearHorizon)
            .OrderBy(x => x.Parent.StartTime)
            .ThenBy(x => x.Parent.Id)
            .ToList();

        List<ParentWindow> responsibilityCandidates;
        if (activeCandidates.Count > 0 || nearCandidates.Count > 0)
        {
            // 実行中PreRecと時間軸直近は共存できる。ここでは遠距離fallbackだけを追加しない。
            responsibilityCandidates = activeCandidates
                .Concat(nearCandidates)
                .GroupBy(x => x.Parent.Id)
                .Select(g => g.First())
                .ToList();
        }
        else
        {
            // 実行中/時間軸候補が一件も無い場合だけ「予約リスト全体の先頭1件」を直近予約として扱う。
            // 各Tunerごとの先頭は絶対に拾わない。
            var globalHead = candidates
                .OrderBy(x => x.Parent.StartTime)
                .ThenBy(x => x.Parent.Id)
                .FirstOrDefault();
            responsibilityCandidates = globalHead is null ? new List<ParentWindow>() : new List<ParentWindow> { globalHead };
        }

        var selected = responsibilityCandidates
            // 実行中DailyはそのTunerの現在責務。未来PreRecで上書きしない。
            .Where(x => x.IsActiveIntent || !dailyRunningTuners.Contains(x.PriorityName))
            // 3時間内に複数候補が同一Tunerへ割り当てられている場合は、そのTunerで最も早い1件だけ。
            .GroupBy(x => x.PriorityName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(x => x.IsActiveIntent)
                .ThenBy(x => x.Parent.StartTime)
                .ThenBy(x => x.Parent.Id)
                .First())
            .OrderBy(x => x.Parent.StartTime)
            .ThenBy(x => x.Parent.Id)
            .Select(x => new SystemEpgPreRecordResponsibility(
                x.Parent.Id,
                x.Group,
                x.Start,
                x.End,
                x.PriorityName))
            .ToList();

        return new SystemEpgResponsibilityPlan(selected, recordingTuners, nextDailyStart);
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

    private static string ResolvePriorityName(
        Reservation parent,
        string group,
        IReadOnlyList<TunerProfile> recordingTuners)
    {
        if (string.IsNullOrWhiteSpace(parent.TunerName))
            return string.Empty;

        var match = recordingTuners.FirstOrDefault(t =>
            string.Equals(t.Name, parent.TunerName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeGroup(t.Group), group, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? string.Empty;
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
        bool IsActiveIntent,
        string PriorityName);
}

public sealed record SystemEpgResponsibilityPlan(
    IReadOnlyList<SystemEpgPreRecordResponsibility> PreRecordResponsibilities,
    IReadOnlyList<TunerProfile> RecordingTuners,
    DateTime? NextDailyStart)
{
    public IReadOnlySet<int> ParentIds
        => PreRecordResponsibilities.Select(x => x.ParentReservationId).ToHashSet();
}

public sealed record SystemEpgPreRecordResponsibility(
    int ParentReservationId,
    string Group,
    DateTime Start,
    DateTime End,
    string PriorityName);
