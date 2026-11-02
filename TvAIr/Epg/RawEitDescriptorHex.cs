namespace TvAIr.Epg;

/// <summary>
/// Raw EIT descriptor hex tokens share one normalization/merge rule in the reader
/// and the canonical title/body projection. Preserve first-seen order and
/// ordinal distinctness; later descriptors must not rewrite earlier bytes.
/// </summary>
internal static class RawEitDescriptorHex
{
    internal static string Merge(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)) return NormalizeSequence(right);
        if (string.IsNullOrWhiteSpace(right)) return NormalizeSequence(left);

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddTokens(tokens, seen, left);
        AddTokens(tokens, seen, right);
        return string.Join(";", tokens);
    }

    private static string NormalizeSequence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddTokens(tokens, seen, raw);
        return string.Join(";", tokens);
    }

    private static void AddTokens(List<string> tokens, HashSet<string> seen, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var token in raw.Split(new[] { ';', ',', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = NormalizeToken(token);
            if (normalized.Length == 0) continue;
            if (seen.Add(normalized)) tokens.Add(normalized);
        }
    }

    internal static string NormalizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 2) return string.Empty;
        return (hex.Length & 1) == 1 ? hex[..^1] : hex;
    }
}
