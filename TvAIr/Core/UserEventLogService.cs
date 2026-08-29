﻿using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TvAIrPlugin;
using TvAIr.Schedule;

namespace TvAIr.Core;

/// <summary>
/// ユーザー向けの軽量運用ログ。
/// /api/log の開発診断ログとは別に、不具合報告で貼れる最小限のイベントだけを保存する。
///
/// 予約追加・取消・有効化・無効化は予約の構造から分類し、内部予約を通常のユーザー運用ログから分離する。
/// 取消・無効化・有効化の詳細は、予約追加と同じ粒度で状態変化を記録する。
/// 起動・Wake・軽微な品質診断は開発ログへ分離し、ユーザー運用ログには日常運用に必要な事実だけを記録する。
/// - ログタブ標準: ユーザーが日常運用で確認する短い事実を必ず1行で表示する。
/// - ログタブ詳細: ユーザーが有効化し、チェックした項目だけを多段表示する。
/// - 報告用コピー: 同じ正本からユーザー可読の必要事実だけを重複なく展開する。/api/log の丸写しや内部契約名の露出は行わない。
/// - /api/log: 裏版・開発診断用。表版では切ってもログタブ/報告用コピーが残る構造にする。
/// </summary>
public sealed class UserEventLogService
{
    // USER_LOG_OUTCOME_POLICY_INVARIANT
    // 赤表示は、録画不能・録画途中終了・保存不能・データ更新不能など、
    // ユーザーの目的が最終的に成立しなかったクリティカル事象だけに限定する。
    // 代替経路で継続できる事象、再試行可能な未完了、単独機能の利用不能は
    // WARN とし、各イベント生成箇所で Severity/Result を独自判断しない。
    private readonly record struct UserLogOutcome(string Severity, string Result, string StoryRole, string Actionability);

    private static class UserLogOutcomes
    {
        public static readonly UserLogOutcome Success = new("INFO", "OK", "Completed", "NoAction");
        public static readonly UserLogOutcome Fallback = new("WARN", "FALLBACK", "Fallback", "NoAction");
        public static readonly UserLogOutcome Partial = new("WARN", "PARTIAL", "Partial", "NoAction");
        public static readonly UserLogOutcome Incomplete = new("WARN", "INCOMPLETE", "Incomplete", "UserCanCheck");
        public static readonly UserLogOutcome Blocked = new("WARN", "BLOCKED", "Blocked", "NoAction");
        public static readonly UserLogOutcome Unavailable = new("WARN", "UNAVAILABLE", "Unavailable", "UserCanCheck");
        public static readonly UserLogOutcome CriticalFailure = new("ERROR", "FAILED", "Failed", "UserCanCheck");
    }

    private enum EpgFailureImpact
    {
        None,
        Unavailable,
        Critical
    }
    private const int MaxRows = 1000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly Database db;
    private readonly object gate = new();
    private int pruneCounter;

