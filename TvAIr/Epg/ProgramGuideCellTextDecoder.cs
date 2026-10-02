using System.Buffers;
using TvAIr.Core;

namespace TvAIr.Epg;

/// <summary>
/// DBから読み出した1イベントのraw descriptor群を、番組表セルへ直接渡す本文ペイロードへ展開する。
/// DB raw descriptorを番組表セル本文の正本として使用する。
/// </summary>
internal static class ProgramGuideCellTextDecoder
{
    private const int MaxCachedCellText = 8192;
    private const int MaxDescriptorBytes = 257; // tag + length + payload(<=255)
    private const int StackDescriptorSliceCapacity = 24;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CacheKey, ProgramGuideCellText> Cache = new();
    private static int cacheTrimGate;

    public static ProgramGuideCellText Decode(EpgEvent e)
    {
        // DB rows have an UpdatedAt generation. Cache only those rows: the key stays fixed-size and
        // does not retain raw-descriptor strings across EPG generations.
        if (e.UpdatedAt == default) return DecodeCore(e);

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
        DecodeShortDescriptors(e.RawShortEventDescriptorHex, ref title, ref outline);

        var detailParts = new List<string>();
        var itemParts = new List<string>();
        var detailKeys = new HashSet<string>(StringComparer.Ordinal);
        var itemKeys = new HashSet<string>(StringComparer.Ordinal);
        DecodeExtendedDescriptors(e.RawExtendedEventDescriptorHex, detailParts, detailKeys, itemParts, itemKeys);

        return new ProgramGuideCellText(
            title,
            outline,
            JoinRawLines(detailParts),
            JoinRawLines(itemParts),
            "db.raw_descriptor.common_cell_decoder");
    }

    // Raw descriptor columns are ASCII-hex tokens. Parse spans directly instead of Split -> filtered string
    // -> byte[] -> descriptor byte[] so a full DB projection does not allocate several transient objects
    // for every descriptor. Output semantics are unchanged; only ownership of temporary decode buffers changes.
    private static void DecodeShortDescriptors(string? rawHex, ref string title, ref string outline)
    {
        if (string.IsNullOrWhiteSpace(rawHex)) return;

        var raw = rawHex.AsSpan();
        var ordinal = 0;
        var pos = 0;
        Span<byte> descriptor = stackalloc byte[MaxDescriptorBytes];
        while (TryReadNextToken(raw, ref pos, out var token))
        {
            if (TryGetDescriptorMetadata(token, 0x4D, out var totalLength, out _)
                && TryDecodeDescriptor(token, descriptor, totalLength))
            {
                var shortText = DecodeShortEventDescriptor(descriptor[..totalLength]);
                if (title.Length == 0 && shortText.Title.Length > 0) title = shortText.Title;
                if (outline.Length == 0 && shortText.Outline.Length > 0) outline = shortText.Outline;
                if (title.Length > 0 && outline.Length > 0) return;
            }
            ordinal++;
        }
    }

