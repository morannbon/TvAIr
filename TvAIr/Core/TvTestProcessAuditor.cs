using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace TvAIr.Core;

/// <summary>
/// TVTest/LIVETest 系プロセスへ干渉していないか確認するための監査ログ。
/// プロセスを触らず、列挙とコマンドライン取得だけを行う。
/// </summary>
public static class TvTestProcessAuditor
{
    // v0.5.83: 外部LIVETestのDIDが読めないだけの既知状態は、制御上は
    // 録画用スロットを1本空けるガードとして扱う。毎回WARNにすると
    // 本当に危険な視聴用DID衝突/BLOCKと混ざるため、短時間は集約してINFO相当に落とす。
    private static readonly ConcurrentDictionary<string, DateTime> ExternalLiveUnknownLogLastUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ExternalLiveUnknownLogWindow = TimeSpan.FromSeconds(60);

    // v0.5.85: ActivityKeeper用TVTestはDirectRecorder録画のSleepGuard向け目印であり、
    // 録画本体ではない。同一スキャン内でプロセスごとにNOTICEを出すとログ量が増えるため、
    // スキャン単位で1行に集約し、同一内容は短時間抑止する。
    private static readonly ConcurrentDictionary<string, DateTime> ActivityKeeperSummaryLastUtc = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ActivityKeeperSummaryWindow = TimeSpan.FromSeconds(120);

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
    /// 録画/停止/EPGの直前直後に、後から起動された LIVETest/外部TVTest を監査する。
    /// 起動時検出だけに依存しないための v0.3.85 追加ログ。
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
        var protectedList = protectedViewingDids
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshot = CaptureSnapshot(log, $"VIEWING_PROTECTION:{reason}", emitLegacyEvents: false);
        var live = snapshot.Processes.Where(p => p.IsLiveViewing).ToList();
        var liveDids = live
            .Select(p => string.IsNullOrWhiteSpace(p.Did) ? "unknown" : p.Did!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var targetIsViewingRole = !string.IsNullOrWhiteSpace(targetDid)
            && protectedList.Any(d => string.Equals(d, targetDid, StringComparison.OrdinalIgnoreCase));
        var sameDid = !string.IsNullOrWhiteSpace(targetDid)
            && live.Any(p => !string.IsNullOrWhiteSpace(p.Did)
                && string.Equals(p.Did, targetDid, StringComparison.OrdinalIgnoreCase));
        var unknownLiveDid = live.Any(p => string.IsNullOrWhiteSpace(p.Did));
        var shouldBlock = targetIsViewingRole || (blockOnSameDid && sameDid);

        var unknownOnly = unknownLiveDid && !shouldBlock && !sameDid && !targetIsViewingRole;
        var reasonText = targetIsViewingRole
            ? "target_is_viewing_role"
            : sameDid
                ? "external_live_same_did"
                : unknownLiveDid
                    ? "external_live_did_unknown"
                    : "target_not_protected";

        if (unknownOnly)
        {
            var key = $"{reason}|{SafeValue(targetDid)}|{SafeValue(targetBonDriver)}|{FormatList(liveDids)}";
            var now = DateTime.UtcNow;
            var emit = true;
            if (ExternalLiveUnknownLogLastUtc.TryGetValue(key, out var last) && now - last < ExternalLiveUnknownLogWindow)
            {
                emit = false;
            }
            if (emit)
            {
                ExternalLiveUnknownLogLastUtc[key] = now;
                log.Add("VIEWING_PROTECTION_DECISION", reason,
                    $"result=INFO reservation={SafeValue(reservationId)} targetDid={SafeValue(targetDid)} " +
                    $"targetBonDriver={SafeValue(targetBonDriver)} protectedDidHit=False externalSameDid=False " +
                    $"externalViewingDetected={(live.Count > 0)} reason={reasonText} " +
                    $"action=recording_slot_guard_only liveViewingCount={live.Count} protectedViewingDids={FormatList(protectedList)} " +
                    $"externalLiveDids={FormatList(liveDids)} rule=v0.5.83_external_live_unknown_log_downgraded");
            }

            return new ViewingProtectionSnapshot(snapshot.Processes, protectedList, shouldBlock, sameDid, targetIsViewingRole, unknownLiveDid);
        }

        log.Add("VIEWING_PROTECTION_SCAN", reason,
            $"reservation={SafeValue(reservationId)} found={snapshot.Processes.Count} liveViewingCount={live.Count} " +
            $"targetDid={SafeValue(targetDid)} targetBonDriver={SafeValue(targetBonDriver)} " +
            $"protectedViewingDids={FormatList(protectedList)} externalLiveDids={FormatList(liveDids)} " +
            $"externalLive={FormatLiveProcesses(live)}");

        var result = shouldBlock ? "BLOCK" : "OK";
        log.Add("VIEWING_PROTECTION_DECISION", reason,
            $"result={result} reservation={SafeValue(reservationId)} targetDid={SafeValue(targetDid)} " +
            $"targetBonDriver={SafeValue(targetBonDriver)} protectedDidHit={targetIsViewingRole} externalSameDid={sameDid} " +
            $"externalViewingDetected={(live.Count > 0)} reason={reasonText}");

        return new ViewingProtectionSnapshot(snapshot.Processes, protectedList, shouldBlock, sameDid, targetIsViewingRole, unknownLiveDid);
    }

