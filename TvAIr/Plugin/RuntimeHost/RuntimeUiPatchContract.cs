namespace TvAIr.Plugin;

using System.Text.RegularExpressions;
using TvAIrPlugin.Runtime;

/// <summary>
/// Action応答とイベント駆動ToolWindow StatePatchが共有する宣言的patch正規化契約。
/// </summary>
internal static class RuntimeUiPatchContract
{
    public static IReadOnlyList<RuntimeUiPatch> Normalize(IReadOnlyList<RuntimeUiPatch>? patches)
    {
        if (patches is null || patches.Count == 0) return Array.Empty<RuntimeUiPatch>();
        var result = new List<RuntimeUiPatch>(Math.Min(patches.Count, 64));
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var patch in patches.Take(64))
        {
            if (patch is null) continue;
            var elementId = (patch.ElementId ?? string.Empty).Trim();
            if (elementId.Length == 0 || elementId.Length > 128) continue;
            if (!Regex.IsMatch(elementId, "^[A-Za-z][A-Za-z0-9_\\-:.]*$")) continue;

            var attrs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (patch.Attributes is not null)
            {
                foreach (var pair in patch.Attributes.Take(32))
                {
                    var name = (pair.Key ?? string.Empty).Trim();
                    if (!(name.Equals("title", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("data-", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_\\-:.]*$")) continue;
                    attrs[name] = pair.Value is null ? null : pair.Value.Length > 1024 ? pair.Value[..1024] : pair.Value;
                }
            }

            var normalized = new RuntimeUiPatch
            {
                ElementId = elementId,
                TextContent = patch.TextContent is null ? null : patch.TextContent.Length > 4096 ? patch.TextContent[..4096] : patch.TextContent,
                ClassName = patch.ClassName is null ? null : patch.ClassName.Length > 1024 ? patch.ClassName[..1024] : patch.ClassName,
                AddClasses = NormalizeClassTokens(patch.AddClasses),
                RemoveClasses = NormalizeClassTokens(patch.RemoveClasses),
                Disabled = patch.Disabled,
                Hidden = patch.Hidden,
                Checked = patch.Checked,
                Value = patch.Value is null ? null : patch.Value.Length > 1024 ? patch.Value[..1024] : patch.Value,
                Attributes = attrs
            };

            if (!indexById.TryGetValue(elementId, out var existingIndex))
            {
                indexById[elementId] = result.Count;
                result.Add(normalized);
                continue;
            }

            var existing = result[existingIndex];
            var mergedAttrs = new Dictionary<string, string?>(existing.Attributes, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in normalized.Attributes) mergedAttrs[pair.Key] = pair.Value;
            result[existingIndex] = new RuntimeUiPatch
            {
                ElementId = elementId,
                TextContent = normalized.TextContent ?? existing.TextContent,
                ClassName = normalized.ClassName ?? existing.ClassName,
                AddClasses = NormalizeClassTokens(existing.AddClasses.Concat(normalized.AddClasses).ToArray()),
                RemoveClasses = NormalizeClassTokens(existing.RemoveClasses.Concat(normalized.RemoveClasses).ToArray()),
                Disabled = normalized.Disabled ?? existing.Disabled,
                Hidden = normalized.Hidden ?? existing.Hidden,
                Checked = normalized.Checked ?? existing.Checked,
                Value = normalized.Value ?? existing.Value,
                Attributes = mergedAttrs
            };
        }
        return result;
    }

    private static IReadOnlyList<string> NormalizeClassTokens(IReadOnlyList<string>? tokens)
    {
        if (tokens is null || tokens.Count == 0) return Array.Empty<string>();
        var result = new List<string>(Math.Min(tokens.Count, 32));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in tokens.Take(32))
        {
            var token = (raw ?? string.Empty).Trim();
            if (token.Length == 0 || token.Length > 128) continue;
            if (!Regex.IsMatch(token, "^[A-Za-z_][A-Za-z0-9_\\-]*$")) continue;
            if (seen.Add(token)) result.Add(token);
        }
        return result;
    }
}