    private static void DecodeExtendedDescriptors(
        string? rawHex,
        List<string> detailParts,
        HashSet<string> detailKeys,
        List<string> itemParts,
        HashSet<string> itemKeys)
    {
        if (string.IsNullOrWhiteSpace(rawHex)) return;

        var raw = rawHex.AsSpan();
        var descriptorCount = CountDescriptors(raw, 0x4E);
        if (descriptorCount == 0) return;

        DescriptorSlice[]? rented = null;
        Span<DescriptorSlice> slices = descriptorCount <= StackDescriptorSliceCapacity
            ? stackalloc DescriptorSlice[descriptorCount]
            : (rented = ArrayPool<DescriptorSlice>.Shared.Rent(descriptorCount)).AsSpan(0, descriptorCount);

        try
        {
            var count = CollectDescriptorSlices(raw, 0x4E, slices);
            slices = slices[..count];
            SortDescriptorSlices(slices);

            Span<byte> descriptor = stackalloc byte[MaxDescriptorBytes];
            foreach (var slice in slices)
            {
                var token = raw.Slice(slice.Start, slice.Length);
                if (!TryGetDescriptorMetadata(token, 0x4E, out var totalLength, out _)
                    || !TryDecodeDescriptor(token, descriptor, totalLength))
                    continue;

                DecodeExtendedEventDescriptor(
                    descriptor[..totalLength],
                    detailParts,
                    detailKeys,
                    itemParts,
                    itemKeys);
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<DescriptorSlice>.Shared.Return(rented, clearArray: false);
        }
    }

    private static int CountDescriptors(ReadOnlySpan<char> raw, byte expectedTag)
    {
        var count = 0;
        var pos = 0;
        while (TryReadNextToken(raw, ref pos, out var token))
        {
            if (TryGetDescriptorMetadata(token, expectedTag, out _, out _)) count++;
        }
        return count;
    }

    private static int CollectDescriptorSlices(ReadOnlySpan<char> raw, byte expectedTag, Span<DescriptorSlice> target)
    {
        var count = 0;
        var ordinal = 0;
        var pos = 0;
        while (pos < raw.Length)
        {
            while (pos < raw.Length && IsDescriptorDelimiter(raw[pos])) pos++;
            if (pos >= raw.Length) break;
            var start = pos;
            while (pos < raw.Length && !IsDescriptorDelimiter(raw[pos])) pos++;
            var token = raw[start..pos];
            if (TryGetDescriptorMetadata(token, expectedTag, out _, out var descriptorNumber))
            {
                target[count++] = new DescriptorSlice(start, token.Length, descriptorNumber, ordinal);
                if (count == target.Length) break;
            }
            ordinal++;
        }
        return count;
    }

    private static void SortDescriptorSlices(Span<DescriptorSlice> slices)
    {
        for (var i = 1; i < slices.Length; i++)
        {
            var current = slices[i];
            var j = i - 1;
            while (j >= 0 && CompareDescriptorSlice(slices[j], current) > 0)
            {
                slices[j + 1] = slices[j];
                j--;
            }
            slices[j + 1] = current;
        }
    }

    private static int CompareDescriptorSlice(DescriptorSlice a, DescriptorSlice b)
    {
        var c = a.DescriptorNumber.CompareTo(b.DescriptorNumber);
        return c != 0 ? c : a.Ordinal.CompareTo(b.Ordinal);
    }

    private static bool TryReadNextToken(ReadOnlySpan<char> raw, ref int pos, out ReadOnlySpan<char> token)
    {
        while (pos < raw.Length && IsDescriptorDelimiter(raw[pos])) pos++;
        if (pos >= raw.Length)
        {
            token = default;
            return false;
        }

        var start = pos;
        while (pos < raw.Length && !IsDescriptorDelimiter(raw[pos])) pos++;
        token = raw[start..pos];
        return true;
    }

    private static bool IsDescriptorDelimiter(char ch)
        => ch is ';' or ',' or '|' or '\r' or '\n' or '\t' or ' ';

    private static bool TryGetDescriptorMetadata(ReadOnlySpan<char> token, byte expectedTag, out int totalLength, out int descriptorNumber)
    {
        totalLength = 0;
        descriptorNumber = 0;

        Span<byte> header = stackalloc byte[3];
        var headerBytes = 0;
        var highNibble = -1;
        var hexDigits = 0;
        foreach (var ch in token)
        {
            var nibble = HexNibble(ch);
            if (nibble < 0) continue;
            hexDigits++;
            if (highNibble < 0)
            {
                highNibble = nibble;
                continue;
            }

            if (headerBytes < header.Length)
                header[headerBytes] = (byte)((highNibble << 4) | nibble);
            headerBytes++;
            highNibble = -1;
        }

        var availableBytes = hexDigits / 2;
        if (headerBytes < 2 || header[0] != expectedTag) return false;
        totalLength = header[1] + 2;
        if (totalLength > MaxDescriptorBytes || totalLength > availableBytes) return false;
        if (expectedTag == 0x4E && headerBytes >= 3)
            descriptorNumber = (header[2] >> 4) & 0x0F;
        return true;
    }

    private static bool TryDecodeDescriptor(ReadOnlySpan<char> token, Span<byte> destination, int totalLength)
    {
        var written = 0;
        var highNibble = -1;
        foreach (var ch in token)
        {
            var nibble = HexNibble(ch);
            if (nibble < 0) continue;
            if (highNibble < 0)
            {
                highNibble = nibble;
                continue;
            }

            if (written < totalLength)
                destination[written] = (byte)((highNibble << 4) | nibble);
            written++;
            highNibble = -1;
            if (written >= totalLength) return true;
        }
        return false;
    }

    private static int HexNibble(char ch)
        => ch is >= '0' and <= '9' ? ch - '0'
            : ch is >= 'A' and <= 'F' ? ch - 'A' + 10
            : ch is >= 'a' and <= 'f' ? ch - 'a' + 10
            : -1;

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

    private static void DecodeExtendedEventDescriptor(
        ReadOnlySpan<byte> descriptor,
        List<string> detailParts,
        HashSet<string> detailKeys,
        List<string> itemParts,
        HashSet<string> itemKeys)
    {
        if (descriptor.Length < 7 || descriptor[0] != 0x4E) return;
        var payloadEnd = Math.Min(descriptor.Length, 2 + descriptor[1]);
        var p = 2;
        if (p >= payloadEnd) return;
        p++; // descriptor_number / last_descriptor_number
        if (p + 3 > payloadEnd) return;
        p += 3; // ISO_639_language_code
        if (p >= payloadEnd) return;

        var itemsLength = descriptor[p++];
        var itemsEnd = Math.Min(payloadEnd, p + itemsLength);
        while (p < itemsEnd)
        {
            var itemDescriptionLength = descriptor[p++];
            if (p + itemDescriptionLength > itemsEnd) break;
            var itemDescription = AribPsiSiDecoder.Decode(descriptor.Slice(p, itemDescriptionLength)).Text;
            p += itemDescriptionLength;

            if (p >= itemsEnd) break;
            var itemTextLength = descriptor[p++];
            if (p + itemTextLength > itemsEnd) break;
            var itemText = AribPsiSiDecoder.Decode(descriptor.Slice(p, itemTextLength)).Text;
            p += itemTextLength;

            if (itemDescription.Length == 0 && itemText.Length == 0) continue;
            var line = FormatDescriptorItem(itemDescription, itemText);
            if (IsItemLikeLabel(itemDescription)) AddUniqueCellTextLine(itemParts, itemKeys, line);
            else AddUniqueCellTextLine(detailParts, detailKeys, line);
        }

        if (p < payloadEnd)
        {
            var textLength = descriptor[p++];
            if (p + textLength <= payloadEnd)
            {
                var detail = AribPsiSiDecoder.Decode(descriptor.Slice(p, textLength)).Text;
                AddUniqueCellTextLine(detailParts, detailKeys, detail);
            }
        }
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
        var source = value.AsSpan().Trim();
        var outputLength = 0;
        var previousWhitespace = false;
        var changed = source.Length != value.Length;
        foreach (var ch in source)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace) outputLength++;
                else changed = true;
                if (ch != ' ') changed = true;
                previousWhitespace = true;
            }
            else
            {
                outputLength++;
                previousWhitespace = false;
            }
        }

        if (!changed) return value;
        return string.Create(outputLength, value, static (dst, state) =>
        {
            var di = 0;
            var inWhitespace = false;
            foreach (var ch in state.AsSpan().Trim())
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!inWhitespace) dst[di++] = ' ';
                    inWhitespace = true;
                }
                else
                {
                    dst[di++] = ch;
                    inWhitespace = false;
                }
            }
        });
    }

    private static string JoinRawLines(List<string> lines)
        => lines.Count switch
        {
            0 => string.Empty,
            1 => lines[0],
            _ => string.Join("\n", lines)
        };

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
        long StartTicks,
        long EndTicks,
        long UpdatedAtTicks,
        byte TableId,
        byte SectionNumber,
        byte VersionNumber)
    {
        public static CacheKey From(EpgEvent e)
            => new(
                e.NetworkId,
                e.TransportStreamId,
                e.ServiceId,
                e.EventId,
                e.Start.Ticks,
                e.End.Ticks,
                e.UpdatedAt.Ticks,
                e.TableId,
                e.SectionNumber,
                e.VersionNumber);
    }

    private readonly record struct DescriptorSlice(int Start, int Length, int DescriptorNumber, int Ordinal);
    private readonly record struct ShortCellText(string Title, string Outline);
}

public sealed record ProgramGuideCellText(
    string Title,
    string Outline,
    string Detail,
    string Items,
    string Source);
