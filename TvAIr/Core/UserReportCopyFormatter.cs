using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TvAIr.Core;

/// <summary>
/// Reversible, alphanumeric transport of the existing public user-event store.
/// No separate journal, schema change, developer diagnostic or additional event producer.
/// The receiver validates every part before decoding; the text is not encryption.
/// </summary>
internal static class UserReportCopyFormatter
{
    private static readonly string[] SafeDetailKeys =
    [
        "state.before", "state.after", "blockedReason", "failureReason", "recordingFailureCause", "reason", "phase",
        "result", "runResult", "targetScope", "completedGroups", "imported", "missingGroups",
        "retryDeadline", "errorCode", "terminalReason", "startReason", "stopReason"
    ];
    private static readonly HashSet<string> AllowedDetail = new(SafeDetailKeys, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Fields =
    [
        "id", "time", "severity", "category", "result", "code", "target", "message",
        "trace", "operation", "reservation", "recording", "recovery", "resume",
        "origin", "trigger", "role", "kind", "actionability", "source", "story",
        "service", "program", "scheduledStart", "scheduledEnd", "actualStart", "actualEnd",
        "drops", "errors", "scrambles", "completionReason", "details", "app", "previousApp", "versionChanged"
    ];

    internal static string Build(IReadOnlyList<UserEventLogEntry> stored, TimeSpan window, string tvairVersion, DateTime outputAt)
    {
        var opaqueIds = new Dictionary<string, int>(StringComparer.Ordinal);
        int? Ref(string kind, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var key = kind + "\u001f" + value;
            if (!opaqueIds.TryGetValue(key, out var id)) opaqueIds.Add(key, id = opaqueIds.Count + 1);
            return id;
        }

        var events = new List<object?[]>(stored.Count);
        foreach (var e in stored.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id))
        {
            events.Add(
            [
                e.Id, Date(e.CreatedAt), Clean(e.Severity), Clean(e.Category), Clean(e.Result), Clean(e.Code),
                Clean(e.Target), Clean(UserLogProjectionService.BuildStandardMessage(e)),
                Ref("operation", e.TraceId), Ref("operation", e.OperationId), Ref("reservation", e.ReservationId),
                Ref("recording", e.RecordingId), Ref("recovery", e.RecordingRecoveryChainId), Ref("resume", e.PowerResumeCycleId),
                Clean(e.Origin), Clean(e.Trigger), Clean(e.StoryRole), Clean(e.TargetKind), Clean(e.Actionability),
                Clean(e.ReservationSource), Clean(e.StoryContext), Clean(e.ServiceName), Clean(e.ProgramTitle),
                Date(e.ScheduledStart), Date(e.ScheduledEnd), Date(e.ActualStart), Date(e.ActualEnd),
                e.DropCount, e.ErrorCount, e.ScrambleCount, Clean(e.CompletionReason), SafeDetails(e),
                Clean(e.AppVersion), Clean(e.PreviousAppVersion), e.VersionChanged ? 1 : 0
            ]);
        }
        var payload = JsonSerializer.Serialize(new
        {
            schema = 2,
            version = Clean(tvairVersion),
            generated = Date(outputAt),
            hours = Math.Round(window.TotalHours, 3),
            count = events.Count,
            fields = Fields,
            events
        });
        var data = Encoding.UTF8.GetBytes(payload);
        using var destination = new MemoryStream();
        using (var stream = new BrotliStream(destination, CompressionLevel.Optimal, leaveOpen: true))
            stream.Write(data);
        return ReportAlphanumericTransport.Encode(destination.ToArray());
    }

    private static string? Date(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture);
    private static string Date(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static SortedDictionary<string, string>? SafeDetails(UserEventLogEntry e)
    {
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(e.DetailsJson))
        {
            try
            {
                using var parsed = JsonDocument.Parse(e.DetailsJson);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in parsed.RootElement.EnumerateObject())
                        if (AllowedDetail.Contains(property.Name) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                            details[property.Name] = Clean(property.Value.ToString());
                }
            }
            catch (JsonException) { /* A malformed optional detail must not break the report. */ }
        }
        if (!string.IsNullOrWhiteSpace(e.Detail))
        {
            foreach (var part in e.Detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = part.IndexOf('=');
                if (equals <= 0) continue;
                var name = part[..equals].Trim();
                if (AllowedDetail.Contains(name)) details[name] = Clean(part[(equals + 1)..]);
            }
        }
        return details.Count == 0 ? null : details;
    }

    // The report is publishable outside the machine. A free-text value containing a
    // local path, UNC share, URL or credential assignment cannot be safely split at
    // spaces or semicolons: these are legal in paths and secret values. Drop the
    // entire suffix instead of guessing where the private value ends.
    private static readonly Regex UnsafeFreeTextStart = new(
        @"(?ix)(?<![A-Za-z0-9])(?:[A-Z]:[\\/]|\\\\[^\\\s]+\\|/(?:home|Users|mnt|Volumes|var|tmp|etc|opt|run|private)/|https?://|(?:password|passwd|token|api[_-]?key|secret|authorization|user(?:name)?|account|computername|pcname)\s*[:=])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MailAddress = new(
        @"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4 = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var unsafeStart = UnsafeFreeTextStart.Match(result);
        if (unsafeStart.Success)
            result = result[..unsafeStart.Index] + "REDACTED";
        result = MailAddress.Replace(result, "REDACTED");
        result = Ipv4.Replace(result, "REDACTED");
        return UserLogProjectionService.WithoutInternalReservationNumbers(result);
    }

}

