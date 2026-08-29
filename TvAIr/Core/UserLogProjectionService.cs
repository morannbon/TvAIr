using System.Text.Json;
using TvAIrPlugin;
using TvAIr.Plugin;

namespace TvAIr.Core;

public static class UserLogProjectionService
{
    // USER_LOG_PRESENTATION_INVARIANT
    // 標準ログは必ずシンプルな1行表示にする。
    // 詳細ログはユーザーが能動的に有効化し、チェックした項目だけを多段表示する。
    // 詳細向け情報を標準へ混入させず、標準と詳細を統合・置換しない。
    public static TvAirLogPresentationPolicyDto? CreateHostPolicy(string viewKey, IniSettingsService ini)
    {
        if (!string.Equals(viewKey, "reservation-log", StringComparison.OrdinalIgnoreCase)) return null;
        if (!ini.UserLogDetailEnabled) return null;

        var keys = new List<string>();
        if (ini.UserLogDetailReservationSource)
            keys.Add(TvAirLogDetailKeys.ReservationSource);
        if (ini.UserLogDetailScheduledTime)
        {
            keys.Add(TvAirLogDetailKeys.ScheduleStart);
            keys.Add(TvAirLogDetailKeys.ScheduleEnd);
        }
        if (ini.UserLogDetailActualRecordingTime)
        {
            keys.Add(TvAirLogDetailKeys.RecordingActualStart);
            keys.Add(TvAirLogDetailKeys.RecordingActualEnd);
        }
        if (ini.UserLogDetailRecordingQuality)
        {
            keys.Add(TvAirLogDetailKeys.RecordingQualityDrop);
            keys.Add(TvAirLogDetailKeys.RecordingQualityError);
            keys.Add(TvAirLogDetailKeys.RecordingQualityScramble);
        }
        if (ini.UserLogDetailStateChange)
        {
            keys.Add(TvAirLogDetailKeys.StateBefore);
            keys.Add(TvAirLogDetailKeys.StateAfter);
        }
        if (ini.UserLogDetailEndOrFailureReason)
            keys.Add(TvAirLogDetailKeys.Reason);

        return new TvAirLogPresentationPolicyDto
        {
            ViewKey = "reservation-log",
            Title = "ログ",
            Enabled = true,
            DetailKeys = keys,
            Layout = TvAirLogDetailLayout.Multiline,
            HideEmptyDetails = true,
            Priority = 0,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    public static string BuildStandardMessage(UserEventLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Message)) return "—";
        var firstLine = entry.Message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "—" : firstLine;
    }

    public static string BuildMessage(UserEventLogEntry entry, TvAirLogPresentationPolicyDto policy)
    {
        var baseMessage = BuildStandardMessage(entry);
        if (!policy.Enabled || policy.DetailKeys is null || policy.DetailKeys.Count == 0) return baseMessage;

        var stored = ReadDetails(entry);
        var details = policy.DetailKeys
            .Select(key => ResolveDetail(entry, stored, key))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        if (details.Length == 0) return baseMessage;
        return policy.Layout == TvAirLogDetailLayout.Multiline
            ? string.Join("\n", new[] { baseMessage }.Concat(details))
            : string.Join(" ", new[] { baseMessage }.Concat(details));
    }

    public static TvAirLogPresentationSnapshotDto BuildSnapshot(TvAirLogPresentationPolicyDto policy, IReadOnlyList<UserEventLogEntry> rows)
    {
        return new TvAirLogPresentationSnapshotDto
        {
            ViewKey = policy.ViewKey,
            Title = string.IsNullOrWhiteSpace(policy.Title) ? "ログ" : policy.Title,
            Summary = "TvAIrのユーザー運用ログを詳細表示します",
            ReplaceHostDefault = true,
            Priority = policy.Priority,
            UpdatedAt = DateTimeOffset.Now,
            Entries = rows.Select(e =>
            {
                var message = BuildMessage(e, policy);
                return new TvAirLogPresentationEntryDto
                {
                    EntryId = string.Empty,
                    Timestamp = new DateTimeOffset(e.CreatedAt),
                    Severity = e.Severity,
                    Category = e.Category,
                    ReservationId = string.Empty,
                    ServiceName = string.Empty,
                    ProgramTitle = string.Empty,
                    Target = e.Target,
                    TargetTextMode = "singleline",
                    ResultTextMode = "singleline",
                    MessageTextMode = message.Contains('\n') ? "multiline" : "singleline",
                    Message = message,
                    Result = e.Result,
                DropCount = null,
                ErrorCount = null,
                ScrambleCount = null,
                FilePath = string.Empty,
                    Details = new Dictionary<string, string>()
                };
            }).ToArray()
        };
    }

