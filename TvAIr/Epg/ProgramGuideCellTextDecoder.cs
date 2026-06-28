using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// DBから読み出した1イベントのraw descriptor群を、番組表セルへ直接渡す本文ペイロードへ展開する。
/// DB raw descriptorだけを入力にし、旧本文列・旧decoded列をセル本文の入力に使わない。
/// </summary>
internal static class ProgramGuideCellTextDecoder
{
    private const int MaxCachedCellText = 8192;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CacheKey, ProgramGuideCellText> Cache = new();
    private static int cacheTrimGate;

    public static ProgramGuideCellText Decode(EpgEvent e)
    {
        var key = CacheKey.From(e);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var decoded = DecodeCore(e);
        if (Cache.Count > MaxCachedCellText) TrimCache();
        Cache.TryAdd(key, decoded);
        return decoded;
    }

    private static ProgramGuideCellText DecodeCore(EpgEvent e)
    {
        var title = string.Empty;
        var outline = string.Empty;
        var detailParts = new List<string>();
        var itemParts = new List<string>();
        var detailKeys = new HashSet<string>(StringComparer.Ordinal);
        var itemKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in ReadDescriptors(e.RawShortEventDescriptorHex, 0x4D))
        {
            var shortText = DecodeShortEventDescriptor(descriptor.Bytes);
            if (title.Length == 0 && shortText.Title.Length > 0) title = shortText.Title;
            if (outline.Length == 0 && shortText.Outline.Length > 0) outline = shortText.Outline;
        }

        foreach (var descriptor in ReadDescriptors(e.RawExtendedEventDescriptorHex, 0x4E)
            .OrderBy(d => d.DescriptorNumber)
            .ThenBy(d => d.Ordinal))
        {
            var ext = DecodeExtendedEventDescriptor(descriptor.Bytes);
            foreach (var line in ext.DetailLines)
            {
                AddUniqueCellTextLine(detailParts, detailKeys, line);
            }
            foreach (var item in ext.ItemLines)
            {
                AddUniqueCellTextLine(itemParts, itemKeys, item);
            }
        }