    private static ProcessSnapshot CaptureSnapshot(LogRepository log, string phase, bool emitLegacyEvents)
    {
        var processes = new List<TvTestProcessInfo>();
        var activityKeepers = new List<ActivityKeeperNoticeItem>();
        try
        {
            var targets = Process.GetProcesses()
                .Where(p => IsTvTestLikeProcessName(p.ProcessName))
                .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Id)
                .ToList();

            log.Add("TVTEST_PROCESS_SCAN", phase, $"count={targets.Count}");

            foreach (var proc in targets)
            {
                try
                {
                    var cmd = TryGetCommandLine(proc.Id) ?? string.Empty;
                    var did = ExtractDid(cmd);
                    var bon = ExtractBonDriverFileName(cmd);
                    var managedByRegistry = TvAirManagedProcessRegistry.TryGet(proc.Id, out var managed);
                    var managedActivityOnly = managedByRegistry && managed.IsActivityOnly;
                    var managedViewer = managedByRegistry && managed.IsViewer;
                    var isCommandLineRecording = ContainsToken(cmd, "/rec");
                    var isEpg = ContainsToken(cmd, "/recfile") || ContainsToken(cmd, "/recduration") || ContainsToken(cmd, "/noview")
                        || (managedActivityOnly && ContainsToken(managed.ActivityReason ?? string.Empty, "EPG"));
                    var isTvAirManaged = managedByRegistry || (isCommandLineRecording && (ContainsToken(cmd, "/silent") || ContainsToken(cmd, "/recfile") || ContainsToken(cmd, "/recduration")));
                    var isRecording = isCommandLineRecording || (managedByRegistry && !managedActivityOnly && !managedViewer);
                    var isLiveViewing = !isRecording && !isTvAirManaged;
                    if (managedByRegistry)
                    {
                        did = string.IsNullOrWhiteSpace(did) ? managed.Did : did;
                        bon = string.IsNullOrWhiteSpace(bon) ? managed.BonDriverFileName : bon;
                    }
                    var info = new TvTestProcessInfo(proc.Id, proc.ProcessName, cmd, isLiveViewing, isRecording, isEpg, isTvAirManaged, did, bon);
                    processes.Add(info);

                    var externalKind = managedByRegistry
                        ? "Managed"
                        : string.Equals(proc.ProcessName, "LIVETest", StringComparison.OrdinalIgnoreCase)
                            ? "ExternalLIVETest"
                            : isLiveViewing
                                ? "ExternalTVTest"
                                : "ExternalUnknown";

                    var managedInfo = managedByRegistry
                        ? $" managedPurpose={managed.Purpose} managedReservation={(managed.ReservationId.HasValue ? "R" + managed.ReservationId.Value : "-")} managedReason={SafeValue(managed.ActivityReason)} managedService={SafeValue(managed.ActivityServiceName)} managedDid={SafeValue(managed.Did)} managedBonDriver={SafeValue(managed.BonDriverFileName)}"
                        : " managedPurpose=- managedReservation=- managedReason=- managedService=- managedDid=- managedBonDriver=-";
                    var didKnown = !string.IsNullOrWhiteSpace(did);
                    var bonKnown = !string.IsNullOrWhiteSpace(bon);
                    var identityState = ResolveIdentityState(cmd, managedByRegistry, isLiveViewing, didKnown, bonKnown);
                    var identityPolicy = ResolveIdentityPolicy(identityState);
                    log.Add("TVTEST_PROCESS_FOUND", phase,
                        $"pid={proc.Id} name={proc.ProcessName} externalKind={externalKind} liveViewing={isLiveViewing} recording={isRecording} epgLike={isEpg} tvairManaged={isTvAirManaged}{managedInfo} did={SafeValue(did)} bonDriver={SafeValue(bon)} didKnown={didKnown} bonDriverKnown={bonKnown} identityState={identityState} identityPolicy={identityPolicy} startupCommandSnapshot={CompactCommandLineForAudit(cmd)} snapshotNote=startup_command_snapshot_not_current_retune_state currentStateSource={(managedByRegistry ? "managed_registry" : "startup_command_snapshot")} rule=v0.11.590_external_tvtest_identity_snapshot_contract");

                    if (managedByRegistry && managedActivityOnly)
                    {
                        activityKeepers.Add(new ActivityKeeperNoticeItem(proc.Id, managed.ActivityServiceName, managed.ActivityReason));
                    }

                    if (emitLegacyEvents && isLiveViewing)
                    {
                        log.Add("LIVE_VIEWING_DETECTED", phase,
                            $"pid={proc.Id} name={proc.ProcessName} externalKind={externalKind} didKnown={didKnown} bonDriverKnown={bonKnown} identityState={identityState} note=existing_tvtest_like_process_will_not_be_touched identityUnknownPolicy=do_not_touch_external_process_recording_slot_guard_only rule=v0.11.590_external_tvtest_identity_snapshot_contract");
                    }
                }
                catch (Exception ex)
                {
                    log.Add("TVTEST_PROCESS_SCAN", "WARN", $"phase={phase} pid={SafeId(proc)} error={ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            log.Add("TVTEST_PROCESS_SCAN", "ERROR", $"phase={phase} error={ex.Message}");
        }
        EmitActivityKeeperSummary(log, phase, activityKeepers);
        return new ProcessSnapshot(processes);
    }


    private static string ResolveIdentityState(string commandLine, bool managedByRegistry, bool isLiveViewing, bool didKnown, bool bonKnown)
    {
        if (managedByRegistry) return "managed_registry";
        if (didKnown && bonKnown) return "external_command_identity_complete";
        if (!isLiveViewing) return "external_non_live_or_recording_snapshot";
        return "external_live_identity_unknown_startup_snapshot";
    }

    private static string ResolveIdentityPolicy(string identityState)
    {
        return identityState switch
        {
            "external_live_identity_unknown_startup_snapshot" => "snapshot_only_do_not_treat_as_current_did_guard_by_itself",
            "external_command_identity_complete" => "did_bondriver_collision_guard_available",
            "managed_registry" => "managed_registry_is_current_state_source",
            _ => "audit_only"
        };
    }

    private static string CompactCommandLineForAudit(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "-";
        var text = commandLine.Trim();
        text = Regex.Replace(text, "^\\\"?[^\\\"\\s]*?(TVTest|LIVETest)\\.exe\\\"?", "$1.exe", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "/d\\s+\\\"?([^\\\"\\s\\\\/]+\\.dll)\\\"?", "/d $1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "[A-Za-z]:\\\\[^\\\"\\s]+\\\\([^\\\\\\\"\\s]+\\.dll)", "$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "[A-Za-z]:\\\\[^\\\"\\s]+\\\\([^\\\\\\\"\\s]+\\.exe)", "$1", RegexOptions.IgnoreCase);
        return Trim(text, 220);
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
        log.Add("RECORDER_ACTIVITY_NOTICE", phase,
            $"result=INFO count={ordered.Count} role=ActivityKeeper " +
            "note=activity_keeper_tvtest_not_recording_route directRecorderFailure=False " +
            $"action=sleepguard_marker_only summary={summary} " +
            "rule=v0.5.85_activitykeeper_notice_scan_summary");
    }

    private static bool IsTvTestLikeProcessName(string name)
    {
        return string.Equals(name, "TVTest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "LIVETest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TVTest", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LIVETest", StringComparison.OrdinalIgnoreCase);
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

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return -1; }
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

    private static string FormatLiveProcesses(IEnumerable<TvTestProcessInfo> live)
    {
        var parts = live.Select(p => $"pid={p.ProcessId}/name={p.ProcessName}/did={SafeValue(p.Did)}/bon={SafeValue(p.BonDriverFileName)}").ToList();
        return parts.Count == 0 ? "-" : string.Join(";", parts);
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