    private static IReadOnlyDictionary<string, string> ReadDetails(UserEventLogEntry entry)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailsJson ?? "{}")
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveDetail(UserEventLogEntry entry, IReadOnlyDictionary<string, string> stored, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        static string DateText(DateTime value) => value.ToString("yyyy/MM/dd HH:mm:ss");

        if (string.Equals(key, TvAirLogDetailKeys.ReservationSource, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.ReservationSource) ? null : $"予約元: {entry.ReservationSource}";
        if (string.Equals(key, TvAirLogDetailKeys.ScheduleStart, StringComparison.OrdinalIgnoreCase))
            return entry.ScheduledStart.HasValue ? $"開始予定: {DateText(entry.ScheduledStart.Value)}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.ScheduleEnd, StringComparison.OrdinalIgnoreCase))
            return entry.ScheduledEnd.HasValue ? $"終了予定: {DateText(entry.ScheduledEnd.Value)}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.RecordingActualStart, StringComparison.OrdinalIgnoreCase))
            return entry.ActualStart.HasValue ? $"実開始: {DateText(entry.ActualStart.Value)}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.RecordingActualEnd, StringComparison.OrdinalIgnoreCase))
            return entry.ActualEnd.HasValue ? $"実終了: {DateText(entry.ActualEnd.Value)}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.RecordingQualityDrop, StringComparison.OrdinalIgnoreCase))
            return entry.DropCount.HasValue ? $"drop={entry.DropCount.Value}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.RecordingQualityError, StringComparison.OrdinalIgnoreCase))
            return entry.ErrorCount.HasValue ? $"error={entry.ErrorCount.Value}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.RecordingQualityScramble, StringComparison.OrdinalIgnoreCase))
            return entry.ScrambleCount.HasValue ? $"scramble={entry.ScrambleCount.Value}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.Reason, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.CompletionReason) ? null : $"理由: {entry.CompletionReason}";
        if (string.Equals(key, TvAirLogDetailKeys.StateBefore, StringComparison.OrdinalIgnoreCase))
            return stored.TryGetValue(TvAirLogDetailKeys.StateBefore, out var before) && !string.IsNullOrWhiteSpace(before) ? $"変更前: {before}" : null;
        if (string.Equals(key, TvAirLogDetailKeys.StateAfter, StringComparison.OrdinalIgnoreCase))
            return stored.TryGetValue(TvAirLogDetailKeys.StateAfter, out var after) && !string.IsNullOrWhiteSpace(after) ? $"変更後: {after}" : null;

        // Generic API compatibility: host-managed keys remain reusable for other presenters.
        if (string.Equals(key, TvAirLogDetailKeys.ProgramTitle, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.ProgramTitle) ? null : $"番組: {entry.ProgramTitle}";
        if (string.Equals(key, TvAirLogDetailKeys.ReservationId, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.ReservationId) ? null : $"予約ID: {entry.ReservationId}";
        if (string.Equals(key, TvAirLogDetailKeys.RecordingId, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.RecordingId) ? null : $"録画ID: {entry.RecordingId}";
        if (string.Equals(key, TvAirLogDetailKeys.RecordingFilePath, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.FilePath) ? null : $"保存先: {entry.FilePath}";

        return stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? $"{key}: {value}"
            : null;
    }
}

internal static class UserLogProjectionContractTests
{
    public static void Run()
    {
        var row = new UserEventLogEntry
        {
            OperationId = "op-test",
            CreatedAt = new DateTime(2026, 7, 12, 12, 0, 0),
            Severity = "INFO",
            Category = "録画",
            Result = "OK",
            Target = "テスト局",
            Message = "録画を終了しました",
            ReservationSource = "番組表",
            ScheduledStart = new DateTime(2026, 7, 12, 11, 0, 0),
            ActualStart = new DateTime(2026, 7, 12, 11, 0, 1),
            DropCount = 0,
            CompletionReason = "正常終了",
            ReservationId = "R1",
            RecordingId = "REC1",
            FilePath = @"C:\private\title.ts",
            DetailsJson = "{\"state.before\":\"recording\",\"state.after\":\"completed\"}"
        };
        var policy = new TvAirLogPresentationPolicyDto
        {
            ViewKey = "reservation-log",
            Enabled = true,
            Layout = TvAirLogDetailLayout.Multiline,
            DetailKeys = new[]
            {
                TvAirLogDetailKeys.ReservationSource,
                TvAirLogDetailKeys.ScheduleStart,
                TvAirLogDetailKeys.RecordingQualityDrop,
                TvAirLogDetailKeys.Reason,
                "unknown.key"
            }
        };
        var snapshot = UserLogProjectionService.BuildSnapshot(policy, new[] { row });
        var projected = snapshot.Entries.Single();
        if (snapshot.Entries.Count != 1)
            throw new InvalidOperationException("User log projection changed the operation row set.");
        if (projected.Target != row.Target || projected.Timestamp.DateTime != row.CreatedAt || projected.Category != row.Category || projected.Result != row.Result)
            throw new InvalidOperationException("User log projection changed a host-owned base column.");
        if (!projected.Message.StartsWith(row.Message, StringComparison.Ordinal) || !projected.Message.Contains("予約元: 番組表") || !projected.Message.Contains("drop=0") || projected.Message.Contains("REC1") || projected.Message.Contains("private"))
            throw new InvalidOperationException("User log detail selection contract failed.");
        if (!projected.Message.Contains('\n'))
            throw new InvalidOperationException("Extended user log must be multiline.");

        var legacyMultilineRow = new UserEventLogEntry
        {
            CreatedAt = row.CreatedAt,
            Severity = row.Severity,
            Category = row.Category,
            Result = row.Result,
            Target = row.Target,
            Message = "録画を開始しました\n予約ID: R1\n録画ID: R1"
        };
        if (UserLogProjectionService.BuildStandardMessage(legacyMultilineRow) != "録画を開始しました")
            throw new InvalidOperationException("Standard user log must remain one line.");
        var noDetailPolicy = new TvAirLogPresentationPolicyDto
        {
            ViewKey = "reservation-log",
            Enabled = true,
            Layout = TvAirLogDetailLayout.Multiline,
            DetailKeys = new[] { TvAirLogDetailKeys.RecordingActualEnd }
        };
        var noDetail = UserLogProjectionService.BuildSnapshot(noDetailPolicy, new[] { legacyMultilineRow }).Entries.Single();
        if (noDetail.MessageTextMode != "singleline" || noDetail.Message.Contains('\n'))
            throw new InvalidOperationException("Rows without projected details must remain single line.");

        var timeFollowRow = new UserEventLogEntry
        {
            Message = "放送時間を更新しました: テスト番組",
            ScheduledStart = new DateTime(2026, 7, 12, 12, 5, 0),
            ScheduledEnd = new DateTime(2026, 7, 12, 12, 35, 0),
            DetailsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [TvAirLogDetailKeys.StateBefore] = "2026/07/12 12:00:00〜2026/07/12 12:30:00",
                [TvAirLogDetailKeys.StateAfter] = "2026/07/12 12:05:00〜2026/07/12 12:35:00"
            })
        };
        var timeFollowPolicy = new TvAirLogPresentationPolicyDto
        {
            ViewKey = "reservation-log",
            Enabled = true,
            Layout = TvAirLogDetailLayout.Multiline,
            DetailKeys = new[]
            {
                TvAirLogDetailKeys.ScheduleStart,
                TvAirLogDetailKeys.ScheduleEnd,
                TvAirLogDetailKeys.StateBefore,
                TvAirLogDetailKeys.StateAfter
            }
        };
        var timeFollowMessage = UserLogProjectionService.BuildMessage(timeFollowRow, timeFollowPolicy);
        if (!timeFollowMessage.StartsWith("放送時間を更新しました: テスト番組\n", StringComparison.Ordinal) ||
            !timeFollowMessage.Contains("変更前: 2026/07/12 12:00:00〜2026/07/12 12:30:00", StringComparison.Ordinal) ||
            !timeFollowMessage.Contains("変更後: 2026/07/12 12:05:00〜2026/07/12 12:35:00", StringComparison.Ordinal))
            throw new InvalidOperationException("Time-follow details must project only through selected detail keys.");

        // Internal System EPG / PreRec reservations must never leak through the generic
        // reservation/recording user-log route. Their dedicated EPG/EPG確認 events are the only user-facing source.
        var internalPreRec = new Reservation
        {
            Source = ReservationSource.Epg,
            Intent = ReservationIntent.SystemPreRecordEpg,
            Title = "EPG確認",
            Status = ReservationStatus.Recording
        };
        if (UserEventLogService.ShouldEmitReservationOperation(internalPreRec))
            throw new InvalidOperationException("Internal PreRec reservation leaked into generic user log semantics.");

        var normalRecording = new Reservation
        {
            Source = ReservationSource.Manual,
            Intent = ReservationIntent.InteractiveProgramEvent,
            Title = "通常番組",
            Status = ReservationStatus.Recording
        };
        if (!UserEventLogService.ShouldEmitReservationOperation(normalRecording))
            throw new InvalidOperationException("Normal user reservation was suppressed from user log semantics.");

        var genericPolicy = new TvAirLogPresentationPolicyDto
        {
            ViewKey = "reservation-log",
            Enabled = true,
            Layout = TvAirLogDetailLayout.Multiline,
            DetailKeys = new[] { TvAirLogDetailKeys.ReservationId }
        };
        var store = new LogPresentationStore();
        store.SetLogPolicy("test.generic.log.presenter", genericPolicy);
        var accepted = store.GetActiveLogPolicy("reservation-log");
        if (accepted?.SourcePluginId != "test.generic.log.presenter")
            throw new InvalidOperationException("Generic presenter plugin id was not accepted.");
        var generic = UserLogProjectionService.BuildSnapshot(accepted.Policy, new[] { row }).Entries.Single();
        if (!generic.Message.Contains("予約ID: R1"))
            throw new InvalidOperationException("Generic presenter policy is not plugin-id independent.");
    }
}
