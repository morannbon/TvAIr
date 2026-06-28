/* v0.11.128 remaining-core-release-readiness-closure: 予約/録画/Wake/EPG/表版ログの正本分離を縦横串Z軸で統合。 */
/* v0.11.127 remaining-backlog-final-closure: WAKE/SameVersion/録画終了/取消/無効化のユーザー運用ログ整合を統合検証対象として固定。 */
﻿using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TvAIr.Core;

/// <summary>
/// ユーザー向けの軽量運用ログ。
/// /api/log の開発診断ログとは別に、不具合報告で貼れる最小限のイベントだけを保存する。
///
/// v0.11.116: 予約追加/取消/有効化/無効化の起点分類を固定し、内部予約を通常ユーザー運用ログから分離する。
/// v0.11.119: 取消・無効/有効化系のユーザー運用ログ詳細status/状態メタ属性を追加系と同粒度へ整理。
/// v0.11.126: SameVersionProcessStart / WAKE / DROP / 軽微品質ログを通常ユーザー運用ログから閉じ、報告用正本を維持。
/// - ログタブ: ユーザーが日常運用で確認する短い事実だけを表示する。
/// - 報告用コピー: 同じ UserOperationEvent のメタ属性を展開する。/api/log の丸写しではない。
/// - /api/log: 裏版・開発診断用。表版では切ってもログタブ/報告用コピーが残る構造にする。
/// </summary>
public sealed class UserEventLogService
{
    private const int MaxRows = 100;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);
    private readonly Database db;
    private readonly object gate = new();
    private int pruneCounter;

    public UserEventLogService(Database db, LogRepository developerLog)
    {
        this.db = db;
        developerLog.EntryAdded += OnDeveloperLogEntryAdded;
        Prune();
    }

    private void OnDeveloperLogEntryAdded(LogEntry entry)
    {
        try
        {
            CleanupResolvedPluginFailure(entry);
            CleanupResolvedChainSwitchFailure(entry);
            CleanupResolvedRecordingInterrupted(entry);
            CleanupResolvedRecordingFailure(entry);
            var mapped = TryMap(entry);
            if (mapped is null) return;
            Add(mapped);
        }
        catch
        {
            // ユーザー向けログは診断補助。通常ログ・録画・EPG本線へ例外を伝播させない。
        }
    }

    public void Add(UserEventLogEntry entry)
    {
        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_event_logs
                    (created_at, severity, category, result, target, message, code, trace_id,
                     operation_id, app_version, previous_app_version, version_changed, story_context,
                     origin, event_trigger, story_role, target_kind, actionability, reservation_source, program_title, detail)
                VALUES
                    ($createdAt, $severity, $category, $result, $target, $message, $code, $traceId,
                     $operationId, $appVersion, $previousAppVersion, $versionChanged, $storyContext,
                     $origin, $trigger, $storyRole, $targetKind, $actionability, $reservationSource, $programTitle, $detail);
                """;
            cmd.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$severity", entry.Severity);
            cmd.Parameters.AddWithValue("$category", entry.Category);
            cmd.Parameters.AddWithValue("$result", entry.Result);
            cmd.Parameters.AddWithValue("$target", entry.Target);
            cmd.Parameters.AddWithValue("$message", entry.Message);
            cmd.Parameters.AddWithValue("$code", entry.Code);
            cmd.Parameters.AddWithValue("$traceId", entry.TraceId);
            cmd.Parameters.AddWithValue("$operationId", string.IsNullOrWhiteSpace(entry.OperationId) ? entry.TraceId : entry.OperationId);
            cmd.Parameters.AddWithValue("$appVersion", entry.AppVersion ?? string.Empty);
            cmd.Parameters.AddWithValue("$previousAppVersion", entry.PreviousAppVersion ?? string.Empty);
            cmd.Parameters.AddWithValue("$versionChanged", entry.VersionChanged ? 1 : 0);
            cmd.Parameters.AddWithValue("$storyContext", entry.StoryContext ?? string.Empty);
            cmd.Parameters.AddWithValue("$origin", entry.Origin ?? string.Empty);
            cmd.Parameters.AddWithValue("$trigger", entry.Trigger ?? string.Empty);
            cmd.Parameters.AddWithValue("$storyRole", entry.StoryRole ?? string.Empty);
            cmd.Parameters.AddWithValue("$targetKind", entry.TargetKind ?? string.Empty);
            cmd.Parameters.AddWithValue("$actionability", entry.Actionability ?? string.Empty);
            cmd.Parameters.AddWithValue("$reservationSource", entry.ReservationSource ?? string.Empty);
            cmd.Parameters.AddWithValue("$programTitle", entry.ProgramTitle ?? string.Empty);
            cmd.Parameters.AddWithValue("$detail", entry.Detail ?? string.Empty);
            cmd.ExecuteNonQuery();

            pruneCounter++;
            if (pruneCounter >= 20)
            {
                pruneCounter = 0;
                Prune(con);
            }
        }
    }

    public IReadOnlyList<UserEventLogEntry> GetRecent(int count = 100, string? severity = null, string? category = null)
    {
        count = Math.Clamp(count, 1, 100);
        lock (gate)
        {
            Prune();
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(severity))
            {
                where.Add("severity = $severity");
                cmd.Parameters.AddWithValue("$severity", severity.Trim().ToUpperInvariant());
            }
            if (!string.IsNullOrWhiteSpace(category))
            {
                where.Add("category = $category");
                cmd.Parameters.AddWithValue("$category", category.Trim());
            }
            cmd.CommandText = $"""
                SELECT id, created_at, severity, category, result, target, message, code, trace_id, operation_id, app_version, previous_app_version, version_changed, story_context, origin, event_trigger, story_role, target_kind, actionability, reservation_source, program_title, detail
                FROM user_event_logs
                {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
                ORDER BY created_at DESC, id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", count);
            return ReadEntries(cmd).ToArray();
        }
    }

    public IReadOnlyList<UserEventLogEntry> GetReportEntries(TimeSpan window, int maxRows = 200)
    {
        maxRows = Math.Clamp(maxRows, 1, 100);
        var since = DateTime.Now.Subtract(window);
        lock (gate)
        {
            Prune();
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT id, created_at, severity, category, result, target, message, code, trace_id, operation_id, app_version, previous_app_version, version_changed, story_context, origin, event_trigger, story_role, target_kind, actionability, reservation_source, program_title, detail
                FROM user_event_logs
                WHERE created_at >= $since
                ORDER BY created_at DESC, id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$since", since.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", maxRows);
            return ReadEntries(cmd).ToArray();
        }
    }

    public int Clear()
    {
        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM user_event_logs;";
            return cmd.ExecuteNonQuery();
        }
    }

    public void AddAppStarted(string appVersion, DateTime? createdAt = null)
    {
        var operationId = BuildOperationId("APP_START", createdAt ?? DateTime.Now);
        var versionState = ResolveAppVersionStory(appVersion);

        // v0.11.137:
        // 実プロセス起動は同一バージョンでもユーザー運用ログへ残す。
        // Wakeシグナルや既存プロセス通知はここへ来ない入口側で分離し、
        // ここでは「TvAIr.exe が実際に起動した事実」を表ログの正本として扱う。

        Add(new UserEventLogEntry
        {
            CreatedAt = createdAt ?? DateTime.Now,
            Severity = "INFO",
            Category = "起動",
            Result = "OK",
            Target = "TvAIr",
            Message = "TvAIrを起動しました",
            Code = "APP_START_OK",
            TraceId = operationId,
            OperationId = operationId,
            AppVersion = appVersion ?? string.Empty,
            PreviousAppVersion = versionState.PreviousVersion,
            VersionChanged = versionState.VersionChanged,
            StoryContext = versionState.StoryContext,
            Origin = "AppLifecycle",
            Trigger = "ProcessStart",
            StoryRole = "Start",
            TargetKind = "App",
            Actionability = "NoAction",
            Detail = string.Empty
        });
    }


    public void AddReservationAdded(Reservation reservation, int reservationId, DateTime? createdAt = null)
    {
        if (!ShouldEmitReservationOperation(reservation)) return;
        // ユーザー明示チェーン予約は通常の「予約追加」に潰さず、
        // AddChainReservationAdded() で predecessor/successor を持つ運用ストーリーとして発行する。
        if (reservation.IsUserChain) return;
        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        if (!route.EmitToUserLog) return;
        var operationId = BuildOperationId(route.OperationCodePrefix, at);
        var title = ResolveReservationOperationTitle(reservation);
        var service = NormalizeOperationText(reservation.ServiceName);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "予約",
            Result = "OK",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? route.MessagePrefix : $"{route.MessagePrefix}: {title}",
            Code = route.ResultCode,
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = route.StoryContext,
            Origin = "Reservation",
            Trigger = route.Trigger,
            StoryRole = "Start",
            TargetKind = route.TargetKind,
            Actionability = "NoAction",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = BuildDirectReservationDetail(reservation, reservationId, route)
        });
    }

    public void AddChainReservationAdded(Reservation? predecessor, Reservation successor, int reservationId, bool convertedExisting, DateTime? createdAt = null)
    {
        if (!ShouldEmitReservationOperation(successor)) return;
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId(convertedExisting ? "CHAIN_RES_CONVERT_OK" : "CHAIN_RES_ADD_OK", at);
        var title = ResolveReservationOperationTitle(successor);
        var service = NormalizeOperationText(successor.ServiceName);
        var predTitle = predecessor is null ? string.Empty : ResolveReservationOperationTitle(predecessor);
        var predService = NormalizeOperationText(predecessor?.ServiceName);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "予約",
            Result = "OK",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title)
                ? (convertedExisting ? "既存予約をチェーン予約に変更しました" : "チェーン予約を追加しました")
                : (convertedExisting ? $"既存予約をチェーン予約に変更しました: {title}" : $"チェーン予約を追加しました: {title}"),
            Code = convertedExisting ? "CHAIN_RESERVATION_CONVERTED" : "CHAIN_RESERVATION_ADDED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = convertedExisting ? "ChainReservationConverted" : "ChainReservationAdded",
            Origin = "Reservation",
            Trigger = "UserChainReservation",
            StoryRole = "Start",
            TargetKind = "チェーン予約",
            Actionability = "NoAction",
            ReservationSource = NormalizeReservationSourceLabel(successor.Source.ToString()),
            ProgramTitle = title,
            Detail = BuildCompactDetail(new []
            {
                reservationId > 0 ? $"reservationId=R{reservationId}" : string.Empty,
                predecessor is null ? string.Empty : $"predecessor=R{predecessor.Id}",
                successor.UserChainRootId.HasValue ? $"chainRoot=R{successor.UserChainRootId.Value}" : string.Empty,
                string.IsNullOrWhiteSpace(predService) ? string.Empty : $"predecessorService={predService}",
                string.IsNullOrWhiteSpace(predTitle) ? string.Empty : $"predecessorTitle={predTitle}",
                string.IsNullOrWhiteSpace(service) ? string.Empty : $"service={service}",
                string.IsNullOrWhiteSpace(title) ? string.Empty : $"programTitle={title}",
                $"source={NormalizeReservationSourceLabel(successor.Source.ToString())}",
                $"sourceRaw={successor.Source}",
                "route=user_chain",
                "retention=active_reservation",
                $"status={successor.Status}",
                string.IsNullOrWhiteSpace(successor.TunerName) ? string.Empty : $"tuner={NormalizeOperationText(successor.TunerName)}",
                $"start={successor.StartTime:yyyy/MM/dd HH:mm:ss}",
                $"end={successor.EndTime:yyyy/MM/dd HH:mm:ss}"
            })
        });
    }

    public void AddChainReservationCancelled(IReadOnlyList<Reservation> targets, int requestedReservationId, DateTime? createdAt = null)
    {
        if (targets is null || targets.Count == 0) return;
        var visibleTargets = targets.Where(ShouldEmitReservationOperation).ToList();
        if (visibleTargets.Count == 0) return;

        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("CHAIN_RES_CANCEL_OK", at);
        var first = visibleTargets[0];
        var title = ResolveReservationOperationTitle(first);
        var service = NormalizeOperationText(first.ServiceName);
        var others = Math.Max(visibleTargets.Count - 1, 0);
        var suffix = others > 0 ? $" ほか{others}件" : string.Empty;

        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "予約",
            Result = "CANCEL",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title)
                ? $"チェーン予約を解除しました{suffix}"
                : $"チェーン予約を解除しました: {title}{suffix}",
            Code = "CHAIN_RESERVATION_CANCELLED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "ChainReservationCancelled",
            Origin = "Reservation",
            Trigger = "UserChainReservation",
            StoryRole = "Cancelled",
            TargetKind = "チェーン予約",
            Actionability = "NoAction",
            ReservationSource = NormalizeReservationSourceLabel(first.Source.ToString()),
            ProgramTitle = title,
            Detail = BuildCompactDetail(new []
            {
                requestedReservationId > 0 ? $"requestedReservationId=R{requestedReservationId}" : string.Empty,
                $"count={visibleTargets.Count}",
                $"targets=[{string.Join(",", visibleTargets.Select(x => $"R{x.Id}"))}]",
                string.IsNullOrWhiteSpace(service) ? string.Empty : $"service={service}",
                string.IsNullOrWhiteSpace(title) ? string.Empty : $"programTitle={title}",
                visibleTargets.Count > 1 ? $"successorTitles=[{string.Join(" | ", visibleTargets.Skip(1).Select(x => ResolveReservationOperationTitle(x)).Where(x => !string.IsNullOrWhiteSpace(x)))}]" : string.Empty,
                $"source={NormalizeReservationSourceLabel(first.Source.ToString())}",
                $"sourceRaw={first.Source}",
                "route=user_chain_cancel",
                "retention=terminal_reservation",
                "status=Cancelled",
                $"enabled={first.IsEnabled}",
                $"start={first.StartTime:yyyy/MM/dd HH:mm:ss}",
                $"end={first.EndTime:yyyy/MM/dd HH:mm:ss}"
            })
        });
    }

    public void AddProgramRuleEnabledChanged(ProgramRule rule, int ruleId, bool enabled, DateTime? createdAt = null)
    {
        if (rule is null) return;
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId(enabled ? "PROG_RULE_ENABLE" : "PROG_RULE_DISABLE", at);
        var title = NormalizeOperationText(rule.Name);
        var action = enabled ? "有効" : "無効";
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "予約",
            Result = enabled ? "OK" : "DISABLED",
            Target = string.IsNullOrWhiteSpace(title) ? "プログラム予約ルール" : title,
            Message = string.IsNullOrWhiteSpace(title)
                ? $"プログラム予約ルールを{action}にしました"
                : $"プログラム予約ルールを{action}にしました: {title}",
            Code = enabled ? "PROGRAM_RULE_ENABLED" : "PROGRAM_RULE_DISABLED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = enabled ? "ProgramRuleEnabled" : "ProgramRuleDisabled",
            Origin = "Reservation",
            Trigger = "ProgramRule",
            StoryRole = enabled ? "Start" : "Stop",
            TargetKind = "プログラム予約ルール",
            Actionability = "NoAction",
            ReservationSource = "プログラム",
            ProgramTitle = title,
            Detail = BuildCompactDetail(new []
            {
                ruleId > 0 ? $"ruleId={ruleId}" : string.Empty,
                string.IsNullOrWhiteSpace(title) ? string.Empty : $"ruleName={title}",
                $"source=プログラム",
                "sourceRaw=ProgramRule",
                "route=program_rule",
                "retention=rule_operation",
                $"enabled={enabled}",
                $"dayOfWeek={rule.DayOfWeek}",
                string.IsNullOrWhiteSpace(rule.StartTime) ? string.Empty : $"startTime={NormalizeOperationText(rule.StartTime)}",
                string.IsNullOrWhiteSpace(rule.EndTime) ? string.Empty : $"endTime={NormalizeOperationText(rule.EndTime)}",
                rule.NetworkId > 0 ? $"networkId={rule.NetworkId}" : string.Empty,
                rule.TransportStreamId > 0 ? $"transportStreamId={rule.TransportStreamId}" : string.Empty,
                rule.ServiceId > 0 ? $"serviceId={rule.ServiceId}" : string.Empty
            })
        });
    }

    public void AddReservationEnabledChanged(Reservation reservation, int reservationId, bool enabled, DateTime? createdAt = null)
    {
        if (!ShouldEmitReservationOperation(reservation)) return;
        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        if (!route.EmitToUserLog) return;
        var action = enabled ? "有効" : "無効";
        var code = $"{route.RouteKind.ToUpperInvariant()}_RESERVATION_{(enabled ? "ENABLED" : "DISABLED")}";
        code = code.Replace("-", "_").Replace(" ", "_");
        var operationId = BuildOperationId(enabled ? $"{route.OperationCodePrefix}_EN" : $"{route.OperationCodePrefix}_DIS", at);
        var title = ResolveReservationOperationTitle(reservation);
        var service = NormalizeOperationText(reservation.ServiceName);
        var messagePrefix = $"{route.ReservationSourceLabel}予約を{action}にしました";
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "予約",
            Result = enabled ? "OK" : "DISABLED",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? messagePrefix : $"{messagePrefix}: {title}",
            Code = code,
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = $"{route.StoryContext.Replace("Added", string.Empty)}{(enabled ? "Enabled" : "Disabled")}",
            Origin = "Reservation",
            Trigger = route.Trigger,
            StoryRole = enabled ? "Start" : "Stop",
            TargetKind = route.TargetKind,
            Actionability = "NoAction",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = BuildDirectReservationDetail(reservation, reservationId, route, null, titleOverride: title) + $"; previousEnabled={reservation.IsEnabled}; enabled={enabled}"
        });
    }

    public void AddReservationDeleted(Reservation reservation, int reservationId, DateTime? createdAt = null)
    {
        if (!ShouldEmitReservationOperation(reservation)) return;
        var recording = reservation.Status == ReservationStatus.Recording;
        if (!recording && IsUserChainLikeReservation(reservation))
        {
            AddChainReservationCancelled(new[] { reservation }, reservationId, createdAt);
            return;
        }
        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        if (!route.EmitToUserLog) return;
        var code = recording ? "REC_CANCELLED" : $"{route.RouteKind.ToUpperInvariant()}_RESERVATION_CANCELLED";
        code = code.Replace("-", "_").Replace(" ", "_");
        var operationId = BuildOperationId(recording ? "REC_CANCELLED" : $"{route.OperationCodePrefix}_CAN", at);
        var title = ResolveReservationOperationTitle(reservation);
        var service = NormalizeOperationText(reservation.ServiceName);
        var messagePrefix = recording ? "録画をキャンセルしました" : $"{route.ReservationSourceLabel}予約を取り消しました";
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = recording ? "録画" : "予約",
            Result = "CANCEL",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? messagePrefix : $"{messagePrefix}: {title}",
            Code = code,
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = recording ? "RecordingCancelled" : $"{route.StoryContext.Replace("Added", string.Empty)}Cancelled",
            Origin = recording ? "Recording" : "Reservation",
            Trigger = recording ? "RecordingPipeline" : route.Trigger,
            StoryRole = "Cancelled",
            TargetKind = recording ? "録画" : route.TargetKind,
            Actionability = "NoAction",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = BuildDirectReservationDetail(reservation, reservationId, route, statusOverride: ReservationStatus.Cancelled)
        });
    }

    public void AddReservationStatusChanged(Reservation? before, int reservationId, ReservationStatus newStatus, DateTime? createdAt = null)
    {
        if (before is null || !ShouldEmitReservationOperation(before)) return;
        var at = createdAt ?? DateTime.Now;
        var title = ResolveReservationOperationTitle(before);
        var service = NormalizeOperationText(before.ServiceName);
        var target = BuildOperationTarget(service, title);
        var source = NormalizeReservationSourceLabel(before.Source.ToString());

        UserEventLogEntry? entry = null;
        if (newStatus == ReservationStatus.Recording && before.Status != ReservationStatus.Recording)
        {
            var operationId = BuildOperationId("REC_START_OK", at);
            entry = new UserEventLogEntry
            {
                CreatedAt = at,
                Severity = "INFO",
                Category = "録画",
                Result = "OK",
                Target = target,
                Message = string.IsNullOrWhiteSpace(title) ? "録画を開始しました" : $"録画を開始しました: {title}",
                Code = "REC_START_OK",
                TraceId = operationId,
                OperationId = operationId,
                StoryContext = "RecordingStart",
                Origin = "Recording",
                Trigger = "RecordingPipeline",
                StoryRole = "Start",
                TargetKind = "録画",
                Actionability = "NoAction",
                ReservationSource = source,
                ProgramTitle = title,
                Detail = BuildDirectReservationDetail(before, reservationId)
            };
        }
        else if (newStatus == ReservationStatus.Completed && before.Status != ReservationStatus.Completed)
        {
            var operationId = BuildOperationId("REC_END_OK", at);
            entry = new UserEventLogEntry
            {
                CreatedAt = at,
                Severity = "INFO",
                Category = "録画",
                Result = "OK",
                Target = target,
                Message = string.IsNullOrWhiteSpace(title) ? "録画を終了しました" : $"録画を終了しました: {title}",
                Code = "REC_END_OK",
                TraceId = operationId,
                OperationId = operationId,
                StoryContext = "RecordingCompleted",
                Origin = "Recording",
                Trigger = "RecordingPipeline",
                StoryRole = "Completed",
                TargetKind = "録画",
                Actionability = "NoAction",
                ReservationSource = source,
                ProgramTitle = title,
                Detail = BuildDirectReservationDetail(before, reservationId, statusOverride: ReservationStatus.Completed)
            };
        }
        else if (newStatus == ReservationStatus.Failed && before.Status != ReservationStatus.Failed)
        {
            var operationId = BuildOperationId("REC_FAILED", at);
            var failureClass = before.IsConflicted ? "TunerAcquireFailed" : "Unknown";
            entry = new UserEventLogEntry
            {
                CreatedAt = at,
                Severity = "ERROR",
                Category = "録画",
                Result = "FAILED",
                Target = target,
                Message = before.IsConflicted
                    ? (string.IsNullOrWhiteSpace(title) ? "チューナー不足で録画できませんでした" : $"チューナー不足で録画できませんでした: {title}")
                    : string.Empty,
                Code = "REC_FAILED",
                TraceId = operationId,
                OperationId = operationId,
                StoryContext = "RecordingFailed",
                Origin = "Recording",
                Trigger = "RecordingPipeline",
                StoryRole = "Failed",
                TargetKind = "録画",
                Actionability = "UserCanCheck",
                ReservationSource = source,
                ProgramTitle = title,
                Detail = BuildDirectReservationDetail(before, reservationId, statusOverride: ReservationStatus.Failed) + $"; failureClass={failureClass}"
            };
        }
        else if (newStatus == ReservationStatus.Cancelled && before.Status != ReservationStatus.Cancelled)
        {
            AddReservationDeleted(before, reservationId, at);
            return;
        }

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Message))
            Add(entry);
    }

    public void AddRecordingInterruptedAtStartup(Reservation? reservation, int reservationId, string? reason = null, string? fileEvidence = null, DateTime? createdAt = null, string trigger = "StartupRecovery")
    {
        if (reservation is null) return;
        if (!ShouldEmitReservationOperation(reservation)) return;
        if (HasUserEventForReservation("REC_INTERRUPTED", reservationId)) return;

        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        var title = NormalizeOperationText(reservation.Title);
        var service = NormalizeOperationText(reservation.ServiceName);
        var target = BuildOperationTarget(service, title);
        var operationId = BuildOperationId("REC_INTERRUPTED", at);
        var detail = BuildDirectReservationDetail(reservation, reservationId, route, ReservationStatus.Failed)
            + "; failureClass=RecordingInterrupted"
            + $"; interruptionReason={NormalizeOperationText(reason ?? string.Empty)}"
            + $"; fileEvidence={NormalizeOperationText(fileEvidence ?? string.Empty)}";

        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "ERROR",
            Category = "録画",
            Result = "FAILED",
            Target = target,
            Message = string.IsNullOrWhiteSpace(title)
                ? "録画が途中で終了しました"
                : $"録画が途中で終了しました: {title}",
            Code = "REC_INTERRUPTED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "RecordingInterrupted",
            Origin = "Recording",
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "StartupRecovery" : trigger,
            StoryRole = "Failed",
            TargetKind = "録画",
            Actionability = "UserCanCheck",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = detail
        });
    }

    public void AddRecordingSkippedByConflict(Reservation reservation, int reservationId, string? group = null, string? reason = null, DateTime? createdAt = null)
    {
        if (!ShouldEmitReservationOperation(reservation)) return;
        if (HasUserEventForReservation("REC_SKIPPED_BY_CONFLICT", reservationId)) return;

        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        var title = NormalizeOperationText(reservation.Title);
        var service = NormalizeOperationText(reservation.ServiceName);
        var target = BuildOperationTarget(service, title);
        var operationId = BuildOperationId("REC_SKIP_CONFLICT", at);
        var detail = BuildDirectReservationDetail(reservation, reservationId, route, ReservationStatus.Failed)
            + $"; conflictReason={NormalizeOperationText(reason ?? "tuner_limit_exceeded")}"
            + $"; group={NormalizeOperationText(group ?? string.Empty)}"
            + "; failureClass=TunerAcquireFailed";

        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "ERROR",
            Category = "録画",
            Result = "FAILED",
            Target = target,
            Message = string.IsNullOrWhiteSpace(title)
                ? "チューナー不足で録画できませんでした"
                : $"チューナー不足で録画できませんでした: {title}",
            Code = "REC_SKIPPED_BY_CONFLICT",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "RecordingSkippedByConflict",
            Origin = "Recording",
            Trigger = "ConflictAtDueTime",
            StoryRole = "Failed",
            TargetKind = "録画",
            Actionability = "UserCanCheck",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = detail
        });
    }



    public void AddScheduledEpgStarted(string targetScope, string requestedBy, bool silent, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("EPG_RUN_START", at);
        var target = NormalizeEpgTargetLabel(targetScope);
        var storyContext = ResolveEpgRunStoryContext(requestedBy, silent, "Start");
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "EPG",
            Result = "START",
            Target = target,
            Message = "EPG取得を開始しました",
            Code = "EPG_RUN_START",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = storyContext,
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = "Start",
            TargetKind = "EPG",
            Actionability = "NoAction",
            Detail = $"targetScope={target}; silent={silent}"
        });
    }

    public void AddScheduledEpgCompleted(
        string targetScope,
        string requestedBy,
        bool silent,
        string? runResult = null,
        int? completedGroups = null,
        int? totalGroups = null,
        int? importedEvents = null,
        int? missingGroups = null,
        string? resultDetail = null,
        DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var normalizedResult = string.IsNullOrWhiteSpace(runResult) ? "OK" : runResult.Trim().ToUpperInvariant();
        var isCleanOk = normalizedResult == "OK";
        var isBlocked = normalizedResult == "BLOCKED";
        var code = isCleanOk ? "EPG_RUN_OK" : isBlocked ? "EPG_RUN_BLOCKED" : "EPG_RUN_FAILED";
        var operationId = BuildOperationId(code, at);
        var target = NormalizeEpgTargetLabel(targetScope);
        var storyContextBase = isCleanOk ? "Completed" : isBlocked ? "Blocked" : "Failed";
        var storyContext = ResolveEpgRunStoryContext(requestedBy, silent, storyContextBase);
        var message = isCleanOk
            ? "EPG取得を完了しました"
            : isBlocked
                ? BuildEpgBlockedUserMessage(resultDetail, targetScope)
                : BuildGroundedEpgFailureUserMessage(resultDetail);
        if (!isCleanOk && !isBlocked && string.IsNullOrWhiteSpace(message))
            return;
        var detailParts = new List<string>
        {
            $"targetScope={target}",
            $"silent={silent}",
            $"runResult={normalizedResult}"
        };
        if (completedGroups.HasValue && totalGroups.HasValue) detailParts.Add($"completedGroups={completedGroups}/{totalGroups}");
        if (importedEvents.HasValue) detailParts.Add($"imported={importedEvents}");
        // User-visible EPG results stay: completed / cannot start / failed.
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = isCleanOk ? "INFO" : isBlocked ? "WARN" : "ERROR",
            Category = "EPG",
            Result = isCleanOk ? "OK" : isBlocked ? "BLOCKED" : "FAILED",
            Target = target,
            Message = message,
            Code = code,
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = storyContext,
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = isCleanOk ? "Completed" : isBlocked ? "Blocked" : "Failed",
            TargetKind = "EPG",
            Actionability = isCleanOk || isBlocked ? "NoAction" : "UserCanCheck",
            Detail = string.Join("; ", detailParts)
        });
    }


    private static string BuildGroundedEpgFailureUserMessage(string? resultDetail)
    {
        if (string.IsNullOrWhiteSpace(resultDetail)) return string.Empty;
        var d = resultDetail.ToUpperInvariant();

        if (d.Contains("UNAUTHORIZEDACCESSEXCEPTION")
            || d.Contains("IOEXCEPTION")
            || d.Contains("DIRECTORYNOTFOUNDEXCEPTION")
            || d.Contains("PATHTOOLONGEXCEPTION")
            || d.Contains("ACCESS DENIED")
            || d.Contains("DISK FULL")
            || d.Contains("NOT ENOUGH SPACE")
            || (d.Contains("WRITE") && (d.Contains("DENIED") || d.Contains("FAILED") || d.Contains("ERROR"))))
            return "保存先に書き込めませんでした";

        if (d.Contains("SQLITE")
            || d.Contains("DATABASE")
            || d.Contains("DB_")
            || (d.Contains("IMPORT") && (d.Contains("FAILED") || d.Contains("ERROR")))
            || (d.Contains("STORE") && (d.Contains("FAILED") || d.Contains("ERROR"))))
            return "番組表を更新できませんでした";

        if ((d.Contains("TVAIREPGREC") || d.Contains("WORKER") || d.Contains("PROCESS"))
            && (d.Contains("START") || d.Contains("LAUNCH") || d.Contains("NOT FOUND") || d.Contains("MISSING")))
            return "EPG取得用の実行ファイルを起動できませんでした";

        return string.Empty;
    }

    private static string BuildEpgBlockedUserMessage(string? resultDetail, string? targetScope = null)
    {
        var reason = ExtractDetailValue(resultDetail, "blockedReason");
        var blockedGroup = ExtractDetailValue(resultDetail, "blockedGroup");
        // User-facing blocked messages stay short. The affected scope is shown separately as Target.

        if (reason.Contains("no_free", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("no_tuner", StringComparison.OrdinalIgnoreCase)
            || (reason.Contains("tuner", StringComparison.OrdinalIgnoreCase)
                && !reason.Contains("recording", StringComparison.OrdinalIgnoreCase)))
        {
            return "空きチューナーがないためEPG取得を開始できません";
        }

        if (reason.Contains("warmup", StringComparison.OrdinalIgnoreCase))
            return "録画開始直後のためEPG取得を開始できません";

        if (reason.Contains("window_insufficient", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("preempt", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("timeline", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("next", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("due", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("recording", StringComparison.OrdinalIgnoreCase))
        {
            return "録画予約が近いためEPG取得を開始できません";
        }

        return "録画予約が近いためEPG取得を開始できません";
    }

    private static string ExtractDetailValue(string? detail, string key)
    {
        if (string.IsNullOrWhiteSpace(detail) || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        foreach (var part in detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            var name = part[..index].Trim();
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                return part[(index + 1)..].Trim();
        }

        return string.Empty;
    }

    public void AddScheduledEpgFailed(string targetScope, string requestedBy, bool silent, string? resultDetail = null, DateTime? createdAt = null)
    {
        var message = BuildGroundedEpgFailureUserMessage(resultDetail);
        if (string.IsNullOrWhiteSpace(message))
            return;

        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("EPG_RUN_FAILED", at);
        var target = NormalizeEpgTargetLabel(targetScope);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "ERROR",
            Category = "EPG",
            Result = "FAILED",
            Target = target,
            Message = message,
            Code = "EPG_RUN_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = ResolveEpgRunStoryContext(requestedBy, silent, "Failed"),
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = "Failed",
            TargetKind = "EPG",
            Actionability = "UserCanCheck",
            Detail = BuildCompactDetail(new [] { $"targetScope={target}", $"silent={silent}" })
        });
    }

    public void AddScheduledEpgCancelled(string targetScope, string requestedBy, bool silent, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("EPG_RUN_CANCELLED", at);
        var target = NormalizeEpgTargetLabel(targetScope);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "EPG",
            Result = "CANCEL",
            Target = target,
            Message = "EPG取得をキャンセルしました",
            Code = "EPG_RUN_CANCELLED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = ResolveEpgRunStoryContext(requestedBy, silent, "Cancelled"),
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = "Cancelled",
            TargetKind = "EPG",
            Actionability = "NoAction",
            Detail = $"targetScope={target}; silent={silent}"
        });
    }

    public void AddPreRecordEpgFailed(Reservation parent, string? reason = null, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("PRE_REC_EPG_FAILED", at);
        var title = NormalizeOperationText(parent.Title);
        var service = NormalizeOperationText(parent.ServiceName);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "ERROR",
            Category = "EPG確認",
            Result = "FAILED",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? "録画前EPG確認ができませんでした。予約時刻で録画します" : $"録画前EPG確認ができませんでした。予約時刻で録画します: {title}",
            Code = "PRE_REC_EPG_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "PreRecordEpgFallbackToReservedTime",
            Origin = "PreRecordEpg",
            Trigger = "PreRecordCheck",
            StoryRole = "Failed",
            TargetKind = "録画前EPG確認",
            Actionability = "UserCanCheck",
            ReservationSource = NormalizeReservationSourceLabel(parent.Source.ToString()),
            ProgramTitle = title,
            Detail = BuildCompactDetail(new [] { $"reservationId=R{parent.Id}", $"service={service}", $"programTitle={title}", string.IsNullOrWhiteSpace(reason) ? string.Empty : $"reason={NormalizeOperationText(reason)}" })
        });
    }

    public void AddTimeFollowUpdated(Reservation reservation, DateTime oldStart, DateTime oldEnd, DateTime newStart, DateTime newEnd, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("TIME_FOLLOW_UPDATED", at);
        var title = NormalizeOperationText(reservation.Title);
        var service = NormalizeOperationText(reservation.ServiceName);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "時間追従",
            Result = "OK",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? "放送時刻を追従しました" : $"放送時刻を追従しました: {title}",
            Code = "TIME_FOLLOW_UPDATED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "ReservationTimeAdjusted",
            Origin = "PreRecordEpg",
            Trigger = "PreRecordCheck",
            StoryRole = "Adjusted",
            TargetKind = "予約",
            Actionability = "NoAction",
            ReservationSource = NormalizeReservationSourceLabel(reservation.Source.ToString()),
            ProgramTitle = title,
            Detail = $"reservationId=R{reservation.Id}; old={oldStart:yyyy/MM/dd HH:mm:ss}〜{oldEnd:yyyy/MM/dd HH:mm:ss}; new={newStart:yyyy/MM/dd HH:mm:ss}〜{newEnd:yyyy/MM/dd HH:mm:ss}"
        });
    }

    public void AddWakeRegistrationFailed(Reservation? reservation, int failedCount, string? detail = null, DateTime? createdAt = null)
    {
        // v0.11.125:
        // Wake登録失敗はユーザー運用ログへ直接出さない。Task Scheduler の AccessDenied/failed/kept は
        // /api/log 側の診断情報に閉じ、ユーザー運用ログは録画本線の実失敗・成功だけを正本にする。
        // ここで追加すると同じ予約に対して繰り返し不安ログが出るため、明示的に no-op とする。
        _ = reservation;
        _ = failedCount;
        _ = detail;
        _ = createdAt;
        return;
    }

    public void AddChainSwitched(Reservation? predecessor, Reservation successor, bool success, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId(success ? "CHAIN_SWITCH_OK" : "CHAIN_SWITCH_FAILED", at);
        var succTitle = NormalizeOperationText(successor.Title);
        var succService = NormalizeOperationText(successor.ServiceName);
        var predTitle = NormalizeOperationText(predecessor?.Title);
        var switchLabel = BuildChainSwitchLabel(predTitle, succTitle);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = success ? "INFO" : "ERROR",
            Category = "録画",
            Result = success ? "OK" : "FAILED",
            Target = BuildOperationTarget(succService, succTitle),
            Message = success
                ? (string.IsNullOrWhiteSpace(switchLabel) ? "チェーン録画を切り替えました" : $"チェーン録画を切り替えました: {switchLabel}")
                : (string.IsNullOrWhiteSpace(switchLabel) ? "チェーン録画の切り替えに失敗しました" : $"チェーン録画の切り替えに失敗しました: {switchLabel}"),
            Code = success ? "CHAIN_SWITCH_OK" : "CHAIN_SWITCH_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "ChainBoundarySwitch",
            Origin = "Recording",
            Trigger = "ChainBoundary",
            StoryRole = success ? "ChainSwitch" : "Failed",
            TargetKind = "録画",
            Actionability = success ? "NoAction" : "UserCanCheck",
            ReservationSource = NormalizeReservationSourceLabel(successor.Source.ToString()),
            ProgramTitle = succTitle,
            Detail = BuildCompactDetail(new [] { predecessor is null ? string.Empty : $"predecessor=R{predecessor.Id}", $"successor=R{successor.Id}", string.IsNullOrWhiteSpace(predTitle) ? string.Empty : $"predecessorTitle={predTitle}", string.IsNullOrWhiteSpace(succTitle) ? string.Empty : $"successorTitle={succTitle}" })
        });
    }

    public void AddPluginLoadFailed(string pluginName, string? reason = null, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("PLUGIN_LOAD_FAILED", at);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "ERROR",
            Category = "プラグイン",
            Result = "FAILED",
            Target = NormalizeOperationText(pluginName),
            Message = "プラグインを読み込めませんでした",
            Code = "PLUGIN_LOAD_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "PluginLoadFailed",
            Origin = "Plugin",
            Trigger = "PluginHost",
            StoryRole = "Failed",
            TargetKind = "プラグイン",
            Actionability = "UserCanCheck",
            Detail = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"reason={NormalizeOperationText(reason)}"
        });
    }

    private static string NormalizeEpgTargetLabel(string? targetScope)
    {
        var v = (targetScope ?? "All").Trim().ToUpperInvariant();
        return v switch
        {
            "GR" or "TERRESTRIAL" or "地上波" => "地上波",
            "BS" or "CS" or "BSCS" or "BS/CS" => "BS/CS",
            _ => "全体"
        };
    }

    private static string ResolveEpgRunStoryContext(string? requestedBy, bool silent, string role)
    {
        var source = requestedBy ?? string.Empty;
        var manualVisible = !silent && source.Contains("WebApi.EpgRun", StringComparison.OrdinalIgnoreCase);
        var scheduled = silent || source.Contains("Scheduler", StringComparison.OrdinalIgnoreCase) || source.Contains("Daily", StringComparison.OrdinalIgnoreCase);
        var prefix = manualVisible ? "ManualVisibleEpg" : scheduled ? "ScheduledEpg" : "Epg";
        return prefix + role;
    }

    private static string NormalizeEpgTriggerLabel(string? trigger)
    {
        var value = NormalizeOperationText(trigger ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Contains("EpgRun.GR", StringComparison.OrdinalIgnoreCase))
            return value.Replace("EpgRun.GR", "EpgRun.地上波", StringComparison.OrdinalIgnoreCase);
        if (value.Contains("EpgRun.BSCS", StringComparison.OrdinalIgnoreCase))
            return value.Replace("EpgRun.BSCS", "EpgRun.BS/CS", StringComparison.OrdinalIgnoreCase);
        if (value.Contains("EpgRun.BS", StringComparison.OrdinalIgnoreCase))
            return value.Replace("EpgRun.BS", "EpgRun.BS/CS", StringComparison.OrdinalIgnoreCase);
        if (value.Contains("EpgRun.CS", StringComparison.OrdinalIgnoreCase))
            return value.Replace("EpgRun.CS", "EpgRun.BS/CS", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static string BuildCompactDetail(IEnumerable<string> parts)
        => string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    private sealed record ReservationAddRoute(
        bool EmitToUserLog,
        string MessagePrefix,
        string StoryContext,
        string Trigger,
        string TargetKind,
        string ResultCode,
        string OperationCodePrefix,
        string ReservationSourceLabel,
        string RouteKind,
        string RetentionClass);

    private static ReservationAddRoute ClassifyReservationAddRoute(Reservation reservation)
    {
        // v0.11.116: 予約追加/取消/有効化/無効化ログは source 文字列の見た目ではなく、予約の性質から共通分類する。
        // SystemEpg / 録画前EPG子予約などの内部予約は通常ユーザー運用ログへ出さない。
        if (reservation.Source == ReservationSource.Epg)
        {
            return new ReservationAddRoute(
                false,
                "内部予約を追加しました",
                "InternalReservationAdded",
                "InternalSystemReservation",
                "内部予約",
                "INTERNAL_RESERVATION_ADDED",
                "INT_RES_ADD",
                "システム",
                "internal_system_epg_or_prerec",
                "short_internal");
        }

        return reservation.Source switch
        {
            ReservationSource.Keyword => new ReservationAddRoute(
                true,
                "自動検索で予約しました",
                "AutoSearchReservationAdded",
                "AutoSearchReservation",
                "予約",
                "AUTO_SEARCH_RESERVATION_ADDED",
                "AUTO_RES_ADD",
                "自動検索",
                "auto_search",
                "active_reservation"),

            ReservationSource.Program => new ReservationAddRoute(
                true,
                "プログラムから予約しました",
                "ProgramReservationAdded",
                "ProgramReservation",
                "予約",
                "PROGRAM_RESERVATION_ADDED",
                "PROG_RES_ADD",
                "プログラム",
                "program",
                "active_reservation"),

            // KeywordSearch は検索画面からユーザーが能動的に選んだ番組表扱い。
            ReservationSource.Manual or ReservationSource.KeywordSearch or ReservationSource.Immediate => new ReservationAddRoute(
                true,
                "番組表から予約しました",
                "ProgramGuideReservationAdded",
                "ProgramGuideReservation",
                "予約",
                "PROGRAM_GUIDE_RESERVATION_ADDED",
                "PG_RES_ADD",
                "番組表",
                reservation.Source == ReservationSource.KeywordSearch ? "keyword_search_user_selected" : "program_guide",
                "active_reservation"),

            _ => new ReservationAddRoute(
                true,
                "番組表から予約しました",
                "ProgramGuideReservationAdded",
                "ProgramGuideReservation",
                "予約",
                "PROGRAM_GUIDE_RESERVATION_ADDED",
                "PG_RES_ADD",
                NormalizeReservationSourceLabel(reservation.Source.ToString()),
                "program_guide_fallback",
                "active_reservation")
        };
    }

    private static bool IsUserChainLikeReservation(Reservation reservation)
        => reservation.IsUserChain || reservation.UserChainPreviousId.HasValue || reservation.UserChainRootId.HasValue;

    private static bool ShouldEmitReservationOperation(Reservation reservation)
    {
        if (reservation.Source == ReservationSource.Epg) return false;
        var title = reservation.Title ?? string.Empty;
        if (title.Contains("EPG確認", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string NormalizeOperationText(string? value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

    private static string ToReservationDisplayTitle(string? value)
        => ReservationTitleDisplayContract.ForUser(value);

    private static string BuildOperationTarget(string serviceName, string programTitle)
    {
        if (!string.IsNullOrWhiteSpace(serviceName)) return serviceName;
        if (!string.IsNullOrWhiteSpace(programTitle)) return programTitle;
        return "—";
    }


    private string ResolveReservationOperationTitle(Reservation reservation)
    {
        var title = NormalizeOperationText(reservation.Title);
        if (!string.IsNullOrWhiteSpace(title)) return title;

        // v0.11.466: 自動検索予約の個別有効/無効ログでは、古い予約や投影由来の予約で
        // Reservation.Title が空になることがある。録画/予約本線は触らず、ユーザー運用ログ表示だけ
        // EPG raw DB の同一 event identity から番組名を補完する。
        if (reservation.NetworkId == 0 || reservation.TransportStreamId == 0 || reservation.ServiceId == 0 || reservation.EventId == 0)
            return "未取得";

        try
        {
            lock (gate)
            {
                using var con = db.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = """
                    SELECT title
                    FROM epg_events
                    WHERE network_id = $nid
                      AND transport_stream_id = $tsid
                      AND service_id = $sid
                      AND event_id = $eid
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$nid", reservation.NetworkId);
                cmd.Parameters.AddWithValue("$tsid", reservation.TransportStreamId);
                cmd.Parameters.AddWithValue("$sid", reservation.ServiceId);
                cmd.Parameters.AddWithValue("$eid", reservation.EventId);
                var result = cmd.ExecuteScalar();
                return ToReservationDisplayTitle(result?.ToString() ?? string.Empty);
            }
        }
        catch
        {
            return "未取得";
        }
    }

    private static string BuildDirectReservationDetail(Reservation reservation, int reservationId)
        => BuildDirectReservationDetail(reservation, reservationId, ClassifyReservationAddRoute(reservation));

    private static string BuildDirectReservationDetail(Reservation reservation, int reservationId, ReservationStatus? statusOverride)
        => BuildDirectReservationDetail(reservation, reservationId, ClassifyReservationAddRoute(reservation), statusOverride);

    private static string BuildDirectReservationDetail(Reservation reservation, int reservationId, ReservationAddRoute route)
        => BuildDirectReservationDetail(reservation, reservationId, route, null);

    private static string BuildDirectReservationDetail(Reservation reservation, int reservationId, ReservationAddRoute route, ReservationStatus? statusOverride, string? titleOverride = null)
    {
        var parts = new List<string>();
        var title = string.IsNullOrWhiteSpace(titleOverride) ? ToReservationDisplayTitle(reservation.Title) : ToReservationDisplayTitle(titleOverride);
        if (reservationId > 0) parts.Add($"reservationId=R{reservationId}");
        if (!string.IsNullOrWhiteSpace(reservation.ServiceName)) parts.Add($"service={NormalizeOperationText(reservation.ServiceName)}");
        if (!string.IsNullOrWhiteSpace(title)) parts.Add($"programTitle={title}");
        parts.Add($"source={route.ReservationSourceLabel}");
        parts.Add($"sourceRaw={reservation.Source}");
        parts.Add($"route={route.RouteKind}");
        parts.Add($"retention={route.RetentionClass}");
        parts.Add($"status={(statusOverride ?? reservation.Status)}");
        parts.Add($"start={reservation.StartTime:yyyy/MM/dd HH:mm:ss}");
        parts.Add($"end={reservation.EndTime:yyyy/MM/dd HH:mm:ss}");
        return string.Join("; ", parts);
    }

    private bool HasUserEventForReservation(string code, int reservationId)
    {
        if (reservationId <= 0) return false;
        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT 1
                FROM user_event_logs
                WHERE code = $code
                  AND detail LIKE $reservationToken
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$reservationToken", $"%reservationId=R{reservationId}%");
            var result = cmd.ExecuteScalar();
            return result is not null && result != DBNull.Value;
        }
    }


    private static string BuildChainSwitchLabel(string predecessorTitle, string successorTitle)
    {
        if (!string.IsNullOrWhiteSpace(predecessorTitle) && !string.IsNullOrWhiteSpace(successorTitle))
            return $"{predecessorTitle} → {successorTitle}";
        if (!string.IsNullOrWhiteSpace(successorTitle)) return successorTitle;
        if (!string.IsNullOrWhiteSpace(predecessorTitle)) return predecessorTitle;
        return string.Empty;
    }


    private sealed record AppVersionStory(string PreviousVersion, bool VersionChanged, string StoryContext);

    private static AppVersionStory ResolveAppVersionStory(string? appVersion)
    {
        var current = (appVersion ?? string.Empty).Trim();
        var dir = Path.Combine(AppContext.BaseDirectory, "runtime");
        var path = Path.Combine(dir, "user-operation-last-version.txt");
        var previous = string.Empty;
        try
        {
            if (File.Exists(path))
                previous = (File.ReadAllText(path) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(previous))
                previous = TryInferPreviousVersionFromReleaseMarkers(current);
            Directory.CreateDirectory(dir);
            if (!string.IsNullOrWhiteSpace(current))
                File.WriteAllText(path, current);
        }
        catch
        {
            // バージョン文脈は報告補助。起動ログ本線は止めない。
        }

        if (!string.IsNullOrWhiteSpace(previous)
            && !string.IsNullOrWhiteSpace(current)
            && !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
        {
            return new AppVersionStory(previous, true, "VersionChangedStart");
        }

        return new AppVersionStory(previous, false, string.IsNullOrWhiteSpace(previous) ? "ProcessStart" : "SameVersionProcessStart");
    }

    private static string TryInferPreviousVersionFromReleaseMarkers(string current)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var currentNorm = NormalizeVersionKey(current);
            var candidates = Directory.GetFiles(baseDir, "release_*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n) && n!.StartsWith("release_", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!["release_".Length..].Replace('_', '.'))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Where(v => !string.Equals(NormalizeVersionKey(v), currentNorm, StringComparison.OrdinalIgnoreCase))
                .Select(v => new { Text = v, Version = Version.TryParse(v, out var parsed) ? parsed : null })
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            return candidates?.Text ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeVersionKey(string? version)
        => (version ?? string.Empty).Trim().TrimStart('v', 'V');

    private static string BuildOperationId(string prefix, DateTime at)
    {
        var safePrefix = Regex.Replace(prefix ?? "EVENT", "[^A-Za-z0-9]+", "_").Trim('_');
        if (safePrefix.Length > 12) safePrefix = safePrefix[..12];
        return $"UOE-{safePrefix}-{at:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
    }

    public void Prune()
    {
        lock (gate)
        {
            using var con = db.Open();
            Prune(con);
        }
    }


    private void CleanupResolvedPluginFailure(LogEntry entry)
    {
        var ev = (entry.Event ?? string.Empty).ToUpperInvariant();
        if (ev != "PLUGIN") return;

        var msg = entry.Message ?? string.Empty;
        var upperMsg = msg.ToUpperInvariant();
        var resolved = upperMsg.Contains("LOADED:")
            || upperMsg.Contains("INITIALIZE 完了")
            || upperMsg.Contains("INITIALIZE COMPLETED")
            || upperMsg.Contains("ONSTART 完了")
            || upperMsg.Contains("ONSTART COMPLETED");
        if (!resolved) return;

        var target = SafeDisplayTarget(entry.Title);
        if (string.IsNullOrWhiteSpace(target) || target == "—") return;

        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                DELETE FROM user_event_logs
                WHERE category = 'プラグイン'
                  AND result = 'FAILED'
                  AND code IN ('PLUGIN_LOAD_FAILED', 'PLUGIN_MENU_FAILED')
                  AND target = $target;
                """;
            cmd.Parameters.AddWithValue("$target", target);
            cmd.ExecuteNonQuery();
        }
    }

    private void CleanupResolvedRecordingInterrupted(LogEntry entry)
    {
        // v0.11.96:
        // 起動時復旧に成功した場合でも、ユーザー向けには
        // 「録画が中断されました(STOP) → 録画を再開しました(OK)」の流れを残す。
        // ここで STOP 行を消すと、TvAIr起動ログだけが唐突に見えるため削除しない。
        _ = entry;
    }

    private void CleanupResolvedRecordingFailure(LogEntry entry)
    {
        var ev = (entry.Event ?? string.Empty).ToUpperInvariant();
        var title = (entry.Title ?? string.Empty).ToUpperInvariant();
        var msg = entry.Message ?? string.Empty;
        var upperMsg = msg.ToUpperInvariant();

        var resolvedByCompletedStatus = ev == "RESERVATION_AUDIT"
            && title == "STATUS"
            && string.Equals(ReadToken(msg, "to"), "Completed", StringComparison.OrdinalIgnoreCase);

        var resolvedByFinalStatus = ev == "TVAIREPGREC_FINAL_STATUS"
            && !IsMajorRecordingFinalFailure(upperMsg);

        var resolvedByVerify = ev == "REC_TS_VERIFY"
            && !IsMajorRecordingVerifyFailure(upperMsg);

        if (!resolvedByCompletedStatus && !resolvedByFinalStatus && !resolvedByVerify) return;

        var target = BuildReservationUserTarget(msg);
        var program = BuildProgramLabel(msg);
        var programNeedle = NormalizeLikeNeedle(program);

        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            var targetClause = !string.IsNullOrWhiteSpace(target) && target != "—" ? "AND target = $target" : string.Empty;
            var programClause = !string.IsNullOrWhiteSpace(programNeedle) && programNeedle != "番組" ? "AND message LIKE $program" : string.Empty;
            cmd.CommandText = $"""
                DELETE FROM user_event_logs
                WHERE category = '録画'
                  AND result = 'FAILED'
                  AND code IN ('REC_FAILED', 'REC_RESULT_FAILED', 'REC_FILE_VERIFY_FAILED', 'REC_RECOVERY_FAILED')
                  {targetClause}
                  {programClause};
                """;
            if (!string.IsNullOrWhiteSpace(targetClause))
                cmd.Parameters.AddWithValue("$target", target);
            if (!string.IsNullOrWhiteSpace(programClause))
                cmd.Parameters.AddWithValue("$program", "%" + programNeedle + "%");
            cmd.ExecuteNonQuery();
        }
    }

    private void CleanupResolvedChainSwitchFailure(LogEntry entry)
    {
        var ev = (entry.Event ?? string.Empty).ToUpperInvariant();
        if (ev != "CHAIN_BOUNDARY_RESTART") return;

        var msg = entry.Message ?? string.Empty;
        var upperMsg = msg.ToUpperInvariant();
        if (!(upperMsg.Contains("RESULT=SUCCESS") || upperMsg.Contains("SUCCESS"))) return;

        var target = BuildReservationUserTarget(msg);

        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();

            // v0.11.86:
            // チェーン境界では内部的な一時失敗後に再試行成功することがある。
            // ユーザー向けログでは後続成功が確認できた失敗を残さない。
            if (!string.IsNullOrWhiteSpace(target) && target != "—")
            {
                cmd.CommandText = """
                    DELETE FROM user_event_logs
                    WHERE category = '録画'
                      AND result = 'FAILED'
                      AND code = 'CHAIN_SWITCH_FAILED'
                      AND target = $target;
                    """;
                cmd.Parameters.AddWithValue("$target", target);
            }
            else
            {
                cmd.CommandText = """
                    DELETE FROM user_event_logs
                    WHERE category = '録画'
                      AND result = 'FAILED'
                      AND code = 'CHAIN_SWITCH_FAILED';
                    """;
            }

            cmd.ExecuteNonQuery();
        }
    }

    private static IEnumerable<UserEventLogEntry> ReadEntries(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return new UserEventLogEntry
            {
                Id = reader.GetInt32(0),
                CreatedAt = DateTime.TryParse(reader.GetString(1), out var dt) ? dt : DateTime.Now,
                Severity = reader.GetString(2),
                Category = reader.GetString(3),
                Result = reader.GetString(4),
                Target = reader.GetString(5),
                Message = reader.GetString(6),
                Code = reader.GetString(7),
                TraceId = reader.GetString(8),
                OperationId = SafeReaderString(reader, 9),
                AppVersion = SafeReaderString(reader, 10),
                PreviousAppVersion = SafeReaderString(reader, 11),
                VersionChanged = SafeReaderBool(reader, 12),
                StoryContext = SafeReaderString(reader, 13),
                Origin = SafeReaderString(reader, 14),
                Trigger = SafeReaderString(reader, 15),
                StoryRole = SafeReaderString(reader, 16),
                TargetKind = SafeReaderString(reader, 17),
                Actionability = SafeReaderString(reader, 18),
                ReservationSource = SafeReaderString(reader, 19),
                ProgramTitle = SafeReaderString(reader, 20),
                Detail = SafeReaderString(reader, 21)
            };
        }
    }

    private static string SafeReaderString(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return ordinal < reader.FieldCount && !reader.IsDBNull(ordinal) ? Convert.ToString(reader.GetValue(ordinal)) ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool SafeReaderBool(SqliteDataReader reader, int ordinal)
    {
        var value = SafeReaderString(reader, ordinal);
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void Prune(SqliteConnection con)
    {
        var cutoff = DateTime.Now.Subtract(Retention).ToString("O");
        using (var old = con.CreateCommand())
        {
            old.CommandText = "DELETE FROM user_event_logs WHERE created_at < $cutoff;";
            old.Parameters.AddWithValue("$cutoff", cutoff);
            old.ExecuteNonQuery();
        }
        using (var stale = con.CreateCommand())
        {
            stale.CommandText = """
                DELETE FROM user_event_logs
                WHERE category = 'Wake'
                   OR code LIKE 'WAKE_%'
                   OR code LIKE '%DROP%'
                   OR code LIKE '%QUALITY%'
                   OR code IN ('VIEWER_PROCESS_LOST', 'PLUGIN_WARN', 'PLUGIN_LOAD_OK')
                   OR (category = 'プラグイン' AND result = 'OK')
                   OR (category = 'EPG' AND code NOT IN ('EPG_RUN_START', 'EPG_RUN_OK', 'EPG_RUN_PARTIAL', 'EPG_RUN_BLOCKED', 'EPG_RUN_FAILED', 'EPG_RUN_CANCELLED'))
                   OR (category = '録画' AND code IN ('REC_START_OK', 'REC_END_OK') AND message LIKE '%EPG確認%')
                   OR target GLOB 'R[0-9]*'
                   OR message GLOB '*R[0-9]*';
                """;
            stale.ExecuteNonQuery();
        }
        using (var overflow = con.CreateCommand())
        {
            overflow.CommandText = """
                DELETE FROM user_event_logs
                WHERE id NOT IN (
                    SELECT id FROM user_event_logs
                    ORDER BY created_at DESC, id DESC
                    LIMIT $maxRows
                );
                """;
            overflow.Parameters.AddWithValue("$maxRows", MaxRows);
            overflow.ExecuteNonQuery();
        }
    }

    public string BuildReportText(TimeSpan window, int maxRows, string tvairVersion)
    {
        var entries = GetReportEntries(window, maxRows).OrderBy(e => e.CreatedAt).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"TvAIr {tvairVersion} ユーザー運用ログ");
        sb.AppendLine($"出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
        sb.AppendLine($"対象範囲: 直近{FormatWindow(window)} / 件数={entries.Length}");
        sb.AppendLine("----");
        foreach (var e in entries)
        {
            sb.AppendLine($"[{e.CreatedAt:yyyy/MM/dd HH:mm:ss}] {e.Severity} {e.Category}/{e.Result}");
            AppendReportLine(sb, "対象", e.Target);
            AppendReportLine(sb, "内容", e.Message);
            AppendReportLine(sb, "番組", e.ProgramTitle);
            AppendReportLine(sb, "予約もと", e.ReservationSource);

            var attributes = BuildReportAttributes(e);
            if (attributes.Count > 0)
                sb.AppendLine("  分類: " + string.Join(" ", attributes));

            var trace = BuildReportTrace(e);
            if (trace.Count > 0)
                sb.AppendLine("  追跡: " + string.Join(" ", trace));

            var detail = CleanReportDetail(e.Detail);
            if (!string.IsNullOrWhiteSpace(detail))
                sb.AppendLine("  詳細: " + detail);
        }
        return sb.ToString();
    }

    private static void AppendReportLine(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();
        if (v == "—" || v == "-") return;
        sb.AppendLine($"  {label}: {v}");
    }

    private static List<string> BuildReportAttributes(UserEventLogEntry e)
    {
        var parts = new List<string>();
        AddReportAttribute(parts, "origin", e.Origin);
        AddReportAttribute(parts, "trigger", e.Trigger);
        AddReportAttribute(parts, "role", e.StoryRole);
        AddReportAttribute(parts, "targetKind", e.TargetKind);
        AddReportAttribute(parts, "actionability", e.Actionability);
        AddReportAttribute(parts, "story", e.StoryContext);
        AddReportAttribute(parts, "version", e.AppVersion);
        AddReportAttribute(parts, "previousVersion", e.PreviousAppVersion);
        if (e.VersionChanged) parts.Add("versionChanged=true");
        return parts;
    }

    private static List<string> BuildReportTrace(UserEventLogEntry e)
    {
        var parts = new List<string>();
        AddReportAttribute(parts, "operationId", string.IsNullOrWhiteSpace(e.OperationId) ? e.TraceId : e.OperationId);
        AddReportAttribute(parts, "resultCode", e.Code);
        return parts;
    }

    private static void AddReportAttribute(List<string> parts, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();
        if (v == "—" || v == "-" || string.Equals(v, "Unknown", StringComparison.OrdinalIgnoreCase)) return;
        parts.Add($"{key}={v}");
    }

    private static string CleanReportDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        var parts = detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("source=UserOperationEvent", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.StartsWith("display=", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.StartsWith("report=", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith("=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return string.Join("; ", parts);
    }

    private static string FormatWindow(TimeSpan window)
    {
        if (window.TotalDays >= 1) return $"{Math.Round(window.TotalDays)}日";
        if (window.TotalHours >= 1) return $"{Math.Round(window.TotalHours)}時間";
        return $"{Math.Round(window.TotalMinutes)}分";
    }

    private enum UserEventOrigin
    {
        Unknown,
        UserAction,
        AppLifecycle,
        Recording,
        PreRecordEpg,
        Epg,
        Wake,
        Plugin,
        Viewer,
        InternalDiagnostic
    }

    private enum UserEventRole
    {
        Unknown,
        Start,
        Stop,
        Completed,
        Failed,
        Cancelled,
        Warning,
        Recovery,
        ChainSwitch,
        TimeFollow,
        InternalSignal
    }

    private enum UserEventVisibility
    {
        Suppress,
        UserVisible
    }

    private sealed record UserEventStory(
        LogEntry Source,
        UserEventOrigin Origin,
        UserEventRole Role,
        UserEventVisibility Visibility,
        string Severity,
        string Category,
        string Result,
        string Target,
        string Message,
        string Code,
        bool UserActionable = false);

    private UserEventLogEntry? TryMap(LogEntry entry)
    {
        var story = ClassifyUserEvent(entry);
        return RenderUserEventStory(story);
    }

    private UserEventLogEntry? RenderUserEventStory(UserEventStory? story)
    {
        if (story is null || story.Visibility != UserEventVisibility.UserVisible)
            return null;

        var entry = New(
            story.Source,
            story.Severity,
            story.Category,
            story.Result,
            story.Target,
            story.Message,
            story.Code);
        entry.OperationId = BuildOperationId(story.Code, story.Source.CreatedAt);
        entry.TraceId = entry.OperationId;
        entry.StoryContext = InferStoryContext(story);
        entry.Origin = story.Origin.ToString();
        entry.Trigger = InferTrigger(story);
        entry.StoryRole = story.Role.ToString();
        entry.TargetKind = story.Category;
        entry.Actionability = story.UserActionable ? "UserCanCheck" : "NoAction";
        entry.ReservationSource = NormalizeReservationSourceLabel(ReadToken(story.Source.Message ?? string.Empty, "source"));
        entry.ProgramTitle = ExtractProgramTitleForReport(story.Source.Message ?? string.Empty, story.Message);
        entry.Detail = BuildOperationDetail(story);
        return entry;
    }

    private UserEventStory? ClassifyUserEvent(LogEntry entry)
    {
        var ev = entry.Event ?? string.Empty;
        var title = entry.Title ?? string.Empty;
        var msg = entry.Message ?? string.Empty;
        var upperEv = ev.ToUpperInvariant();
        var upperTitle = title.ToUpperInvariant();
        var upperMsg = msg.ToUpperInvariant();

        // v0.11.101:
        // ユーザー向けログは event 名から直接文言を作らない。
        // いったん Origin/Role/Visibility/Actionability を持つ UserEventStory に分類し、
        // その結果だけを表示へ変換する。判定不能な内部シグナルは出さない。

        // v0.11.109:
        // Wake/EPG/録画前EPG/チェーン/プラグインのユーザー運用ログは本線から直接発行する。
        // /api/log 由来の詳細ログを UserOperationEvent 正本へ変換しない。
        if (upperEv == "WAKE_REGISTER_CRITICAL") return null;

        if (IsUserEventNoise(upperEv, upperTitle, upperMsg))
            return null;

        // APP_LIFECYCLE START は旧実装の時間間隔では判断しない。
        // 起動元メタ情報がないものは、Wake/既存プロセスシグナル/録画準備と区別できないため非表示。
        if (upperEv == "APP_LIFECYCLE" && upperTitle == "START")
        {
            if (IsUserVisibleAppLifecycleStart(msg, upperMsg))
            {
                return new UserEventStory(entry, UserEventOrigin.AppLifecycle, UserEventRole.Start,
                    UserEventVisibility.UserVisible, "INFO", "起動", "OK", "TvAIr",
                    "TvAIrを起動しました", "APP_START_OK");
            }
            return new UserEventStory(entry, UserEventOrigin.AppLifecycle, UserEventRole.InternalSignal,
                UserEventVisibility.Suppress, "INFO", "起動", "OK", "TvAIr",
                "判定不能な起動ログを抑制しました", "APP_START_SUPPRESSED");
        }

        if (upperEv == "APP_LIFECYCLE" && upperTitle == "STOPPED")
        {
            if (IsUserVisibleAppLifecycleStop(msg, upperMsg))
            {
                return new UserEventStory(entry, UserEventOrigin.AppLifecycle, UserEventRole.Stop,
                    UserEventVisibility.UserVisible, "INFO", "起動", "STOP", "TvAIr",
                    "TvAIrを終了しました", "APP_STOP_OK");
            }
            return null;
        }

        if (upperEv == "PLUGIN") return null;

        // v0.11.109:
        // 予約/録画のユーザー運用ログは ReservationStore/録画本線から直接発行する。
        // RESERVATION_AUDIT は /api/log 向け監査ログであり、TrimForAudit 済み文字列を含むため、
        // UserOperationEvent 正本へ変換しない。
        if (upperEv == "RESERVATION_AUDIT"
            && (upperTitle == "ADD" || upperTitle == "STATUS" || upperTitle == "DELETE" || upperTitle == "ENABLED_FLAG"))
            return null;

        if (upperEv == "REC_INTERRUPTED_DETECTED")
        {
            // v0.11.137:
            // 起動時復旧の録画中断は ReservationStore 側で予約状態を Failed へ終端化し、
            // UserOperationEvent も REC_INTERRUPTED として直接発行する。
            // ここで開発ログから推測生成すると INFO/STOP と ERROR/FAILED が二重化するため出さない。
            return null;
        }

        if (upperEv == "REC_STARTUP_RECOVERY_REQUEUE")
        {
            var target = BuildReservationUserTarget(msg);
            var program = BuildProgramLabel(msg);
            if (upperMsg.Contains("RESULT=REQUEUED_AS_NEW"))
                return new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Recovery,
                    UserEventVisibility.UserVisible, "INFO", "録画", "OK", target,
                    $"録画を再開しました: {program}", "REC_RECOVERY_STARTED");
            return null;
        }

        if (upperEv == "REC_WRITE_STALLED")
        {
            var target = BuildReservationUserTarget(msg);
            var program = BuildProgramLabel(msg);
            return new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Failed,
                UserEventVisibility.UserVisible, "ERROR", "録画", "FAILED", target,
                $"録画データが途中で止まりました: {program}", "REC_WRITE_STALLED", UserActionable: true);
        }

        if (upperEv == "RESERVATION_AUDIT" && upperTitle == "STATUS")
        {
            var to = ReadToken(msg, "to");
            if (string.IsNullOrWhiteSpace(to)) return null;
            var target = BuildReservationUserTarget(msg);
            var program = BuildProgramLabel(msg);

            if (program.Contains("EPG確認", StringComparison.OrdinalIgnoreCase))
                return null;

            var from = ReadToken(msg, "from");
            return to.ToLowerInvariant() switch
            {
                "recording" => new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Start,
                    UserEventVisibility.UserVisible, "INFO", "録画", "OK", target,
                    $"録画を開始しました: {program}", "REC_START_OK"),
                "completed" => new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Completed,
                    UserEventVisibility.UserVisible, "INFO", "録画", "OK", target,
                    $"録画を終了しました: {program}", "REC_END_OK"),
                "failed" => new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Failed,
                    UserEventVisibility.UserVisible, "ERROR", "録画", "FAILED", target,
                    BuildRecordingFailureMessage(program, msg), "REC_FAILED", UserActionable: true),
                "cancelled" when string.Equals(from, "Recording", StringComparison.OrdinalIgnoreCase)
                    => new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Cancelled,
                        UserEventVisibility.UserVisible, "INFO", "録画", "CANCEL", target,
                        $"録画をキャンセルしました: {program}", "REC_CANCELLED"),
                "cancelled"
                    => new UserEventStory(entry, UserEventOrigin.UserAction, UserEventRole.Cancelled,
                        UserEventVisibility.UserVisible, "INFO", "予約", "CANCEL", target,
                        $"予約をキャンセルしました: {program}", "RESERVATION_CANCELLED"),
                _ => null
            };
        }

        if (upperEv is "REC_START_MODE" or "TVAIREPGREC_RECORD_START")
            return null;
        if (upperEv == "TVAIREPGREC_FINAL_STATUS" && upperMsg.Contains("RECORDING_FILE_GROWTH_STALLED"))
            return null;
        if (upperEv == "TVAIREPGREC_FINAL_STATUS" && IsMajorRecordingFinalFailure(upperMsg))
        {
            var failureMessage = BuildRecordingFailureMessage(BuildProgramLabel(msg), msg);
            if (string.IsNullOrWhiteSpace(failureMessage)) return null;
            return new UserEventStory(entry, UserEventOrigin.Recording, UserEventRole.Failed,
                UserEventVisibility.UserVisible, "ERROR", "録画", "FAILED", BuildReservationUserTarget(msg),
                failureMessage, "REC_RESULT_FAILED", UserActionable: true);
        }
        if (upperEv == "REC_TS_VERIFY" && IsMajorRecordingVerifyFailure(upperMsg))
            return null;

        if (upperEv == "CHAIN_BOUNDARY_RESTART") return null;

        if (IsEpgRunUserEvent(upperEv, upperTitle, upperMsg)) return null;

        if (upperEv == "PRE_REC_EPG_START" || upperEv == "PRE_REC_EPG_RESULT") return null;
        if (upperEv == "EPG_SCHEDULER" && upperTitle.StartsWith("TIMEFOLLOW", StringComparison.OrdinalIgnoreCase)) return null;

        if (upperEv.Contains("VIEWER") || upperEv.Contains("PLUGIN_ACTION_VIEWER"))
        {
            if (IsResolvedViewerDiagnosticOnly(upperEv, upperMsg))
                return null;

            var reason = BuildViewerFailureMessage(upperMsg);
            if (reason is null) return null;
            return new UserEventStory(entry, UserEventOrigin.Viewer, UserEventRole.Failed,
                UserEventVisibility.UserVisible, "ERROR", "視聴", "FAILED", BuildViewerTarget(msg),
                reason, "VIEWER_ACTION_FAILED", UserActionable: true);
        }

        if (upperEv == "TRAY_PLUGIN_MENU" && (upperMsg.Contains("FAILED") || upperMsg.Contains("WARN")))
            return new UserEventStory(entry, UserEventOrigin.Plugin, UserEventRole.Failed,
                UserEventVisibility.UserVisible, "ERROR", "プラグイン", "FAILED", SafeDisplayTarget(title),
                "プラグイン画面を開けませんでした。", "PLUGIN_MENU_FAILED", UserActionable: true);

        return null;
    }

    private static bool IsUserVisibleAppLifecycleStart(string msg, string upperMsg)
    {
        // 既存ログの APP_LIFECYCLE START だけでは、手動起動・Wakeシグナル・録画準備・既存プロセス確認を識別できない。
        // 明示メタ属性がある場合だけユーザー向けにする。判定不能は安全側で非表示。
        var userVisible = ReadToken(msg, "userVisible");
        if (string.Equals(userVisible, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(userVisible, "false", StringComparison.OrdinalIgnoreCase)) return false;

        var startupKind = ReadToken(msg, "startupKind");
        if (string.IsNullOrWhiteSpace(startupKind)) startupKind = ReadToken(msg, "startKind");
        if (string.IsNullOrWhiteSpace(startupKind)) startupKind = ReadToken(msg, "reason");
        if (string.IsNullOrWhiteSpace(startupKind)) return false;

        var kind = startupKind.ToUpperInvariant();
        if (kind is "MANUAL" or "USER" or "USER_MANUAL" or "PROCESS_START" or "PC_START" or "OS_START" or "UPDATE_RESTART" or "RECOVERY_RESTART")
            return true;
        if (kind.Contains("WAKE") || kind.Contains("SIGNAL") || kind.Contains("PRE_REC") || kind.Contains("RECORD") || kind.Contains("INTERNAL"))
            return false;
        return false;
    }

    private static bool IsUserVisibleAppLifecycleStop(string msg, string upperMsg)
    {
        var userVisible = ReadToken(msg, "userVisible");
        if (string.Equals(userVisible, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(userVisible, "false", StringComparison.OrdinalIgnoreCase)) return false;

        var stopKind = ReadToken(msg, "stopKind");
        if (string.IsNullOrWhiteSpace(stopKind)) stopKind = ReadToken(msg, "reason");
        if (string.IsNullOrWhiteSpace(stopKind)) return false;
        var kind = stopKind.ToUpperInvariant();
        if (kind is "MANUAL" or "USER" or "UPDATE_RESTART" or "SHUTDOWN") return true;
        if (kind.Contains("WAKE") || kind.Contains("SIGNAL") || kind.Contains("INTERNAL")) return false;
        return false;
    }

    private static string InferStoryContext(UserEventStory story)
    {
        return story.Origin switch
        {
            UserEventOrigin.Wake when story.Role == UserEventRole.Failed => "WakeRegistrationFailed",
            UserEventOrigin.PreRecordEpg when story.Role == UserEventRole.Failed => "PreRecordEpgFallbackToReservedTime",
            UserEventOrigin.Recording when story.Role == UserEventRole.ChainSwitch => "ChainBoundarySwitch",
            UserEventOrigin.Recording when story.Role == UserEventRole.Start => "RecordingStart",
            UserEventOrigin.Recording when story.Role == UserEventRole.Completed => "RecordingCompleted",
            UserEventOrigin.Epg when story.Role == UserEventRole.Start => "ScheduledEpgStart",
            UserEventOrigin.Epg when story.Role == UserEventRole.Completed => "ScheduledEpgCompleted",
            _ => story.Role.ToString()
        };
    }

    private static string InferTrigger(UserEventStory story)
    {
        var msg = story.Source.Message ?? string.Empty;
        var trigger = ReadToken(msg, "trigger");
        if (string.IsNullOrWhiteSpace(trigger)) trigger = ReadToken(msg, "event_trigger");
        if (!string.IsNullOrWhiteSpace(trigger)) return SafeText(trigger);

        return story.Origin switch
        {
            UserEventOrigin.AppLifecycle => "ProcessStart",
            UserEventOrigin.UserAction => "UserAction",
            UserEventOrigin.Recording when story.Role == UserEventRole.ChainSwitch => "ChainBoundary",
            UserEventOrigin.Recording => "RecordingPipeline",
            UserEventOrigin.PreRecordEpg => "PreRecordCheck",
            UserEventOrigin.Epg => "EpgSchedule",
            UserEventOrigin.Wake => "WakeTask",
            UserEventOrigin.Plugin => "PluginHost",
            UserEventOrigin.Viewer => "ViewerAction",
            _ => "Unknown"
        };
    }

    private static string BuildOperationDetail(UserEventStory story)
    {
        var msg = story.Source.Message ?? string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(story.Source.Event)) parts.Add($"event={SafeText(story.Source.Event)}");
        if (!string.IsNullOrWhiteSpace(story.Source.Title)) parts.Add($"title={SafeText(story.Source.Title)}");

        var id = ReadTokenLoose(msg, "id");
        if (!string.IsNullOrWhiteSpace(id)) parts.Add($"reservationId={SafeText(id)}");
        var parent = ReadTokenLoose(msg, "parent");
        if (!string.IsNullOrWhiteSpace(parent)) parts.Add($"parent={SafeText(parent)}");
        var source = ReadTokenLoose(msg, "source");
        if (!string.IsNullOrWhiteSpace(source)) parts.Add($"reservationSource={NormalizeReservationSourceLabel(source)}");
        var result = ReadTokenLoose(msg, "result");
        if (!string.IsNullOrWhiteSpace(result)) parts.Add($"result={SafeText(result)}");
        var service = ReadTokenLoose(msg, "service");
        if (!string.IsNullOrWhiteSpace(service)) parts.Add($"service={SafeText(service)}");
        var title = ReadTokenLoose(msg, "title");
        if (!string.IsNullOrWhiteSpace(title)) parts.Add($"programTitle={SafeText(title)}");
        return string.Join("; ", parts);
    }

    private static string ExtractProgramTitleForReport(string sourceMessage, string displayMessage)
    {
        var title = ReadTokenLoose(sourceMessage, "title");
        if (!string.IsNullOrWhiteSpace(title)) return SafeText(title);
        var idx = displayMessage.IndexOf(':');
        if (idx >= 0 && idx + 1 < displayMessage.Length) return SafeText(displayMessage[(idx + 1)..].Trim());
        return string.Empty;
    }

    private static string NormalizeReservationSourceLabel(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var s = source.Trim();
        return s.ToLowerInvariant() switch
        {
            "manual" or "keywordsearch" or "immediate" => "番組表",
            "keyword" or "autokeyword" or "auto" => "自動検索",
            "program" => "プログラム",
            "epg" or "system" => "システム",
            _ => SafeText(s)
        };
    }

    private static string BuildWakeTarget(string msg)
    {
        var firstFailed = ReadTokenLoose(msg, "firstFailed");
        if (!string.IsNullOrWhiteSpace(firstFailed))
            return SafeDisplayTarget(firstFailed);
        var title = ReadTokenLoose(msg, "title");
        if (!string.IsNullOrWhiteSpace(title))
            return SafeDisplayTarget(title);
        var reservation = ReadTokenLoose(msg, "reservation");
        if (!string.IsNullOrWhiteSpace(reservation))
            return SafeDisplayTarget(reservation);
        return "録画復帰";
    }

    private static bool IsPluginLoaderFatalFailure(string upperMsg)
    {
        // PluginLoader由来のメッセージは "[Plugin] Error:" / "[Plugin] Blocked:" で始まる。
        // PluginContext.Log(Error, ...) 等のプラグイン内部ログはロード失敗扱いしない。
        if (!(upperMsg.Contains("[PLUGIN] ERROR") || upperMsg.Contains("[PLUGIN] BLOCKED")))
            return false;

        if (upperMsg.Contains("ロード失敗")) return true;
        if (upperMsg.Contains("インスタンス生成失敗")) return true;
        if (upperMsg.Contains("INITIALIZE 失敗") || upperMsg.Contains("INITIALIZE FAILED")) return true;
        if (upperMsg.Contains("ONSTART 失敗") || upperMsg.Contains("ONSTART FAILED")) return true;
        if (upperMsg.Contains("BLOCKED")) return true;
        return false;
    }

    private static bool IsUserEventNoise(string upperEv, string upperTitle, string upperMsg)
    {
        // Wakeはユーザー向け運用ログに出さない。必要時は録画失敗等の文脈で別イベント化する。
        if (upperEv.Contains("WAKE") || upperTitle.Contains("WAKE")) return true;

        // EPGの予定・起動時同期・登録対象なし・遅延実行抑止は取得実行ではないため出さない。
        if (upperEv is "EPG_SCHEDULER_START" or "EPG_SCHEDULER_DUE" or "EPG_SCHEDULER_NEXT" or "EPG_DURATION_POLICY" or "EPG_ORPHAN_SAFETY") return true;
        if (upperEv is "EPG_PREEMPT_FILTER" or "EPG_PREEMPT_COOLDOWN_END" or "PRE_REC_EPG_DEDUPE" or "PRE_REC_EPG_DUE_SCAN") return true;
        if (upperMsg.Contains("MISSED_WAKE_CATCHUP_SUPPRESSED") || upperMsg.Contains("NO_REGISTER") || upperMsg.Contains("STARTUPSYNC")) return true;

        // 軽微DROP/品質相関/transport warnはユーザーが事後対策できないため出さない。
        if (upperEv.Contains("DROP") || upperEv.Contains("QUALITY") || upperEv.Contains("RUNTIME_STATS")) return true;
        if (upperMsg.Contains("OUTPUTDROPS=") || upperMsg.Contains("WARN_OUTPUT") || upperMsg.Contains("TRANSPORT_DAMAGE")) return true;

        // 周期監査・内部trace・チューナー/割当詳細は /api/log 側。
        if (upperEv.Contains("ALLOC") || upperEv.Contains("TUNER_TRACE") || upperEv.Contains("TUNER_ALLOC") || upperEv.Contains("TUNER_SKIP")) return true;
        if (upperEv.Contains("CHAIN_TRACE") || upperEv.Contains("CHAIN_AUDIT") || upperEv.Contains("CHAIN_SESSION") || upperEv.Contains("CHAIN_RECORDING_EVAL")) return true;
        if (upperEv.Contains("RESERVATION_PIPELINE") || upperEv.Contains("TUNER_PIPELINE") || upperEv.Contains("RECORD_FILENAME") || upperEv.Contains("RECORD_FILE_PATH")) return true;
        if (upperEv.Contains("PROCESS_OWNERSHIP") || upperEv.Contains("TVTEST_PROCESS") || upperEv.Contains("VIEWING_PROTECTION")) return true;
        if (upperEv.Contains("PLUGIN_SAFE_EVENT") || upperEv.Contains("PLUGIN_RENDER") || upperEv.Contains("PLUGIN_WINDOW_REFRESH_SCROLL")) return true;
        if (upperEv.Contains("PLUGIN_UI_CONTEXT") || upperEv.Contains("WINDOW_STATE_ENDPOINT_CONTRACT")) return true;
        if (upperEv.Contains("TIMEFOLLOWAUDIT") || upperEv.Contains("TIME_FOLLOW_AUDIT")) return true;
        if (upperEv.Contains("PLUGIN_LIVE_COMMENT") || upperMsg.Contains("COMMENT受信")) return true;
        if (upperTitle.Contains("TICKWINDOW") || upperEv.Contains("SCHEDULER_NEXT")) return true;

        return false;
    }

    private static bool IsUserInitiatedCancellation(string upperMsg)
    {
        return upperMsg.Contains("USER")
            || upperMsg.Contains("MANUAL")
            || upperMsg.Contains("REQUESTED_BY_USER")
            || upperMsg.Contains("USER_CANCEL")
            || upperMsg.Contains("UI_CANCEL")
            || upperMsg.Contains("TRAY_CANCEL");
    }

    private static bool IsEpgRunUserEvent(string upperEv, string upperTitle, string upperMsg)
    {
        return upperEv is "EPG_RUN_START"
            or "EPG_RUN_OK"
            or "EPG_RUN_PARTIAL"
            or "EPG_RUN_BLOCKED"
            or "EPG_RUN_FAIL"
            or "EPG_RUN_ERROR"
            or "EPG_RUN_CANCELLED"
            or "EPG_RUN_END";
    }

    private static bool IsMajorRecordingFinalFailure(string upperMsg)
    {
        // OK系・Completed系・stop boundary由来のtitle warningは失敗ではない。
        // "not_recording_failure" のような説明文字列に FAIL が含まれていてもユーザー向けFAILEDへ昇格しない。
        if (upperMsg.Contains("RESULT=OK")
            || upperMsg.Contains("SUCCESS=TRUE")
            || upperMsg.Contains("FINALSTATUS=COMPLETED")
            || upperMsg.Contains("OK_CLEAR")
            || upperMsg.Contains("LIVE_CLEAR")
            || upperMsg.Contains("NOT_RECORDING_FAILURE")
            || upperMsg.Contains("CLEAR_TS_OK"))
            return false;

        return upperMsg.Contains("FINALSTATUS=FAILED")
            || upperMsg.Contains("SUCCESS=FALSE")
            || upperMsg.Contains("EXITCODE=") && !upperMsg.Contains("EXITCODE=0")
            || upperMsg.Contains("UNREADABLE")
            || upperMsg.Contains("MISSING")
            || upperMsg.Contains("SCRAMBLED_REMAINING")
            || upperMsg.Contains("RESULT=FAILED")
            || upperMsg.Contains("RESULT=ERROR");
    }

    private static bool IsMajorRecordingVerifyFailure(string upperMsg)
    {
        if (upperMsg.Contains("RESULT=OK")
            || upperMsg.Contains("OK_EVENT_TITLE_BOUNDARY_WARN")
            || upperMsg.Contains("BOUNDARY_WARN")
            || upperMsg.Contains("CLEAR_TS_OK")
            || upperMsg.Contains("LIVE_CLEAR")
            || upperMsg.Contains("NOT_RECORDING_FAILURE")
            || upperMsg.Contains("CLEARENoughForCompleted=TRUE".ToUpperInvariant()))
            return false;

        return upperMsg.Contains("RESULT=FAILED")
            || upperMsg.Contains("RESULT=ERROR")
            || upperMsg.Contains("UNREADABLE")
            || upperMsg.Contains("SCRAMBLED_REMAINING")
            || upperMsg.Contains("MISSING")
            || upperMsg.Contains("FILESIZE=0")
            || upperMsg.Contains("SIZE=0");
    }

    private static string BuildReservationUserTarget(string msg)
    {
        // ユーザー画面では予約IDを使わない。局名だけを対象欄に出す。
        var service = ReadTokenLoose(msg, "service");
        if (!string.IsNullOrWhiteSpace(service)) return SafeDisplayTarget(service);
        var currentService = ReadTokenLoose(msg, "currentService");
        if (!string.IsNullOrWhiteSpace(currentService)) return SafeDisplayTarget(currentService);
        var nextService = ReadTokenLoose(msg, "nextService");
        if (!string.IsNullOrWhiteSpace(nextService)) return SafeDisplayTarget(nextService);
        return "—";
    }

    private static string BuildProgramLabel(string msg)
    {
        var title = ReadTokenLoose(msg, "title");
        if (string.IsNullOrWhiteSpace(title)) title = ReadTokenLoose(msg, "currentTitle");
        if (string.IsNullOrWhiteSpace(title)) title = ReadTokenLoose(msg, "expectedTitle");
        return string.IsNullOrWhiteSpace(title) ? "番組" : SafeMessageText(title);
    }

    private static string BuildRecordingFailureMessage(string program, string msg)
    {
        var upper = (msg ?? string.Empty).ToUpperInvariant();
        var tunerShortage = upper.Contains("TUNER_LIMIT_EXCEEDED")
            || upper.Contains("NO_FREE_TUNER")
            || upper.Contains("TUNER SHORTAGE")
            || upper.Contains("チューナー不足")
            || upper.Contains("CONFLICTED=TRUE")
            || upper.Contains("CONFLICT_STILL_TRUE");

        if (tunerShortage)
            return $"チューナー不足で録画できませんでした: {program}";
        if (upper.Contains("REC_WRITE_STALLED") || upper.Contains("RECORDING_FILE_GROWTH_STALLED") || upper.Contains("NO_RECORDING_DATA_AFTER_INITIAL_GRACE") || upper.Contains("RECORDING_FILE_MISSING_AFTER_INITIAL_GRACE"))
            return $"録画データが途中で止まりました: {program}";
        if (upper.Contains("SCRAMBLED_REMAINING") || upper.Contains("SCRAMBLE") || upper.Contains("B25"))
            return $"スクランブル解除に失敗しました: {program}";
        if (upper.Contains("UNAUTHORIZEDACCESSEXCEPTION") || upper.Contains("IOEXCEPTION") || upper.Contains("DISK FULL") || upper.Contains("ACCESS DENIED"))
            return "保存先に書き込めませんでした";
        if (upper.Contains("EXITCODE=") || upper.Contains("PROCESS") || upper.Contains("FINALSTATUS=FAILED"))
            return $"録画中に録画プロセスが終了しました: {program}";
        return string.Empty;
    }

    private static string BuildEpgTarget(string msg)
    {
        var scope = ReadToken(msg, "targetScope");
        if (string.IsNullOrWhiteSpace(scope)) scope = ReadToken(msg, "scope");
        if (string.IsNullOrWhiteSpace(scope)) scope = ReadToken(msg, "group");
        if (string.IsNullOrWhiteSpace(scope)) scope = ReadToken(msg, "target");
        return string.IsNullOrWhiteSpace(scope) ? "全体" : SafeDisplayTarget(scope);
    }

    private static bool IsResolvedViewerDiagnosticOnly(string upperEv, string upperMsg)
    {
        // 古いviewer lease / delayed-death監査は、viewerStart前の清掃・診断であり最終結果ではない。
        // ここをFAILED表示すると「TVTestが終了したため視聴切替失敗」と誤認させる。
        if (upperEv.Contains("VIEWER_RETUNE_DELAYED_DEATH_AUDIT")) return true;
        if (upperMsg.Contains("STALE_LEASE_RELEASED")) return true;
        if (upperMsg.Contains("STALE_VIEWER_LEASE_CLEANUP_BEFORE_START")) return true;
        if (upperMsg.Contains("EXISTING_PROCESS_NOT_ALIVE_BEFORE_LIGHT_RETUNE")) return true;
        if (upperMsg.Contains("RESULT=ACCEPTED") || upperMsg.Contains("SUCCESS=TRUE")) return true;
        return false;
    }

    private static string? BuildViewerFailureMessage(string upperMsg)
    {
        if (upperMsg.Contains("EXISTING_PROCESS_LOST") || upperMsg.Contains("PROCESS_LOST") || upperMsg.Contains("NOT_ALIVE") || (upperMsg.Contains("SURVIVAL") && upperMsg.Contains("FAILED")))
            return "視聴切替に失敗しました: TVTestが終了しました。";
        if (upperMsg.Contains("TOKEN_NOT_FOUND") || upperMsg.Contains("TOKEN_EXPIRED") || upperMsg.Contains("SESSION") && upperMsg.Contains("DENIED"))
            return "視聴切替に失敗しました: AIrConを開き直してください。";
        if (upperMsg.Contains("CHANNEL") && (upperMsg.Contains("NOT_FOUND") || upperMsg.Contains("RESOLVE") && upperMsg.Contains("FAILED")))
            return "視聴切替に失敗しました: チャンネルを解決できませんでした。";
        if (upperMsg.Contains("FAILED") || upperMsg.Contains("ERROR"))
            return null; // 原因が曖昧なFAILEDは不安をあおるためユーザー向けには出さない。
        return null;
    }

    private static string SafeDisplayTarget(string? value)
    {
        var s = SafeText(value);
        return s.Length <= 32 ? s : s[..32] + "…";
    }

    private static string ReadTokenLoose(string message, string key)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var m = Regex.Match(message, $@"(?:^|\s){Regex.Escape(key)}=", RegexOptions.IgnoreCase);
        if (!m.Success) return string.Empty;
        var start = m.Index + m.Length;
        var next = Regex.Match(message[start..], @"\s[a-zA-Z][a-zA-Z0-9_]*=");
        var raw = next.Success ? message.Substring(start, next.Index) : message[start..];
        return raw.Trim().Trim('"');
    }

    private static UserEventLogEntry New(LogEntry src, string severity, string category, string result, string target, string message, string code)
        => new()
        {
            CreatedAt = src.CreatedAt,
            Severity = severity,
            Category = category,
            Result = result,
            Target = SafeText(target),
            Message = SafeMessageText(message),
            Code = code,
            TraceId = BuildTraceId(src)
        };

    private static string BuildTraceId(LogEntry src)
    {
        var seed = $"{src.CreatedAt:O}|{src.Event}|{src.Title}|{src.Message}";
        var hash = Math.Abs(seed.GetHashCode()).ToString("X4");
        return $"U{src.CreatedAt:yyyyMMdd-HHmmss}-{hash}";
    }

    private static string BuildReservationTarget(string msg)
    {
        return BuildReservationUserTarget(msg);
    }

    private static string BuildViewerTarget(string msg)
    {
        var service = ReadTokenLoose(msg, "service");
        if (!string.IsNullOrWhiteSpace(service)) return SafeDisplayTarget(service);
        var requestedService = ReadTokenLoose(msg, "requestedService");
        if (!string.IsNullOrWhiteSpace(requestedService)) return SafeDisplayTarget(requestedService);
        var group = ReadToken(msg, "group");
        if (string.IsNullOrWhiteSpace(group)) group = ReadToken(msg, "requestedGroup");
        if (!string.IsNullOrWhiteSpace(group)) return SafeDisplayTarget(group);
        return "AIrCon";
    }

    private static string BuildGenericTarget(string msg)
    {
        var service = ReadTokenLoose(msg, "service");
        if (!string.IsNullOrWhiteSpace(service)) return SafeDisplayTarget(service);
        var group = ReadToken(msg, "group");
        var scope = ReadToken(msg, "scope");
        return SafeText(string.Join(" ", new[] { group, scope }.Where(x => !string.IsNullOrWhiteSpace(x))));
    }

    private static string ReadToken(string message, string key)
    {
        var match = Regex.Match(message ?? string.Empty, $@"(?:^|\s){Regex.Escape(key)}=([^\s\|,;\]]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return string.Empty;
        return match.Groups[1].Value.Trim().Trim('[', ']', '"');
    }

    private bool ShouldEmitAppStart(LogEntry entry)
    {
        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT created_at
                FROM user_event_logs
                WHERE code = 'APP_START_OK'
                ORDER BY created_at DESC, id DESC
                LIMIT 1;
                """;
            var obj = cmd.ExecuteScalar();
            if (obj is null || obj == DBNull.Value) return true;
            if (!DateTime.TryParse(Convert.ToString(obj), out var last)) return true;

            // v0.11.101:
            // APP_LIFECYCLE START は「起動」という起点メタ属性だけではユーザー表示可否を決めない。
            // 直近の録画/予約/EPGストーリーが既に存在する場合、Wake/Recovery/single-instance
            // の内部シグナルとして扱い、ユーザー向けには出さない。
            // REC_INTERRUPTED_STOP / APP_STOP_OK の直後だけは、停止→起動→再開のストーリー上必要な起点として許容する。
            if (entry.CreatedAt - last < TimeSpan.FromMinutes(30))
                return false;

            using var recent = con.CreateCommand();
            recent.CommandText = """
                SELECT code, created_at
                FROM user_event_logs
                WHERE created_at >= $since
                ORDER BY created_at DESC, id DESC
                LIMIT 1;
                """;
            recent.Parameters.AddWithValue("$since", entry.CreatedAt.Subtract(TimeSpan.FromMinutes(15)).ToString("O"));
            using var reader = recent.ExecuteReader();
            if (reader.Read())
            {
                var recentCode = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                // REC_INTERRUPTED_STOP is the explicit story bridge: app start -> interrupted -> resumed.
                if (!string.Equals(recentCode, "REC_INTERRUPTED_STOP", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(recentCode, "APP_STOP_OK", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static string NormalizeLikeNeedle(string value)
    {
        var s = SafeText(value);
        if (string.IsNullOrWhiteSpace(s) || s == "—") return string.Empty;
        if (s.Length > 64) s = s[..64];
        return s.Replace("%", "").Replace("_", "").Trim();
    }

    private static string SafeMessageText(string? value)
    {
        var s = SafeTextCore(value);
        if (string.IsNullOrWhiteSpace(s)) return "—";
        return s.Length <= 1200 ? s : s[..1200] + "…";
    }

    private static string SafeText(string? value)
    {
        var s = SafeTextCore(value);
        if (string.IsNullOrWhiteSpace(s)) return "—";
        return s.Length <= 180 ? s : s[..180] + "…";
    }

    private static string SafeTextCore(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = Regex.Replace(s, @"[A-Za-z]:\\[^\s\|,;]+", "[path]");
        s = Regex.Replace(s, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "[id]");
        s = Regex.Replace(s, @"[0-9a-fA-F]{24,}", "[id]");
        s = Regex.Replace(s, @"(?<![A-Za-z0-9_])R\d{1,8}(?![A-Za-z0-9_])", string.Empty).Trim();
        return s;
    }

}
