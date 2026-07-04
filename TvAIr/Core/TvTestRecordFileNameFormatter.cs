using System.Text;
using System.Text.RegularExpressions;

namespace TvAIr.Core;

/// <summary>
/// TVTest.ini の RecordFileName 設定を、TvAIrEpgRec 直録画用の相対パスへ展開する。
/// ユーザーがTVTest側で設定した命名規則を尊重し、TvAIr独自の全角→半角強制変換は行わない。
/// </summary>
public static class TvTestRecordFileNameFormatter
{
    public const string Rule = "recordfilename_ini_template";

    private static readonly Regex ShortMarkRegex = new(@"\[[^\[\]]{1,3}\]", RegexOptions.Compiled);

    public static TvTestRecordFileNameFormatResult Format(TvTestRecordFileNameFormatRequest request)
    {
        var template = string.IsNullOrWhiteSpace(request.Template)
            ? "%year2%年%month2%月%day2%日%hour2%時%minute2%分-%event-name%.ts"
            : request.Template;

        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = BuildValues(request);
        var rendered = RenderConfiguredTemplate(template, values, unknown);
        rendered = NormalizeRenderedRelativePath(rendered);

        if (string.IsNullOrWhiteSpace(Path.GetExtension(rendered)))
            rendered += ".ts";

        return new TvTestRecordFileNameFormatResult(
            FileName: rendered,
            EventName: values["event-name"],
            EventTitle: values["event-title"],
            EventMark: values["event-mark"],
            ServiceName: values["service-name"],
            ChannelName: values["channel-name"],
            UnknownTokens: unknown.Count == 0 ? "-" : string.Join(',', unknown),
            Rule: Rule);
    }

    private static Dictionary<string, string> BuildValues(TvTestRecordFileNameFormatRequest request)
    {
        var now = request.Now;
        var start = request.StartTime;
        var end = request.EndTime;
        var tot = request.TotTime ?? now;
        var duration = request.EndTime > request.StartTime
            ? request.EndTime - request.StartTime
            : TimeSpan.Zero;

        var eventName = ReplacePathUnsafeCharacters(request.EventName);
        var serviceName = ReplacePathUnsafeCharacters(string.IsNullOrWhiteSpace(request.ServiceName) ? "TvAIr" : request.ServiceName);
        var channelName = ReplacePathUnsafeCharacters(string.IsNullOrWhiteSpace(request.ChannelName) ? serviceName : request.ChannelName);
        var eventTitleRaw = ShortMarkRegex.Replace(eventName, string.Empty).Trim();
        var eventMarkRaw = string.Concat(ShortMarkRegex.Matches(eventName).Cast<Match>().Select(m => m.Value));
        var tunerFileName = ReplacePathUnsafeCharacters(Path.GetFileName(request.TunerFileName ?? string.Empty));
        var tunerName = ReplacePathUnsafeCharacters(ResolveTunerName(request.TunerName, request.TunerFileName));
        var channelNo = ReplacePathUnsafeCharacters(request.ChannelNo ?? string.Empty);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channel-name"] = channelName,
            ["channel-no"] = channelNo,
            ["channel-no2"] = PadNumber(channelNo, 2),
            ["channel-no3"] = PadNumber(channelNo, 3),
            ["event-name"] = eventName,
            ["event-title"] = ReplacePathUnsafeCharacters(eventTitleRaw),
            ["event-mark"] = ReplacePathUnsafeCharacters(eventMarkRaw),
            ["event-id"] = request.EventId == 0 ? string.Empty : request.EventId.ToString("X4"),
            ["service-name"] = serviceName,
            ["service-id"] = request.ServiceId == 0 ? string.Empty : request.ServiceId.ToString("X4"),
            ["tuner-filename"] = tunerFileName,
            ["tuner-name"] = tunerName,
            ["event-duration-hour"] = ((int)duration.TotalHours).ToString(),
            ["event-duration-hour2"] = ((int)duration.TotalHours).ToString("D2"),
            ["event-duration-min"] = duration.Minutes.ToString(),
            ["event-duration-min2"] = duration.Minutes.ToString("D2"),
            ["event-duration-sec"] = duration.Seconds.ToString(),
            ["event-duration-sec2"] = duration.Seconds.ToString("D2"),
        };