        return new ProgramGuideCellText(
            title,
            outline,
            JoinRawLines(detailParts),
            JoinRawLines(itemParts),
            e.RawShortEventDescriptorHex ?? string.Empty,
            e.RawExtendedEventDescriptorHex ?? string.Empty,
            "db.raw_descriptor.common_cell_decoder");
    }

    private static ShortCellText DecodeShortEventDescriptor(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 7 || descriptor[0] != 0x4D) return new ShortCellText(string.Empty, string.Empty);
        var payloadEnd = Math.Min(descriptor.Length, 2 + descriptor[1]);
        var p = 2;
        if (p + 3 > payloadEnd) return new ShortCellText(string.Empty, string.Empty);
        p += 3; // ISO_639_language_code
        if (p >= payloadEnd) return new ShortCellText(string.Empty, string.Empty);

        var eventNameLength = descriptor[p++];
        if (p + eventNameLength > payloadEnd) return new ShortCellText(string.Empty, string.Empty);
        var title = AribPsiSiDecoder.Decode(descriptor.Slice(p, eventNameLength)).Text;
        p += eventNameLength;

        if (p >= payloadEnd) return new ShortCellText(title, string.Empty);
        var textLength = descriptor[p++];
        if (p + textLength > payloadEnd) return new ShortCellText(title, string.Empty);
        var outline = AribPsiSiDecoder.Decode(descriptor.Slice(p, textLength)).Text;
        return new ShortCellText(title, outline);
    }

    private static ExtendedCellText DecodeExtendedEventDescriptor(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 7 || descriptor[0] != 0x4E) return new ExtendedCellText(Array.Empty<string>(), Array.Empty<string>());
        var payloadEnd = Math.Min(descriptor.Length, 2 + descriptor[1]);
        var p = 2;
        if (p >= payloadEnd) return new ExtendedCellText(Array.Empty<string>(), Array.Empty<string>());
        p++; // descriptor_number / last_descriptor_number
        if (p + 3 > payloadEnd) return new ExtendedCellText(Array.Empty<string>(), Array.Empty<string>());
        p += 3; // ISO_639_language_code
        if (p >= payloadEnd) return new ExtendedCellText(Array.Empty<string>(), Array.Empty<string>());

        var detailLines = new List<string>();
        var itemLines = new List<string>();
        var itemsLength = descriptor[p++];
        var itemsEnd = Math.Min(payloadEnd, p + itemsLength);
        while (p < itemsEnd)
        {
            if (p >= itemsEnd) break;
            var itemDescriptionLength = descriptor[p++];
            if (p + itemDescriptionLength > itemsEnd) break;
            var itemDescription = AribPsiSiDecoder.Decode(descriptor.Slice(p, itemDescriptionLength)).Text;
            p += itemDescriptionLength;

            if (p >= itemsEnd) break;
            var itemTextLength = descriptor[p++];
            if (p + itemTextLength > itemsEnd) break;
            var itemText = AribPsiSiDecoder.Decode(descriptor.Slice(p, itemTextLength)).Text;
            p += itemTextLength;

            if (itemDescription.Length > 0 || itemText.Length > 0)
            {
                var line = FormatDescriptorItem(itemDescription, itemText);
                if (IsItemLikeLabel(itemDescription)) itemLines.Add(line);
                else detailLines.Add(line);
            }
        }

        if (p < payloadEnd)
        {
            var textLength = descriptor[p++];
            if (p + textLength <= payloadEnd)
            {
                var detail = AribPsiSiDecoder.Decode(descriptor.Slice(p, textLength)).Text;
                if (detail.Length > 0) detailLines.Add(detail);
            }
        }
        return new ExtendedCellText(detailLines, itemLines);
    }

    private static string FormatDescriptorItem(string itemDescription, string itemText)
        => itemDescription.Length == 0 ? itemText : itemText.Length == 0 ? itemDescription : itemDescription + ": " + itemText;

    private static bool IsItemLikeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var normalized = label.Replace(" ", string.Empty).Replace("　", string.Empty);
        return normalized.Contains("出演")
            || normalized.Contains("声の出演")
            || normalized.Contains("キャスト")
            || normalized.Contains("ゲスト")
            || normalized.Contains("司会")
            || normalized.Equals("MC", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ナレーター")
            || normalized.Contains("語り")
            || normalized.Contains("解説")
            || normalized.Contains("実況")
            || normalized.Contains("原作")
            || normalized.Contains("監督")
            || normalized.Contains("脚本")
            || normalized.Contains("音楽")
            || normalized.Contains("スタッフ");
    }

    private static IEnumerable<RawDescriptor> ReadDescriptors(string? rawHex, byte expectedTag)
    {
        if (string.IsNullOrWhiteSpace(rawHex)) yield break;
        var ordinal = 0;
        var seenDescriptors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in rawHex.Split(new[] { ';', ',', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = TryParseHex(token);
            if (bytes.Length >= 2 && bytes[0] == expectedTag)
            {
                var declaredLength = bytes[1];
                var totalLength = declaredLength + 2;
                if (totalLength <= bytes.Length)
                {
                    var descriptor = bytes.Take(totalLength).ToArray();
                    var descriptorKey = Convert.ToHexString(descriptor);
                    if (seenDescriptors.Add(descriptorKey))
                    {
                        var descriptorNumber = expectedTag == 0x4E && descriptor.Length > 2 ? (descriptor[2] >> 4) & 0x0F : 0;
                        yield return new RawDescriptor(descriptor, descriptorNumber, ordinal);
                    }
                }
            }
            ordinal++;
        }
    }

    private static void AddUniqueCellTextLine(List<string> target, HashSet<string> keys, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = value.Trim();
        var key = NormalizeCellTextLineKey(text);
        if (key.Length == 0) return;
        if (keys.Add(key)) target.Add(text);
    }

    private static string NormalizeCellTextLineKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = new List<char>(value.Length);
        var inWhitespace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace) chars.Add(' ');
                inWhitespace = true;
            }
            else
            {
                chars.Add(ch);
                inWhitespace = false;
            }
        }
        return new string(chars.ToArray());
    }

    private static byte[] TryParseHex(string raw)
    {
        var hex = new string((raw ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
        if (hex.Length < 2) return Array.Empty<byte>();
        if ((hex.Length & 1) == 1) hex = hex[..^1];
        var bytes = new byte[hex.Length / 2];
        try
        {
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static string JoinRawLines(IEnumerable<string> lines)
        => string.Join("\n", lines.Where(x => !string.IsNullOrEmpty(x)));

    private static void TrimCache()
    {
        if (System.Threading.Interlocked.Exchange(ref cacheTrimGate, 1) != 0) return;
        try
        {
            if (Cache.Count <= MaxCachedCellText) return;
            var remove = Math.Max(512, Cache.Count - MaxCachedCellText + 512);
            foreach (var key in Cache.Keys.Take(remove))
            {
                Cache.TryRemove(key, out _);
            }
        }
        finally
        {
            System.Threading.Volatile.Write(ref cacheTrimGate, 0);
        }
    }

    private readonly record struct CacheKey(
        ushort NetworkId,
        ushort TransportStreamId,
        ushort ServiceId,
        ushort EventId,
        DateTime Start,
        DateTime End,
        string RawShortDescriptorHex,
        string RawExtendedDescriptorHex)
    {
        public static CacheKey From(EpgEvent e)
            => new(
                e.NetworkId,
                e.TransportStreamId,
                e.ServiceId,
                e.EventId,
                e.Start,
                e.End,
                e.RawShortEventDescriptorHex ?? string.Empty,
                e.RawExtendedEventDescriptorHex ?? string.Empty);
    }

    private readonly record struct RawDescriptor(byte[] Bytes, int DescriptorNumber, int Ordinal);
    private readonly record struct ShortCellText(string Title, string Outline);
    private sealed record ExtendedCellText(IReadOnlyList<string> DetailLines, IReadOnlyList<string> ItemLines);
}

public sealed record ProgramGuideCellText(
    string Title,
    string Outline,
    string Detail,
    string Items,
    [property: System.Text.Json.Serialization.JsonIgnore] string RawShortEventDescriptorHex,
    [property: System.Text.Json.Serialization.JsonIgnore] string RawExtendedEventDescriptorHex,
    string Source);
