using System.Text.Json;
using System.Text.RegularExpressions;
using TvAIrPlugin;
using TvAIr.Plugin;

namespace TvAIr.Core;

public static class UserLogProjectionService
{
    private static readonly Regex InternalReservationNumber = new(
        @"(?i)(?:予約ID|録画ID|reservationId|recordingId)\s*[:=：]\s*R[0-9]+(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string WithoutInternalReservationNumbers(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : InternalReservationNumber.Replace(text, string.Empty).Trim();

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
        var visible = WithoutInternalReservationNumbers(firstLine);
        return string.IsNullOrWhiteSpace(visible) ? "—" : visible;
    }

    public static string BuildMessage(UserEventLogEntry entry, TvAirLogPresentationPolicyDto policy)
    {
        var baseMessage = BuildStandardMessage(entry);
        if (!policy.Enabled || policy.DetailKeys is null || policy.DetailKeys.Count == 0) return baseMessage;

        var stored = ReadDetails(entry);
        var details = policy.DetailKeys
            .Select(key => ResolveDetail(entry, stored, key))
            .Select(WithoutInternalReservationNumbers)
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

        // Host-managed keys remain reusable for other presenters.
        if (string.Equals(key, TvAirLogDetailKeys.ProgramTitle, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.ProgramTitle) ? null : $"番組: {entry.ProgramTitle}";
        // Internal correlation identifiers belong to persisted records, not presentation text.
        if (string.Equals(key, TvAirLogDetailKeys.ReservationId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, TvAirLogDetailKeys.RecordingId, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(key, TvAirLogDetailKeys.RecordingFilePath, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(entry.FilePath) ? null : $"保存先: {entry.FilePath}";

        return stored.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? $"{key}: {value}"
            : null;
    }
}