        AddDateTimeValues(values, string.Empty, now);
        AddDateTimeValues(values, "start-", start);
        AddDateTimeValues(values, "end-", end);
        AddDateTimeValues(values, "tot-", tot);
        return values;
    }

    private static void AddDateTimeValues(Dictionary<string, string> values, string prefix, DateTime value)
    {
        values[$"{prefix}date"] = value.ToString("yyyyMMdd");
        values[$"{prefix}year"] = value.ToString("yyyy");
        values[$"{prefix}year2"] = value.ToString("yy");
        values[$"{prefix}month"] = value.Month.ToString();
        values[$"{prefix}month2"] = value.ToString("MM");
        values[$"{prefix}day"] = value.Day.ToString();
        values[$"{prefix}day2"] = value.ToString("dd");
        values[$"{prefix}time"] = value.ToString("HHmmss");
        values[$"{prefix}hour"] = value.Hour.ToString();
        values[$"{prefix}hour2"] = value.ToString("HH");
        values[$"{prefix}minute"] = value.Minute.ToString();
        values[$"{prefix}minute2"] = value.ToString("mm");
        values[$"{prefix}second"] = value.Second.ToString();
        values[$"{prefix}second2"] = value.ToString("ss");
        values[$"{prefix}day-of-week"] = DayOfWeekText(value.DayOfWeek);
    }

    private static string RenderConfiguredTemplate(string template, Dictionary<string, string> values, SortedSet<string> unknown)
    {
        var output = new StringBuilder(template.Length + 32);
        string? pendingSeparator = null;

        void AppendValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (pendingSeparator is not null && HasEffectiveText(output.ToString()))
                output.Append(pendingSeparator);
            pendingSeparator = null;
            output.Append(value);
        }

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '%')
            {
                AppendValue(c.ToString());
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '%')
            {
                AppendValue("%");
                i++;
                continue;
            }

            var end = template.IndexOf('%', i + 1);
            if (end < 0)
            {
                AppendValue("%");
                continue;
            }

            var key = template.Substring(i + 1, end - i - 1);
            i = end;
            if (string.Equals(key, "sep-hyphen", StringComparison.OrdinalIgnoreCase))
            {
                pendingSeparator = "-";
                continue;
            }
            if (string.Equals(key, "sep-slash", StringComparison.OrdinalIgnoreCase))
            {
                pendingSeparator = "／";
                continue;
            }
            if (string.Equals(key, "sep-backslash", StringComparison.OrdinalIgnoreCase))
            {
                pendingSeparator = "\\";
                continue;
            }
            if (values.TryGetValue(key, out var replacement))
            {
                AppendValue(replacement);
                continue;
            }

            var original = $"%{key}%";
            unknown.Add(original);
            AppendValue(original);
        }

        return output.ToString();
    }

    private static string NormalizeRenderedRelativePath(string value)
    {
        var normalized = value.Replace('/', '\\');
        var parts = normalized.Split('\\', StringSplitOptions.None)
            .Select(NormalizePathSegment)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        var joined = string.Join(Path.DirectorySeparatorChar, parts);
        return string.IsNullOrWhiteSpace(joined) ? "TvAIr.ts" : joined;
    }

    private static string NormalizePathSegment(string value)
    {
        var trimmed = ReplacePathUnsafeCharacters(value).Trim();
        trimmed = Regex.Replace(trimmed, @"\s+", " ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;
        if (IsReservedDeviceName(trimmed)) trimmed = $"_{trimmed}";
        return trimmed;
    }

    public static string ReplacePathUnsafeCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            buffer.Append(c switch
            {
                '\\' => '￥',
                '/' => '／',
                ':' => '：',
                '*' => '＊',
                '?' => '？',
                '"' => '”',
                '<' => '＜',
                '>' => '＞',
                '|' => '｜',
                _ when char.IsControl(c) => ' ',
                _ => c
            });
        }
        return buffer.ToString();
    }

    private static bool HasEffectiveText(string value)
        => value.Any(c => !char.IsWhiteSpace(c) && c is not '-' and not '／' and not '\\');

    private static string DayOfWeekText(DayOfWeek day)
        => day switch
        {
            DayOfWeek.Sunday => "日",
            DayOfWeek.Monday => "月",
            DayOfWeek.Tuesday => "火",
            DayOfWeek.Wednesday => "水",
            DayOfWeek.Thursday => "木",
            DayOfWeek.Friday => "金",
            DayOfWeek.Saturday => "土",
            _ => string.Empty
        };

    private static string PadNumber(string value, int width)
        => int.TryParse(value, out var n) ? n.ToString($"D{width}") : value;

    private static string ResolveTunerName(string? tunerName, string? tunerFileName)
    {
        if (!string.IsNullOrWhiteSpace(tunerName)) return tunerName.Trim();
        var name = Path.GetFileNameWithoutExtension(tunerFileName ?? string.Empty);
        if (name.StartsWith("BonDriver_", StringComparison.OrdinalIgnoreCase))
            name = name["BonDriver_".Length..];
        return name;
    }

    private static bool IsReservedDeviceName(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && name[3] >= '1' && name[3] <= '9') return true;
        return false;
    }
}

public sealed record TvTestRecordFileNameFormatRequest(
    string Template,
    DateTime Now,
    DateTime StartTime,
    DateTime EndTime,
    DateTime? TotTime,
    string EventName,
    string ServiceName,
    string ChannelName,
    string ChannelNo,
    ushort ServiceId,
    ushort EventId,
    string TunerFileName,
    string TunerName);

public sealed record TvTestRecordFileNameFormatResult(
    string FileName,
    string EventName,
    string EventTitle,
    string EventMark,
    string ServiceName,
    string ChannelName,
    string UnknownTokens,
    string Rule);