/// <summary>Wire format: TVR2 + SHA256 prefix(16 hex) + byte length(8 hex) + part number(4 decimal) + part count(4 decimal) + base62 data.</summary>
internal static class ReportAlphanumericTransport
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Prefix = "TVR2";
    internal const int MaxPartChars = 1700;
    private const int BlockBytes = 32;
    private const int BlockChars = 43;
    private const int HeaderChars = 36;
    private const int MaxCompressedBytes = 8 * 1024 * 1024;
    private const int MaxJsonChars = 16 * 1024 * 1024;

    internal static string Encode(byte[] compressed)
    {

        if (compressed.Length > MaxCompressedBytes) throw new InvalidOperationException("Report exceeds transport capacity.");
        var coded = new StringBuilder();
        for (var offset = 0; offset < compressed.Length; offset += BlockBytes)
        {
            var length = Math.Min(BlockBytes, compressed.Length - offset);
            var number = new BigInteger(compressed.AsSpan(offset, length), isUnsigned: true, isBigEndian: true);
            var width = length == BlockBytes ? BlockChars : Width(length);
            var chars = new char[width];
            for (var i = width - 1; i >= 0; i--)
            {
                number = BigInteger.DivRem(number, 62, out var digit);
                chars[i] = Alphabet[(int)digit];
            }
            if (number != BigInteger.Zero) throw new InvalidOperationException("Base62 block overflow.");
            coded.Append(chars);
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(compressed))[..16];
        var partWidth = MaxPartChars - HeaderChars;
        var parts = Math.Max(1, (coded.Length + partWidth - 1) / partWidth);
        if (parts > 9999) throw new InvalidOperationException("Too many report parts.");
        var output = new StringBuilder();
        for (var i = 0; i < parts; i++)
        {
            if (i > 0) output.Append('\n');
            output.Append(Prefix).Append(fingerprint)
                .Append(compressed.Length.ToString("X8", CultureInfo.InvariantCulture))
                .Append((i + 1).ToString("D4", CultureInfo.InvariantCulture))
                .Append(parts.ToString("D4", CultureInfo.InvariantCulture));
            var start = i * partWidth;
            output.Append(coded.ToString(start, Math.Min(partWidth, coded.Length - start)));
        }
        return output.ToString();
    }

    internal static byte[] Decode(string report)
    {
        var lines = report.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) throw new FormatException("No report parts.");
        string? reportId = null;
        var chunks = new Dictionary<int, string>();
        int expectedParts = -1, expectedLength = -1;
        foreach (var part in lines)
        {
            if (part.Length < HeaderChars || !part.StartsWith(Prefix, StringComparison.Ordinal))
                throw new FormatException("Invalid report header.");
            var id = part.Substring(4, 16);
            if (!Regex.IsMatch(id, "^[0-9A-F]{16}$")) throw new FormatException("Invalid report checksum.");
            if (!int.TryParse(part.Substring(20, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var len) || len < 0
                || !int.TryParse(part.Substring(28, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || !int.TryParse(part.Substring(32, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || index < 1 || count < 1 || index > count)
                throw new FormatException("Invalid part index.");
            if (len > MaxCompressedBytes || part.Length > MaxPartChars) throw new FormatException("Report part exceeds size limit.");
            if (reportId is not null && (reportId != id || expectedParts != count || expectedLength != len))
                throw new FormatException("Mixed report parts.");
            reportId = id;
            expectedParts = count;
            expectedLength = len;
            if (!chunks.TryAdd(index, part[HeaderChars..])) throw new FormatException("Duplicate report part.");
        }
        if (chunks.Count != expectedParts) throw new FormatException("Missing report part.");
        var coded = string.Concat(Enumerable.Range(1, expectedParts).Select(i => chunks[i]));
        var wholeBlocks = expectedLength / BlockBytes;
        var remainder = expectedLength % BlockBytes;
        var wantedChars = wholeBlocks * BlockChars + (remainder == 0 ? 0 : Width(remainder));
        if (coded.Length != wantedChars) throw new FormatException("Unexpected report data length.");
        var bytes = new byte[expectedLength];
        var position = 0;
        for (var block = 0; block < wholeBlocks + (remainder > 0 ? 1 : 0); block++)
        {
            var count = block < wholeBlocks ? BlockBytes : remainder;
            var width = count == BlockBytes ? BlockChars : Width(count);
            var number = BigInteger.Zero;
            for (var i = 0; i < width; i++)
            {
                var c = coded[position + i];
                var digit = Alphabet.IndexOf(c);
                if (digit < 0) throw new FormatException("Non-alphanumeric report data.");
                number = number * 62 + digit;
            }
            var data = number.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (data.Length > count) throw new FormatException("Report block overflow.");
            data.CopyTo(bytes.AsSpan(block * BlockBytes + count - data.Length));
            position += width;
        }
        if (Convert.ToHexString(SHA256.HashData(bytes))[..16] != reportId)
            throw new FormatException("Report checksum mismatch.");
        return bytes;
    }

    internal static string DecodeJson(string report)
    {
        var bytes = Decode(report);
        using var source = new MemoryStream(bytes);
        using var brotli = new BrotliStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, new UTF8Encoding(false, true));
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length > MaxJsonChars - read) throw new FormatException("Report payload exceeds size limit.");
            text.Append(buffer, 0, read);
        }
        var json = text.ToString();
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.GetProperty("schema").GetInt32() != 2)
            throw new FormatException("Unsupported report version.");
        return json;
    }

    private static int Width(int bytes) => (int)Math.Ceiling(bytes * 8 / Math.Log2(62));
}