    public UserEventLogService(Database db, LogRepository developerLog)
    {
        this.db = db;
        // User operation records are written only from confirmed domain operations.
        // Developer diagnostics remain a separate surface and are not parsed into this store.
        Prune();
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
                     origin, event_trigger, story_role, target_kind, actionability, reservation_source, program_title,
                     reservation_id, recording_id, recording_recovery_chain_id, power_resume_cycle_id, service_name, scheduled_start, scheduled_end, actual_start, actual_end,
                     drop_count, error_count, scramble_count, file_path, completion_reason, details_json, detail)
                VALUES
                    ($createdAt, $severity, $category, $result, $target, $message, $code, $traceId,
                     $operationId, $appVersion, $previousAppVersion, $versionChanged, $storyContext,
                     $origin, $trigger, $storyRole, $targetKind, $actionability, $reservationSource, $programTitle,
                     $reservationId, $recordingId, $recordingRecoveryChainId, $powerResumeCycleId, $serviceName, $scheduledStart, $scheduledEnd, $actualStart, $actualEnd,
                     $dropCount, $errorCount, $scrambleCount, $filePath, $completionReason, $detailsJson, $detail);
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
            PopulateStructuredFields(entry);
            cmd.Parameters.AddWithValue("$programTitle", entry.ProgramTitle ?? string.Empty);
            cmd.Parameters.AddWithValue("$reservationId", entry.ReservationId ?? string.Empty);
            cmd.Parameters.AddWithValue("$recordingId", entry.RecordingId ?? string.Empty);
            cmd.Parameters.AddWithValue("$recordingRecoveryChainId", entry.RecordingRecoveryChainId ?? string.Empty);
            cmd.Parameters.AddWithValue("$powerResumeCycleId", entry.PowerResumeCycleId ?? string.Empty);
            cmd.Parameters.AddWithValue("$serviceName", entry.ServiceName ?? string.Empty);
            cmd.Parameters.AddWithValue("$scheduledStart", FormatNullableDate(entry.ScheduledStart));
            cmd.Parameters.AddWithValue("$scheduledEnd", FormatNullableDate(entry.ScheduledEnd));
            cmd.Parameters.AddWithValue("$actualStart", FormatNullableDate(entry.ActualStart));
            cmd.Parameters.AddWithValue("$actualEnd", FormatNullableDate(entry.ActualEnd));
            cmd.Parameters.AddWithValue("$dropCount", (object?)entry.DropCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorCount", (object?)entry.ErrorCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$scrambleCount", (object?)entry.ScrambleCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$filePath", entry.FilePath ?? string.Empty);
            cmd.Parameters.AddWithValue("$completionReason", entry.CompletionReason ?? string.Empty);
            cmd.Parameters.AddWithValue("$detailsJson", NormalizeDetailsJson(entry.DetailsJson));
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
        count = Math.Clamp(count, 1, 1000);
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
                SELECT id, created_at, severity, category, result, target, message, code, trace_id, operation_id, app_version, previous_app_version, version_changed, story_context, origin, event_trigger, story_role, target_kind, actionability, reservation_source, program_title, reservation_id, recording_id, recording_recovery_chain_id, power_resume_cycle_id, service_name, scheduled_start, scheduled_end, actual_start, actual_end, drop_count, error_count, scramble_count, file_path, completion_reason, details_json, detail
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
        maxRows = Math.Clamp(maxRows, 1, 500);
        var since = DateTime.Now.Subtract(window);
        lock (gate)
        {
            Prune();
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT id, created_at, severity, category, result, target, message, code, trace_id, operation_id, app_version, previous_app_version, version_changed, story_context, origin, event_trigger, story_role, target_kind, actionability, reservation_source, program_title, reservation_id, recording_id, recording_recovery_chain_id, power_resume_cycle_id, service_name, scheduled_start, scheduled_end, actual_start, actual_end, drop_count, error_count, scramble_count, file_path, completion_reason, details_json, detail
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

        // 
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
            ReservationSource = ReservationOriginClassifier.GetUserSourceLabel(successor),
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
                $"source={ReservationOriginClassifier.GetUserSourceLabel(successor)}",
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
            ReservationSource = ReservationOriginClassifier.GetUserSourceLabel(first),
            ProgramTitle = title,
            // USER_LOG_RESERVATION_ID_PROJECTION_INVARIANT
            // 取消も追加・録画開始・失敗と同じ ReservationId 正本へ格納する。
            // requestedReservationId を Detail だけへ残して報告用コピーから欠落させない。
            ReservationId = requestedReservationId > 0 ? $"R{requestedReservationId}" : string.Empty,
            Detail = BuildCompactDetail(new []
            {
                requestedReservationId > 0 ? $"requestedReservationId=R{requestedReservationId}" : string.Empty,
                $"count={visibleTargets.Count}",
                $"targets=[{string.Join(",", visibleTargets.Select(x => $"R{x.Id}"))}]",
                string.IsNullOrWhiteSpace(service) ? string.Empty : $"service={service}",
                string.IsNullOrWhiteSpace(title) ? string.Empty : $"programTitle={title}",
                visibleTargets.Count > 1 ? $"successorTitles=[{string.Join(" | ", visibleTargets.Skip(1).Select(x => ResolveReservationOperationTitle(x)).Where(x => !string.IsNullOrWhiteSpace(x)))}]" : string.Empty,
                $"source={ReservationOriginClassifier.GetUserSourceLabel(first)}",
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

        // USER_LOG_OPERATION_SEMANTICS_INVARIANT:
        // 予約取消の入口は未来予約だけを扱う。録画中/Starting/Stoppingをここで CANCEL として表現すると
        // 実際のユーザー操作「録画停止」と意味が衝突するため、録画系は専用 lifecycle イベントへ委譲する。
        // 通常APIは録画中の予約取消を拒否するので、ここでは誤投影を生成せず終了する。
        if (reservation.Status is ReservationStatus.Starting or ReservationStatus.Recording or ReservationStatus.Stopping)
            return;

        if (IsUserChainLikeReservation(reservation))
        {
            AddChainReservationCancelled(new[] { reservation }, reservationId, createdAt);
            return;
        }
        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        if (!route.EmitToUserLog) return;
        var code = $"{route.RouteKind.ToUpperInvariant()}_RESERVATION_CANCELLED";
        code = code.Replace("-", "_").Replace(" ", "_");
        var operationId = BuildOperationId($"{route.OperationCodePrefix}_CAN", at);
        var title = ResolveReservationOperationTitle(reservation);
        var service = NormalizeOperationText(reservation.ServiceName);
        var messagePrefix = $"{route.ReservationSourceLabel}予約を取り消しました";
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "予約",
            Result = "CANCEL",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? messagePrefix : $"{messagePrefix}: {title}",
            Code = code,
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = $"{route.StoryContext.Replace("Added", string.Empty)}Cancelled",
            Origin = "Reservation",
            Trigger = route.Trigger,
            StoryRole = "Cancelled",
            TargetKind = route.TargetKind,
            Actionability = "NoAction",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = BuildDirectReservationDetail(reservation, reservationId, route, statusOverride: ReservationStatus.Cancelled)
        });
    }

    public void AddReservationStatusChanged(
        Reservation? before,
        int reservationId,
        ReservationStatus newStatus,
        DateTime? createdAt = null,
        string? failureReason = null)
    {
        if (before is null || !ShouldEmitReservationOperation(before)) return;
        var at = createdAt ?? DateTime.Now;
        var title = ResolveReservationOperationTitle(before);
        var service = NormalizeOperationText(before.ServiceName);
        var target = BuildOperationTarget(service, title);
        var source = ReservationOriginClassifier.GetUserSourceLabel(before);

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
            // RECORDING_FAILURE_REASON_PROJECTION_SINGLE_SOURCE:
            // Failure semantics come from the lifecycle mutation metadata that committed Failed.
            // IsConflicted is allocation state and must not be reinterpreted here as a tuner-acquire failure.
            // Conflict-at-due has its own REC_SKIPPED_BY_CONFLICT event and suppresses this generic route.
            var canonicalFailureReason = NormalizeOperationText(failureReason);
            entry = new UserEventLogEntry
            {
                CreatedAt = at,
                Severity = "ERROR",
                Category = "録画",
                Result = "FAILED",
                Target = target,
                Message = string.IsNullOrWhiteSpace(title) ? "録画に失敗しました" : $"録画に失敗しました: {title}",
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
                Detail = BuildDirectReservationDetail(before, reservationId, statusOverride: ReservationStatus.Failed)
                    + $"; failureReason={(string.IsNullOrWhiteSpace(canonicalFailureReason) ? "unknown" : canonicalFailureReason)}"
            };
        }
        else if (newStatus == ReservationStatus.Cancelled && before.Status != ReservationStatus.Cancelled)
        {
            // RECORDING_STOP_USER_LOG_SEMANTICS_INVARIANT:
            // Recording の手動停止は lifecycle 上 Stopping -> Cancelled で終端するが、
            // これは「予約取消」ではない。ユーザーが実行した操作と監査上の意味を維持し、
            // 予約取消系ログへ落とさず録画停止として独立投影する。
            if (before.Status == ReservationStatus.Stopping)
            {
                AddRecordingStopped(before, reservationId, at);
                return;
            }

            AddReservationDeleted(before, reservationId, at);
            return;
        }

        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Message))
            Add(entry);
    }


    public void AddRecordingStopped(Reservation reservation, int reservationId, DateTime? createdAt = null)
    {
        if (!ShouldEmitReservationOperation(reservation)) return;
        var at = createdAt ?? DateTime.Now;
        var route = ClassifyReservationAddRoute(reservation);
        if (!route.EmitToUserLog) return;
        var title = ResolveReservationOperationTitle(reservation);
        var service = NormalizeOperationText(reservation.ServiceName);
        var operationId = BuildOperationId("REC_STOPPED", at);

        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "録画",
            Result = "OK",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? "録画を停止しました" : $"録画を停止しました: {title}",
            Code = "REC_STOPPED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "RecordingStoppedByUser",
            Origin = "Recording",
            Trigger = "UserManualStop",
            StoryRole = "Stopped",
            TargetKind = "録画",
            Actionability = "NoAction",
            ReservationSource = route.ReservationSourceLabel,
            ProgramTitle = title,
            Detail = BuildDirectReservationDetail(reservation, reservationId, route, statusOverride: ReservationStatus.Cancelled)
                + "; completionReason=UserStopped; semanticOperation=recording_stop"
        });
    }

    public void AddRecordingInterrupted(Reservation? reservation, int reservationId, string? reason = null, string? fileEvidence = null, DateTime? createdAt = null, string trigger = "InterruptedRecordingRecovery")
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
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "InterruptedRecordingRecovery" : trigger,
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

    public void AddScheduledEpgPartial(
        string targetScope,
        string requestedBy,
        bool silent,
        int completedGroups,
        int totalGroups,
        int importedEvents,
        int missingGroups,
        string missingScopes,
        DateTime? retryAt,
        DateTime? retryDeadline,
        DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("EPG_RUN_PARTIAL", at);
        var target = NormalizeEpgTargetLabel(targetScope);
        var retryText = retryAt.HasValue
            ? $"。未完了分は{retryAt.Value:HH:mm:ss}以降に再試行します"
            : "。未完了分を再試行します";
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "WARN",
            Category = "EPG",
            Result = "PARTIAL",
            Target = target,
            Message = "EPG取得の一部を完了しました" + retryText,
            Code = "EPG_RUN_PARTIAL",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = ResolveEpgRunStoryContext(requestedBy, silent, "Partial"),
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = "Partial",
            TargetKind = "EPG",
            Actionability = "NoAction",
            Detail = BuildCompactDetail(new[]
            {
                $"targetScope={target}",
                $"silent={silent}",
                $"completedGroups={completedGroups}/{totalGroups}",
                $"imported={importedEvents}",
                $"missingGroups={missingGroups}",
                string.IsNullOrWhiteSpace(missingScopes) ? string.Empty : $"missingScopes={missingScopes}",
                retryAt.HasValue ? $"retryAt={retryAt.Value:yyyy/MM/dd HH:mm:ss}" : string.Empty,
                retryDeadline.HasValue ? $"retryDeadline={retryDeadline.Value:yyyy/MM/dd HH:mm:ss}" : string.Empty
            })
        });
    }

    public void AddScheduledEpgContinuationExpired(
        string targetScope,
        string requestedBy,
        bool silent,
        string completedScopes,
        DateTime retryDeadline,
        DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("EPG_RUN_FAILED", at);
        var target = NormalizeEpgTargetLabel(targetScope);
        var outcome = UserLogOutcomes.Incomplete;
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = outcome.Severity,
            Category = "EPG",
            Result = outcome.Result,
            Target = target,
            Message = "EPG取得を完了できませんでした",
            Code = "EPG_RUN_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = ResolveEpgRunStoryContext(requestedBy, silent, "Failed"),
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = outcome.StoryRole,
            TargetKind = "EPG",
            Actionability = outcome.Actionability,
            Detail = BuildCompactDetail(new[]
            {
                $"targetScope={target}",
                $"silent={silent}",
                "reason=daily_continuation_deadline_expired",
                string.IsNullOrWhiteSpace(completedScopes) ? string.Empty : $"completedScopes={completedScopes}",
                $"retryDeadline={retryDeadline:yyyy/MM/dd HH:mm:ss}"
            })
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
        var failureImpact = isCleanOk || isBlocked ? EpgFailureImpact.None : ClassifyEpgFailureImpact(resultDetail);
        var failureOutcome = failureImpact == EpgFailureImpact.Critical
            ? UserLogOutcomes.CriticalFailure
            : UserLogOutcomes.Unavailable;
        var message = isCleanOk
            ? "EPG取得を完了しました"
            : isBlocked
                ? BuildEpgBlockedUserMessage(resultDetail, targetScope)
                : BuildGroundedEpgFailureUserMessage(resultDetail, failureImpact);
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
            Severity = isCleanOk ? UserLogOutcomes.Success.Severity : isBlocked ? UserLogOutcomes.Blocked.Severity : failureOutcome.Severity,
            Category = "EPG",
            Result = isCleanOk ? UserLogOutcomes.Success.Result : isBlocked ? UserLogOutcomes.Blocked.Result : failureOutcome.Result,
            Target = target,
            Message = message,
            Code = code,
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = storyContext,
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = isCleanOk ? UserLogOutcomes.Success.StoryRole : isBlocked ? UserLogOutcomes.Blocked.StoryRole : failureOutcome.StoryRole,
            TargetKind = "EPG",
            Actionability = isCleanOk ? UserLogOutcomes.Success.Actionability : isBlocked ? UserLogOutcomes.Blocked.Actionability : failureOutcome.Actionability,
            Detail = string.Join("; ", detailParts)
        });
    }


    private static EpgFailureImpact ClassifyEpgFailureImpact(string? resultDetail)
    {
        if (string.IsNullOrWhiteSpace(resultDetail)) return EpgFailureImpact.None;
        var d = resultDetail.ToUpperInvariant();

        // 永続データを保存・更新できない場合は、ユーザーの目的が成立していないため赤表示を許可する。
        if (d.Contains("UNAUTHORIZEDACCESSEXCEPTION")
            || d.Contains("IOEXCEPTION")
            || d.Contains("DIRECTORYNOTFOUNDEXCEPTION")
            || d.Contains("PATHTOOLONGEXCEPTION")
            || d.Contains("ACCESS DENIED")
            || d.Contains("DISK FULL")
            || d.Contains("NOT ENOUGH SPACE")
            || (d.Contains("WRITE") && (d.Contains("DENIED") || d.Contains("FAILED") || d.Contains("ERROR")))
            || d.Contains("SQLITE")
            || d.Contains("DATABASE")
            || d.Contains("DB_")
            || (d.Contains("IMPORT") && (d.Contains("FAILED") || d.Contains("ERROR")))
            || (d.Contains("STORE") && (d.Contains("FAILED") || d.Contains("ERROR"))))
            return EpgFailureImpact.Critical;

        // Workerを起動できない等はEPG機能単独の利用不能であり、TvAIr全体の継続不能ではない。
        if ((d.Contains("TVAIREPGREC") || d.Contains("WORKER") || d.Contains("PROCESS"))
            && (d.Contains("START") || d.Contains("LAUNCH") || d.Contains("NOT FOUND") || d.Contains("MISSING")))
            return EpgFailureImpact.Unavailable;

        return EpgFailureImpact.None;
    }

    private static string BuildGroundedEpgFailureUserMessage(string? resultDetail, EpgFailureImpact impact)
    {
        if (impact == EpgFailureImpact.None) return string.Empty;
        var d = (resultDetail ?? string.Empty).ToUpperInvariant();

        if (impact == EpgFailureImpact.Critical)
        {
            if (d.Contains("SQLITE") || d.Contains("DATABASE") || d.Contains("DB_")
                || (d.Contains("IMPORT") && (d.Contains("FAILED") || d.Contains("ERROR")))
                || (d.Contains("STORE") && (d.Contains("FAILED") || d.Contains("ERROR"))))
                return "番組表を更新できませんでした";
            return "保存先に書き込めませんでした";
        }

        return "EPG取得用の実行ファイルを起動できませんでした";
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
        var failureImpact = ClassifyEpgFailureImpact(resultDetail);
        var message = BuildGroundedEpgFailureUserMessage(resultDetail, failureImpact);
        if (string.IsNullOrWhiteSpace(message))
            return;
        var outcome = failureImpact == EpgFailureImpact.Critical
            ? UserLogOutcomes.CriticalFailure
            : UserLogOutcomes.Unavailable;

        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("EPG_RUN_FAILED", at);
        var target = NormalizeEpgTargetLabel(targetScope);
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = outcome.Severity,
            Category = "EPG",
            Result = outcome.Result,
            Target = target,
            Message = message,
            Code = "EPG_RUN_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = ResolveEpgRunStoryContext(requestedBy, silent, "Failed"),
            Origin = "Epg",
            Trigger = NormalizeEpgTriggerLabel(requestedBy),
            StoryRole = outcome.StoryRole,
            TargetKind = "EPG",
            Actionability = outcome.Actionability,
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

        // USER_LOG_PRESENTATION_INVARIANT
        // 標準ログは、画面をうるさくしないため必ずシンプルな1行表示にする。
        // 詳細ログは、ユーザーがオプションで有効化し、チェックした項目だけを多段表示する。
        // 詳細向け情報を標準ログへ詰め込まず、標準と詳細の既存契約を混同・統合・置換しない。
        // 録画前EPG確認に失敗しても、予約時刻を正本として録画を継続できる場合は障害ではない。
        // ユーザー運用ログの赤表示は録画不能・途中終了・データ破損などのクリティカル事象に限定し、
        // この経路は警告色のフォールバック結果として提示する。内部診断コードは追跡互換のため維持する。
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "WARN",
            Category = "EPG確認",
            Result = "FALLBACK",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? "EPG確認ができなかったため、予約時刻のまま録画します" : $"EPG確認ができなかったため、予約時刻のまま録画します: {title}",
            Code = "PRE_REC_EPG_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "PreRecordEpgFallbackToReservedTime",
            Origin = "PreRecordEpg",
            Trigger = "PreRecordCheck",
            StoryRole = UserLogOutcomes.Fallback.StoryRole,
            TargetKind = "EPG確認",
            Actionability = UserLogOutcomes.Fallback.Actionability,
            ReservationSource = ReservationOriginClassifier.GetUserSourceLabel(parent),
            ProgramTitle = title,
            ReservationId = $"R{parent.Id}",
            ServiceName = service,
            ScheduledStart = parent.StartTime,
            ScheduledEnd = parent.EndTime,
            CompletionReason = "EPG確認ができなかったため、予約時刻のまま録画します",
            Detail = BuildCompactDetail(new [] { $"reservationId=R{parent.Id}", $"service={service}", $"programTitle={title}", string.IsNullOrWhiteSpace(reason) ? string.Empty : $"diagnosticReason={NormalizeOperationText(reason)}" })
        });
    }

    public void AddPreRecordEpgCheckedNoChange(Reservation reservation, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("PRE_REC_EPG_OK", at);
        var title = NormalizeOperationText(reservation.Title);
        var service = NormalizeOperationText(reservation.ServiceName);

        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = UserLogOutcomes.Success.Severity,
            Category = "EPG確認",
            Result = UserLogOutcomes.Success.Result,
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title)
                ? "EPG確認を完了しました（放送時間の変更なし）"
                : $"EPG確認を完了しました（放送時間の変更なし）: {title}",
            Code = "PRE_REC_EPG_OK",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "PreRecordEpgCheckedNoChange",
            Origin = "PreRecordEpg",
            Trigger = "PreRecordCheck",
            StoryRole = UserLogOutcomes.Success.StoryRole,
            TargetKind = "EPG確認",
            Actionability = UserLogOutcomes.Success.Actionability,
            ReservationSource = ReservationOriginClassifier.GetUserSourceLabel(reservation),
            ProgramTitle = title,
            ReservationId = $"R{reservation.Id}",
            ServiceName = service,
            ScheduledStart = reservation.StartTime,
            ScheduledEnd = reservation.EndTime,
            CompletionReason = "放送時間の変更なし",
            Detail = BuildCompactDetail(new []
            {
                $"reservationId=R{reservation.Id}",
                $"service={service}",
                $"programTitle={title}",
                $"start={reservation.StartTime:yyyy/MM/dd HH:mm:ss}",
                $"end={reservation.EndTime:yyyy/MM/dd HH:mm:ss}"
            })
        });
    }

    public void AddTimeFollowUpdated(Reservation reservation, DateTime oldStart, DateTime oldEnd, DateTime newStart, DateTime newEnd, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("TIME_FOLLOW_UPDATED", at);
        var title = NormalizeOperationText(reservation.Title);
        var service = NormalizeOperationText(reservation.ServiceName);
        var before = $"{oldStart:yyyy/MM/dd HH:mm:ss}〜{oldEnd:yyyy/MM/dd HH:mm:ss}";
        var after = $"{newStart:yyyy/MM/dd HH:mm:ss}〜{newEnd:yyyy/MM/dd HH:mm:ss}";

        // USER_LOG_PRESENTATION_INVARIANT
        // 標準ログは1行の結果だけとし、変更前後は「状態変化」が選択された詳細ログへ投影する。
        // 更新後の予定時刻は「予定時刻」、変更前→変更後は「状態変化」の正本へそれぞれ流す。
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = "INFO",
            Category = "EPG確認",
            Result = "OK",
            Target = BuildOperationTarget(service, title),
            Message = string.IsNullOrWhiteSpace(title) ? "放送時間を更新しました" : $"放送時間を更新しました: {title}",
            Code = "TIME_FOLLOW_UPDATED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "ReservationTimeAdjusted",
            Origin = "PreRecordEpg",
            Trigger = "PreRecordCheck",
            StoryRole = "Adjusted",
            TargetKind = "予約",
            Actionability = "NoAction",
            ReservationSource = ReservationOriginClassifier.GetUserSourceLabel(reservation),
            ProgramTitle = title,
            ReservationId = $"R{reservation.Id}",
            ServiceName = service,
            ScheduledStart = newStart,
            ScheduledEnd = newEnd,
            DetailsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [TvAirLogDetailKeys.StateBefore] = before,
                [TvAirLogDetailKeys.StateAfter] = after
            }),
            Detail = BuildCompactDetail(new []
            {
                $"reservationId=R{reservation.Id}",
                $"service={service}",
                $"programTitle={title}",
                $"start={newStart:yyyy/MM/dd HH:mm:ss}",
                $"end={newEnd:yyyy/MM/dd HH:mm:ss}",
                $"{TvAirLogDetailKeys.StateBefore}={before}",
                $"{TvAirLogDetailKeys.StateAfter}={after}"
            })
        });
    }

    public void AddWakeRegistrationFailed(Reservation? reservation, int failedCount, string? detail = null, DateTime? createdAt = null)
    {
        // 
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
            ReservationSource = ReservationOriginClassifier.GetUserSourceLabel(successor),
            ProgramTitle = succTitle,
            Detail = BuildCompactDetail(new [] { predecessor is null ? string.Empty : $"predecessor=R{predecessor.Id}", $"successor=R{successor.Id}", string.IsNullOrWhiteSpace(predTitle) ? string.Empty : $"predecessorTitle={predTitle}", string.IsNullOrWhiteSpace(succTitle) ? string.Empty : $"successorTitle={succTitle}" })
        });
    }

    public void AddPluginLoadFailed(string pluginName, string? reason = null, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.Now;
        var operationId = BuildOperationId("PLUGIN_LOAD_FAILED", at);
        var outcome = UserLogOutcomes.Unavailable;
        Add(new UserEventLogEntry
        {
            CreatedAt = at,
            Severity = outcome.Severity,
            Category = "プラグイン",
            Result = outcome.Result,
            Target = NormalizeOperationText(pluginName),
            Message = "プラグインを読み込めませんでした",
            Code = "PLUGIN_LOAD_FAILED",
            TraceId = operationId,
            OperationId = operationId,
            StoryContext = "PluginLoadFailed",
            Origin = "Plugin",
            Trigger = "PluginHost",
            StoryRole = outcome.StoryRole,
            TargetKind = "プラグイン",
            Actionability = outcome.Actionability,
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
        // 予約元の意味は ReservationOriginClassifier を唯一の正本とし、
        // ユーザーログはその分類結果から操作別の文言だけを組み立てる。
        var origin = ReservationOriginClassifier.Classify(reservation).Origin;
        var sourceLabel = ReservationOriginClassifier.GetUserSourceLabel(reservation);

        return origin switch
        {
            ReservationOriginKind.SystemEpg => new ReservationAddRoute(
                false,
                "内部予約を追加しました",
                "InternalReservationAdded",
                "InternalSystemReservation",
                "内部予約",
                "INTERNAL_RESERVATION_ADDED",
                "INT_RES_ADD",
                sourceLabel,
                "internal_system_epg_or_prerec",
                "short_internal"),

            ReservationOriginKind.AutoSearch => new ReservationAddRoute(
                true,
                "自動検索で予約しました",
                "AutoSearchReservationAdded",
                "AutoSearchReservation",
                "予約",
                "AUTO_SEARCH_RESERVATION_ADDED",
                "AUTO_RES_ADD",
                sourceLabel,
                "auto_search",
                "active_reservation"),

            ReservationOriginKind.ExplicitProgramRule
                or ReservationOriginKind.ProgramGuideMissingProgramRule => new ReservationAddRoute(
                true,
                "プログラムから予約しました",
                "ProgramReservationAdded",
                "ProgramReservation",
                "予約",
                "PROGRAM_RESERVATION_ADDED",
                "PROG_RES_ADD",
                sourceLabel,
                "program",
                "active_reservation"),

            ReservationOriginKind.KeywordSearchProgramGuide => new ReservationAddRoute(
                true,
                "番組表から予約しました",
                "ProgramGuideReservationAdded",
                "ProgramGuideReservation",
                "予約",
                "PROGRAM_GUIDE_RESERVATION_ADDED",
                "PG_RES_ADD",
                sourceLabel,
                "keyword_search_user_selected",
                "active_reservation"),

            ReservationOriginKind.ManualProgramGuide
                or ReservationOriginKind.ImmediateProgramGuide => new ReservationAddRoute(
                true,
                "番組表から予約しました",
                "ProgramGuideReservationAdded",
                "ProgramGuideReservation",
                "予約",
                "PROGRAM_GUIDE_RESERVATION_ADDED",
                "PG_RES_ADD",
                sourceLabel,
                "program_guide",
                "active_reservation"),

            _ => new ReservationAddRoute(
                true,
                "予約しました",
                "ReservationAdded",
                "Reservation",
                "予約",
                "RESERVATION_ADDED",
                "RES_ADD",
                sourceLabel,
                ReservationOriginClassifier.GetOperationalRoute(reservation),
                "active_reservation")
        };
    }

    private static bool IsUserChainLikeReservation(Reservation reservation)
        => reservation.IsUserChain || reservation.UserChainPreviousId.HasValue || reservation.UserChainRootId.HasValue;

    internal static bool ShouldEmitReservationOperation(Reservation reservation)
    {
        // USER_LOG_INTERNAL_RESERVATION_BOUNDARY_INVARIANT:
        // System EPG / PreRec intent は内部運用予約であり、状態が Scheduled→Recording→Completed と遷移しても
        // 汎用の「予約」「録画」ユーザーログへ投影しない。EPG/EPG確認の専用イベントだけを正本とする。
        // 内部System予約の意味は Source / ReservationIntent の構造化正本だけで遮断する。
        // Title / SourceRuleName による用途推測をユーザーログへ持ち込まない。
        if (reservation.Source == ReservationSource.Epg || ReservationIntentContract.IsSystem(reservation.Intent)) return false;
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

        //  自動検索予約の個別有効/無効ログでは、古い予約や投影由来の予約で
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






    public int EnrichRecordingResult(
        TvAIrPlugin.TvAirRecordingResultDto result,
        IReadOnlyDictionary<string, string>? terminationEvidence = null)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.ReservationId)) return 0;
        lock (gate)
        {
            using var con = db.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                UPDATE user_event_logs
                SET recording_id = $recordingId,
                    service_name = CASE WHEN service_name = '' THEN $serviceName ELSE service_name END,
                    program_title = CASE WHEN program_title = '' THEN $programTitle ELSE program_title END,
                    scheduled_start = $scheduledStart,
                    actual_start = $actualStart,
                    actual_end = $actualEnd,
                    drop_count = $dropCount,
                    error_count = $errorCount,
                    scramble_count = $scrambleCount,
                    file_path = $filePath,
                    completion_reason = $completionReason,
                    recording_recovery_chain_id = $recordingRecoveryChainId,
                    power_resume_cycle_id = $powerResumeCycleId,
                    details_json = $detailsJson
                WHERE id = (
                    SELECT id FROM user_event_logs
                    WHERE reservation_id = $reservationId
                      AND category = '録画'
                      AND code IN ('REC_END_OK', 'REC_STOPPED', 'REC_FAILED', 'REC_INTERRUPTED')
                    ORDER BY created_at DESC, id DESC
                    LIMIT 1
                );
                """;
            cmd.Parameters.AddWithValue("$reservationId", result.ReservationId);
            cmd.Parameters.AddWithValue("$recordingId", result.RecordingId ?? string.Empty);
            cmd.Parameters.AddWithValue("$serviceName", result.ServiceName ?? string.Empty);
            cmd.Parameters.AddWithValue("$programTitle", result.EventTitle ?? string.Empty);
            cmd.Parameters.AddWithValue("$scheduledStart", result.ScheduledStartTime.ToString("O"));
            cmd.Parameters.AddWithValue("$actualStart", result.ActualStartTime?.ToString("O") ?? string.Empty);
            cmd.Parameters.AddWithValue("$actualEnd", result.ActualEndTime?.ToString("O") ?? string.Empty);
            cmd.Parameters.AddWithValue("$dropCount", (object?)result.Drop ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errorCount", (object?)result.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$scrambleCount", (object?)result.Scramble ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$filePath", result.FilePath ?? string.Empty);
            cmd.Parameters.AddWithValue("$completionReason", result.EndReason ?? string.Empty);
            var recordingRecoveryChainId = terminationEvidence is not null && terminationEvidence.TryGetValue("recordingRecoveryChainId", out var recoveryChainId) ? recoveryChainId : string.Empty;
            var powerResumeCycleId = terminationEvidence is not null && terminationEvidence.TryGetValue("powerResumeCycleId", out var resumeCycleId) ? resumeCycleId : string.Empty;
            cmd.Parameters.AddWithValue("$recordingRecoveryChainId", recordingRecoveryChainId ?? string.Empty);
            cmd.Parameters.AddWithValue("$powerResumeCycleId", powerResumeCycleId ?? string.Empty);

            var mergedDetails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var read = con.CreateCommand())
            {
                read.CommandText = """
                    SELECT details_json
                    FROM user_event_logs
                    WHERE reservation_id = $reservationId
                      AND category = '録画'
                      AND code IN ('REC_END_OK', 'REC_STOPPED', 'REC_FAILED', 'REC_INTERRUPTED')
                    ORDER BY created_at DESC, id DESC
                    LIMIT 1;
                    """;
                read.Parameters.AddWithValue("$reservationId", result.ReservationId);
                var existingJson = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(existingJson))
                {
                    try
                    {
                        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson);
                        if (existing is not null)
                        {
                            foreach (var pair in existing)
                            {
                                var value = pair.Value.ValueKind == JsonValueKind.String
                                    ? pair.Value.GetString() ?? string.Empty
                                    : pair.Value.ToString();
                                if (!string.IsNullOrWhiteSpace(value))
                                    mergedDetails[pair.Key] = value;
                            }
                        }
                    }
                    catch
                    {
                        // Existing user-operation evidence must not block result finalization.
                    }
                }
            }

            mergedDetails["result"] = result.Result ?? string.Empty;
            mergedDetails["qualityDataAvailable"] = result.QualityDataAvailable.ToString();
            mergedDetails["qualityCompleteness"] = result.QualityCompleteness ?? string.Empty;
            mergedDetails["qualitySource"] = result.QualitySource ?? string.Empty;
            mergedDetails["resourceReleaseState"] = result.ResourceReleaseState ?? string.Empty;
            mergedDetails["resultFinalized"] = result.ResultFinalized.ToString();
            mergedDetails["fileCreated"] = result.FileCreated?.ToString() ?? string.Empty;
            if (terminationEvidence is not null)
            {
                foreach (var pair in terminationEvidence)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                        mergedDetails[pair.Key] = pair.Value;
                }
            }
            cmd.Parameters.AddWithValue("$detailsJson", JsonSerializer.Serialize(mergedDetails));
            return cmd.ExecuteNonQuery();
        }
    }

    private static void PopulateStructuredFields(UserEventLogEntry entry)
    {
        var detail = ParseDetail(entry.Detail);
        if (string.IsNullOrWhiteSpace(entry.ReservationId) && detail.TryGetValue("reservationId", out var reservationId)) entry.ReservationId = NormalizeReservationId(reservationId);
        if (string.IsNullOrWhiteSpace(entry.ReservationId) && detail.TryGetValue("id", out var id)) entry.ReservationId = NormalizeReservationId(id);
        if (string.IsNullOrWhiteSpace(entry.RecordingId) && entry.Category == "録画" && !string.IsNullOrWhiteSpace(entry.ReservationId)) entry.RecordingId = entry.ReservationId;
        if (string.IsNullOrWhiteSpace(entry.RecordingRecoveryChainId) && detail.TryGetValue("recordingRecoveryChainId", out var recoveryChainId)) entry.RecordingRecoveryChainId = recoveryChainId.Trim();
        if (string.IsNullOrWhiteSpace(entry.PowerResumeCycleId) && detail.TryGetValue("powerResumeCycleId", out var resumeCycleId)) entry.PowerResumeCycleId = resumeCycleId.Trim();
        if (string.IsNullOrWhiteSpace(entry.ServiceName)) entry.ServiceName = ExtractServiceFromTarget(entry.Target, entry.ProgramTitle);
        entry.ScheduledStart ??= ParseDetailDate(detail, "start") ?? ParseDetailDate(detail, "scheduledStart");
        entry.ScheduledEnd ??= ParseDetailDate(detail, "end") ?? ParseDetailDate(detail, "scheduledEnd");
        if (string.IsNullOrWhiteSpace(entry.DetailsJson) || entry.DetailsJson == "{}")
            entry.DetailsJson = JsonSerializer.Serialize(detail);
    }

    private static Dictionary<string, string> ParseDetail(string? detail)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (detail ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            result[part[..index].Trim()] = part[(index + 1)..].Trim();
        }
        return result;
    }

    private static string NormalizeReservationId(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) return string.Empty;
        return normalized.StartsWith("R", StringComparison.OrdinalIgnoreCase) ? normalized.ToUpperInvariant() : $"R{normalized}";
    }

    private static string ExtractServiceFromTarget(string? target, string? title)
    {
        var value = (target ?? string.Empty).Trim();
        var programTitle = (title ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (programTitle.Length > 0 && value.EndsWith(programTitle, StringComparison.Ordinal))
            value = value[..^programTitle.Length].Trim().TrimEnd('/', '・', ' ');
        return value;
    }

    private static DateTime? ParseDetailDate(IReadOnlyDictionary<string, string> detail, string key)
        => detail.TryGetValue(key, out var value) && DateTime.TryParse(value, out var parsed) ? parsed : null;

    private static string FormatNullableDate(DateTime? value) => value?.ToString("O") ?? string.Empty;
    private static string NormalizeDetailsJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "{}";
        try { JsonDocument.Parse(value); return value; } catch { return "{}"; }
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
                ReservationId = SafeReaderString(reader, 21),
                RecordingId = SafeReaderString(reader, 22),
                RecordingRecoveryChainId = SafeReaderString(reader, 23),
                PowerResumeCycleId = SafeReaderString(reader, 24),
                ServiceName = SafeReaderString(reader, 25),
                ScheduledStart = SafeReaderDate(reader, 26),
                ScheduledEnd = SafeReaderDate(reader, 27),
                ActualStart = SafeReaderDate(reader, 28),
                ActualEnd = SafeReaderDate(reader, 29),
                DropCount = SafeReaderInt64(reader, 30),
                ErrorCount = SafeReaderInt64(reader, 31),
                ScrambleCount = SafeReaderInt64(reader, 32),
                FilePath = SafeReaderString(reader, 33),
                CompletionReason = SafeReaderString(reader, 34),
                DetailsJson = SafeReaderString(reader, 35),
                Detail = SafeReaderString(reader, 36)
            };
        }
    }

    private static DateTime? SafeReaderDate(SqliteDataReader reader, int ordinal)
    {
        var value = SafeReaderString(reader, ordinal);
        return DateTime.TryParse(value, out var parsed) ? parsed : null;
    }

    private static long? SafeReaderInt64(SqliteDataReader reader, int ordinal)
    {
        try { return ordinal < reader.FieldCount && !reader.IsDBNull(ordinal) ? Convert.ToInt64(reader.GetValue(ordinal)) : null; }
        catch { return null; }
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
                   OR (category = '録画' AND code IN ('REC_START_OK', 'REC_END_OK') AND message LIKE '%EPG確認%');
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
        var all = GetReportEntries(window, maxRows).OrderBy(e => e.CreatedAt).ToArray();
        var selected = SelectReportEntries(all);
        var body = new StringBuilder();
        body.AppendLine($"TvAIr {SanitizeReportValue(tvairVersion)} トラブル報告用コピー");
        body.AppendLine($"出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
        body.AppendLine($"対象範囲: 直近{FormatWindow(window)} / 抽出={selected.Length}件 / 保持={all.Length}件");
        body.AppendLine("※個人情報・保存先の固有名は出力しません。");
        body.AppendLine("----");

        // USER_LOG_REPORT_COPY_INVARIANT
        // 報告用コピーもユーザーが扱う公開面である。
        // 標準1行の内容を正本とし、解析に必要なユーザー可読の事実だけを重複なく追加する。
        // origin / trigger / route / contract / operationId 等の内部契約名や、同一値の別形式再掲を出さない。
        foreach (var e in selected)
        {
            body.AppendLine($"[{e.CreatedAt:yyyy/MM/dd HH:mm:ss}] {SanitizeReportValue(e.Severity)} {SanitizeReportValue(e.Category)}/{SanitizeReportValue(e.Result)}");
            AppendReportLine(body, "内容", SanitizeReportValue(UserLogProjectionService.BuildStandardMessage(e)));
            AppendReportLine(body, "放送局", SanitizeReportValue(e.ServiceName));
            AppendReportLine(body, "予約元", SanitizeReportValue(e.ReservationSource));
            AppendReportLine(body, "予約ID", SanitizeReportValue(e.ReservationId));
            AppendReportLine(body, "録画ID", string.Equals(e.RecordingId, e.ReservationId, StringComparison.OrdinalIgnoreCase) ? null : SanitizeReportValue(e.RecordingId));
            AppendReportLine(body, "予定時刻", FormatReportRange(e.ScheduledStart, e.ScheduledEnd));
            AppendReportLine(body, "実録画時刻", FormatReportRange(e.ActualStart, e.ActualEnd));
            AppendReportLine(body, "録画品質", FormatReportQuality(e));
            AppendReportLine(body, "理由", SanitizeReportValue(e.CompletionReason));
            AppendReportLine(body, "変更前", ReadReportDetail(e, TvAirLogDetailKeys.StateBefore));
            AppendReportLine(body, "変更後", ReadReportDetail(e, TvAirLogDetailKeys.StateAfter));
        }

        return SplitReportForPosting(body.ToString());
    }

    private static UserEventLogEntry[] SelectReportEntries(UserEventLogEntry[] all)
    {
        if (all.Length == 0) return Array.Empty<UserEventLogEntry>();

        // REPORT_COPY_CURRENT_CONTEXT_INVARIANT:
        // 報告用コピーは、過去のWARN/ERRORだけをアンカーにして最新の正常事象を落としてはならない。
        // 最新の運用事実を必ず含めたうえで、直近のWARN/ERROR周辺を補助的に加える。
        // これにより、出力直前の録画開始・録画終了・EPG確認結果が画面ログと不一致になるのを防ぐ。
        const int maxSelected = 18;
        const int latestCount = 12;
        var selected = all.TakeLast(Math.Min(latestCount, all.Length)).ToList();

        var incident = all.LastOrDefault(e =>
            string.Equals(e.Severity, "ERROR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Severity, "WARN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Result, "NG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.Result, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            e.Result.Contains("失敗", StringComparison.OrdinalIgnoreCase));

        if (incident is not null)
        {
            static string Op(UserEventLogEntry e) => string.IsNullOrWhiteSpace(e.OperationId) ? e.TraceId : e.OperationId;
            var related = all.Where(e =>
                (!string.IsNullOrWhiteSpace(incident.RecordingRecoveryChainId) && string.Equals(e.RecordingRecoveryChainId, incident.RecordingRecoveryChainId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(incident.PowerResumeCycleId) && string.Equals(e.PowerResumeCycleId, incident.PowerResumeCycleId, StringComparison.OrdinalIgnoreCase) &&
                    ((!string.IsNullOrWhiteSpace(incident.ReservationId) && string.Equals(e.ReservationId, incident.ReservationId, StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrWhiteSpace(incident.RecordingId) && string.Equals(e.RecordingId, incident.RecordingId, StringComparison.OrdinalIgnoreCase)) ||
                     string.Equals(e.Origin, incident.Origin, StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrWhiteSpace(Op(incident)) && string.Equals(Op(e), Op(incident), StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(incident.ReservationId) && string.Equals(e.ReservationId, incident.ReservationId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(incident.RecordingId) && string.Equals(e.RecordingId, incident.RecordingId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (related.Count < 5)
            {
                var index = Array.IndexOf(all, incident);
                var from = Math.Max(0, index - 2);
                var to = Math.Min(all.Length - 1, index + 2);
                for (var i = from; i <= to; i++)
                    if (!related.Contains(all[i])) related.Add(all[i]);
            }

            foreach (var entry in related.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id))
            {
                if (selected.Any(x => x.Id == entry.Id)) continue;
                selected.Add(entry);
                if (selected.Count >= maxSelected) break;
            }
        }

        return selected
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .TakeLast(maxSelected)
            .ToArray();
    }




    private static string? FormatReportRange(DateTime? start, DateTime? end)
    {
        if (!start.HasValue && !end.HasValue) return null;
        if (start.HasValue && end.HasValue)
            return $"{start.Value:yyyy/MM/dd HH:mm:ss}〜{end.Value:yyyy/MM/dd HH:mm:ss}";
        return start.HasValue
            ? $"{start.Value:yyyy/MM/dd HH:mm:ss}〜"
            : $"〜{end!.Value:yyyy/MM/dd HH:mm:ss}";
    }

    private static string? FormatReportQuality(UserEventLogEntry entry)
    {
        var values = new List<string>();
        if (entry.DropCount.HasValue) values.Add($"drop={entry.DropCount.Value}");
        if (entry.ErrorCount.HasValue) values.Add($"error={entry.ErrorCount.Value}");
        if (entry.ScrambleCount.HasValue) values.Add($"scramble={entry.ScrambleCount.Value}");
        return values.Count == 0 ? null : string.Join(" ", values);
    }

    private static string? ReadReportDetail(UserEventLogEntry entry, string key)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailsJson ?? "{}");
            return values is not null && values.TryGetValue(key, out var value)
                ? SanitizeReportValue(value)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SplitReportForPosting(string text)
    {
        const int blockLimit = 3600;
        const int maxBlocks = 3;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Length > 500 ? raw[..500] + "…" : raw;
            if (current.Length > 0 && current.Length + line.Length + 1 > blockLimit)
            {
                blocks.Add(current.ToString().TrimEnd());
                current.Clear();
                if (blocks.Count == maxBlocks) break;
            }
            current.AppendLine(line);
        }
        if (current.Length > 0 && blocks.Count < maxBlocks)
            blocks.Add(current.ToString().TrimEnd());
        if (blocks.Count == 0) blocks.Add("報告対象のユーザー運用ログはありません。");

        var count = blocks.Count;
        return string.Join("\n\n", blocks.Select((b, i) => $"[{i + 1}/{count}]\n{b}"));
    }

    private static string SanitizeReportValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var v = value.Trim();
        v = Regex.Replace(v, @"(?i)\\\\[^\\\s]+\\[^\r\n;]+", "<network-path>");
        v = Regex.Replace(v, @"(?i)\b[A-Z]:\\[^\r\n;]+", "<local-path>");
        v = Regex.Replace(v, @"(?i)(?:/home|/Users)/[^/\s]+/[^\r\n;]+", "<local-path>");
        v = Regex.Replace(v, @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "<email>");
        v = Regex.Replace(v, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<ip>");
        v = Regex.Replace(v, @"(?i)(user(name)?|account|pcname|computername)=([^;\s]+)", "$1=<private>");
        return v;
    }

    private static void AppendReportLine(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();
        if (v == "—" || v == "-") return;
        sb.AppendLine($"  {label}: {v}");
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
