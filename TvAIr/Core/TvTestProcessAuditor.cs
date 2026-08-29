using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace TvAIr.Core;

/// <summary>
/// TvAIr管理下のTVTest系プロセスだけを監査する。
/// 管理外TVTestとそのチューナーは責任範囲外であり、列挙結果・DID・BonDriverを制御判断へ使わない。
/// 物理PT3チューナーの選択・包含的利用・競合処理・再配置はBonDriver_PTx/PT3側の責務であり、
/// TvAIrはその内部判断へ介入しない。TvAIrは自分に割り当てられた論理Tuner/DIDへ通常の取得要求を出し、
/// 管理外TVTestの存在を理由に回避・待機・譲歩・候補除外をしてはならない。
/// </summary>
public static class TvTestProcessAuditor
{
    // release_contract: ActivityKeeper用TVTestはDirectRecorder録画のSleepGuard向け目印であり、
    // 録画本体ではない。同一スキャン内でプロセスごとにNOTICEを出すとログ量が増えるため、
    // スキャン単位で1行に集約し、同一内容は短時間抑止する。
    private static readonly ConcurrentDictionary<string, DateTime> ActivityKeeperSummaryLastUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ActivityKeeperSummaryWindow = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ActivityKeeperSummaryRetention = TimeSpan.FromMinutes(10);

