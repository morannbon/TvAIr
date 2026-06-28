using System.Text;

namespace TvAIr.Epg;

internal static class ShortEventDescriptorReader
{
    public static ShortEventDescriptor Read(ReadOnlySpan<byte> section, int descriptorOffset, int descriptorLength)
    {
        var payloadOffset = descriptorOffset + 2;
        var descriptorEnd = payloadOffset + descriptorLength;
        if (descriptorOffset < 0 || payloadOffset < 0 || descriptorLength < 0 || descriptorEnd > section.Length)
        {
            return ShortEventDescriptor.Invalid(descriptorOffset, descriptorLength, "descriptor_range_invalid");
        }

        if (descriptorLength < 4)
        {
            return ShortEventDescriptor.Invalid(descriptorOffset, descriptorLength, "descriptor_too_short");
        }

        var iso = Encoding.ASCII.GetString(section.Slice(payloadOffset, 3));
        var eventNameLengthOffset = payloadOffset + 3;
        var eventNameLength = section[eventNameLengthOffset];
        var eventNameStart = eventNameLengthOffset + 1;
        var eventNameEnd = eventNameStart + eventNameLength;
        if (eventNameEnd > descriptorEnd)
        {
            return ShortEventDescriptor.Invalid(descriptorOffset, descriptorLength, "event_name_declared_length_exceeds_descriptor") with
            {
                Iso639LanguageCode = iso,
                EventNameLength = eventNameLength
            };
        }

        if (eventNameEnd >= descriptorEnd)
        {
            return ShortEventDescriptor.Invalid(descriptorOffset, descriptorLength, "text_length_missing") with
            {
                Iso639LanguageCode = iso,
                EventNameLength = eventNameLength,
                EventNameBytes = section.Slice(eventNameStart, eventNameLength).ToArray()
            };
        }

        var textLength = section[eventNameEnd];
        var textStart = eventNameEnd + 1;
        var textEnd = textStart + textLength;
        if (textEnd > descriptorEnd)
        {
            return ShortEventDescriptor.Invalid(descriptorOffset, descriptorLength, "text_declared_length_exceeds_descriptor") with
            {
                Iso639LanguageCode = iso,
                EventNameLength = eventNameLength,
                EventNameBytes = section.Slice(eventNameStart, eventNameLength).ToArray(),
                TextLength = textLength
            };
        }

        var consumed = textEnd - payloadOffset;
        var status = consumed == descriptorLength ? "arib_fields_read" : "descriptor_consumed_mismatch";
        return new ShortEventDescriptor(
            descriptorOffset,
            descriptorLength,
            status,
            iso,
            eventNameLength,
            section.Slice(eventNameStart, eventNameLength).ToArray(),
            textLength,
            section.Slice(textStart, textLength).ToArray());
    }

    public static string Hex(ReadOnlySpan<byte> bytes, int max)
    {
        var take = Math.Min(bytes.Length, Math.Max(0, max));
        if (take == 0) return string.Empty;
        var sb = new StringBuilder(take * 2 + 3);
        for (var i = 0; i < take; i++) sb.Append(bytes[i].ToString("X2"));
        if (bytes.Length > take) sb.Append("...");
        return sb.ToString();
    }
}

internal sealed record ShortEventDescriptor(
    int DescriptorOffset,
    int DescriptorLength,
    string BoundaryStatus,
    string Iso639LanguageCode,
    int EventNameLength,
    byte[] EventNameBytes,
    int TextLength,
    byte[] TextBytes)
{
    public static ShortEventDescriptor Invalid(int descriptorOffset, int descriptorLength, string status) =>
        new(descriptorOffset, descriptorLength, status, string.Empty, 0, Array.Empty<byte>(), 0, Array.Empty<byte>());
}