    private static readonly Regex DidRegex = new(@"/DID\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BonDriverRegex = new(@"/d\s+""?([^""\s]+\.dll)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void EmitSnapshot(LogRepository log, string phase)
    {
        _ = CaptureSnapshot(log, phase, emitLegacyEvents: true);
    }

    /// <summary>
    /// TvAIr管理下のTVTest/EPGプロセス確認用スナップショット。
    /// 監査ログを出しながら、呼び出し元が対象PIDを安全に判定するために使う。
    /// </summary>
    public static ProcessSnapshot Capture(LogRepository log, string phase, bool emitLegacyEvents = false)
        => CaptureSnapshot(log, phase, emitLegacyEvents);

    /// <summary>
    /// TvAIr設定内の視聴専用DIDだけを保護判定する。
    /// 管理外TVTestのプロセス状態は参照しない。
    /// </summary>
    public static ViewingProtectionSnapshot EmitViewingProtectionAudit(
        LogRepository log,
        string reason,
        string? reservationId,
        string? targetDid,
        string? targetBonDriver,
        IEnumerable<string> protectedViewingDids,
        bool blockOnSameDid)
    {
        // 管理外TVTestとそのチューナーはTvAIrの責任範囲外。
        // プロセス列挙・DID/BonDriver推定・衝突回避には使用せず、
        // TvAIr設定内で明示された視聴専用DIDだけを内部契約として保護する。
        // 物理PT3側の競合・再配置はBonDriver_PTx/PT3へ委ねる。ここで外部利用を推測して
        // 別Tunerへ逃がす、待つ、譲る、失敗扱いにする等のHost独自調停を追加してはならない。
        var protectedList = protectedViewingDids
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var targetIsViewingRole = !string.IsNullOrWhiteSpace(targetDid)
            && protectedList.Any(d => string.Equals(d, targetDid, StringComparison.OrdinalIgnoreCase));

        log.Add("VIEWING_ROLE_PROTECTION_DECISION", reason,
            $"result={(targetIsViewingRole ? "BLOCK" : "OK")} reservation={SafeValue(reservationId)} " +
            $"targetDid={SafeValue(targetDid)} targetBonDriver={SafeValue(targetBonDriver)} " +
            $"targetIsConfiguredViewingRole={targetIsViewingRole} protectedViewingDids={FormatList(protectedList)} " +
            "unmanagedExternalTvTest=out_of_scope externalProcessInspection=disabled rule=release_contract");

        return new ViewingProtectionSnapshot(
            Processes: Array.Empty<TvTestProcessInfo>(),
            ProtectedViewingDids: protectedList,
            ShouldBlock: targetIsViewingRole,
            ExternalSameDid: false,
            TargetIsViewingRole: targetIsViewingRole,
            UnknownLiveDid: false);
    }

    private static ProcessSnapshot CaptureSnapshot(LogRepository log, string phase, bool emitLegacyEvents)
    {
        var processes = new List<TvTestProcessInfo>();
        var activityKeepers = new List<ActivityKeeperNoticeItem>();
        try
        {
            var managedEntries = TvAirManagedProcessRegistry.GetAll();
            foreach (var managed in managedEntries)
            {
                Process? proc = null;
                try
                {
                    proc = Process.GetProcessById(managed.ProcessId);
                    if (proc.HasExited)
                    {
                        TvAirManagedProcessRegistry.Unregister(managed.ProcessId);
                        continue;
                    }

                    var cmd = TryGetCommandLine(managed.ProcessId) ?? string.Empty;
                    var did = string.IsNullOrWhiteSpace(managed.Did) ? ExtractDid(cmd) : managed.Did;
                    var bon = string.IsNullOrWhiteSpace(managed.BonDriverFileName) ? ExtractBonDriverFileName(cmd) : managed.BonDriverFileName;
                    var managedActivityOnly = managed.IsActivityOnly;
                    var managedViewer = managed.IsViewer;
                    var isEpg = managedActivityOnly && ContainsToken(managed.ActivityReason ?? string.Empty, "EPG");
                    var isRecording = !managedActivityOnly && !managedViewer;
                    var info = new TvTestProcessInfo(managed.ProcessId, proc.ProcessName, cmd, false, isRecording, isEpg, true, did, bon);
                    processes.Add(info);

                    log.Add("TVAIR_MANAGED_TVTEST_PROCESS", phase,
                        $"pid={managed.ProcessId} name={proc.ProcessName} purpose={managed.Purpose} " +
                        $"reservation={(managed.ReservationId.HasValue ? "R" + managed.ReservationId.Value : "-")} " +
                        $"reason={SafeValue(managed.ActivityReason)} service={SafeValue(managed.ActivityServiceName)} " +
                        $"did={SafeValue(did)} bonDriver={SafeValue(bon)} ownershipSource=managed_registry rule=release_contract");

                    if (managedActivityOnly)
                    {
                        activityKeepers.Add(new ActivityKeeperNoticeItem(managed.ProcessId, managed.ActivityServiceName, managed.ActivityReason));
                    }
                }
                catch (ArgumentException)
                {
                    TvAirManagedProcessRegistry.Unregister(managed.ProcessId);
                }
                catch (Exception ex)
                {
                    log.Add("TVAIR_MANAGED_PROCESS_SCAN", "WARN", $"phase={phase} pid={managed.ProcessId} error={ex.Message}");
                }
                finally
                {
                    proc?.Dispose();
                }
            }

            log.Add("TVAIR_MANAGED_PROCESS_SCAN", phase,
                $"result=OK managedCount={processes.Count} unmanagedExternalTvTest=out_of_scope externalProcessEnumeration=disabled rule=release_contract");
        }
        catch (Exception ex)
        {
            log.Add("TVAIR_MANAGED_PROCESS_SCAN", "ERROR", $"phase={phase} error={ex.Message}");
        }
        EmitActivityKeeperSummary(log, phase, activityKeepers);
        return new ProcessSnapshot(processes);
    }

    private static void EmitActivityKeeperSummary(LogRepository log, string phase, IReadOnlyList<ActivityKeeperNoticeItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ordered = items
            .OrderBy(i => SafeValue(i.ServiceName), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ProcessId)
            .ToList();
        var summary = string.Join(";", ordered.Select(i => $"pid={i.ProcessId}/service={SafeValue(i.ServiceName)}/reason={SafeValue(i.ActivityReason)}"));
        var key = $"{phase}|{summary}";
        var now = DateTime.UtcNow;
        if (ActivityKeeperSummaryLastUtc.TryGetValue(key, out var last) && now - last < ActivityKeeperSummaryWindow)
        {
            return;
        }

        ActivityKeeperSummaryLastUtc[key] = now;
        // 長期連続稼働保護: phase/PIDを含むログ抑制キーは実行ごとに変化するため、
        // 抑制窓を十分に過ぎた項目をここで破棄し、録画・EPG回数に比例して増やさない。
        foreach (var pair in ActivityKeeperSummaryLastUtc)
        {
            if (now - pair.Value > ActivityKeeperSummaryRetention)
                ActivityKeeperSummaryLastUtc.TryRemove(pair.Key, out _);
        }
        log.Add("RECORDER_ACTIVITY_NOTICE", phase,
            $"result=INFO count={ordered.Count} role=ActivityKeeper " +
            "note=activity_keeper_tvtest_not_recording_route directRecorderFailure=False " +
            $"action=sleepguard_marker_only summary={summary} " +
            "rule=release_contract");
    }

    private static bool ContainsToken(string text, string token)
        => text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractDid(string commandLine)
    {
        var m = DidRegex.Match(commandLine ?? string.Empty);
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static string? ExtractBonDriverFileName(string commandLine)
    {
        var m = BonDriverRegex.Match(commandLine ?? string.Empty);
        return m.Success ? Path.GetFileName(m.Groups[1].Value.Trim().Trim('"')) : null;
    }

    private static string? TryGetCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid);
            foreach (ManagementObject obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch { }
        return null;
    }


    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= max ? value : value[..max] + "...";
    }

    private static string SafeValue(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string FormatList(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return list.Count == 0 ? "-" : string.Join(",", list);
    }

}

public sealed record TvTestProcessInfo(
    int ProcessId,
    string ProcessName,
    string CommandLine,
    bool IsLiveViewing,
    bool IsRecording,
    bool IsEpgLike,
    bool IsTvAirManaged,
    string? Did,
    string? BonDriverFileName);

internal sealed record ActivityKeeperNoticeItem(int ProcessId, string? ServiceName, string? ActivityReason);

public sealed record ProcessSnapshot(IReadOnlyList<TvTestProcessInfo> Processes);

public sealed record ViewingProtectionSnapshot(
    IReadOnlyList<TvTestProcessInfo> Processes,
    IReadOnlyList<string> ProtectedViewingDids,
    bool ShouldBlock,
    bool ExternalSameDid,
    bool TargetIsViewingRole,
    bool UnknownLiveDid);
