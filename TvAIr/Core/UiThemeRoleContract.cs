using System.Text.RegularExpressions;

namespace TvAIr.Core;

/// <summary>
/// Reads the final Web semantic role values from tvair-theme-contract.css.
/// Web theme roles are the color source of truth; native/plugin projections must not carry a second palette.
/// </summary>
internal static partial class UiThemeRoleContract
{
    private static readonly Lazy<ThemeRoles> Roles = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Get(string theme, string token)
    {
        var normalizedTheme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
        var normalizedToken = token.StartsWith("--", StringComparison.Ordinal) ? token : "--" + token;
        var source = normalizedTheme == "dark" ? Roles.Value.Dark : Roles.Value.Light;
        if (!source.TryGetValue(normalizedToken, out var raw))
            throw new InvalidOperationException($"Theme role token is not defined: theme={normalizedTheme} token={normalizedToken}");
        return Resolve(source, normalizedToken, new HashSet<string>(StringComparer.Ordinal));
    }

    private static string Resolve(IReadOnlyDictionary<string, string> source, string token, HashSet<string> stack)
    {
        if (!source.TryGetValue(token, out var value))
            throw new InvalidOperationException($"Theme role token is not defined: token={token}");
        if (!stack.Add(token))
            throw new InvalidOperationException($"Theme role token cycle detected: token={token}");

        try
        {
            var result = VarReferenceRegex().Replace(value, match =>
            {
                var referenced = match.Groups[1].Value;
                var fallback = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
                if (source.ContainsKey(referenced))
                    return Resolve(source, referenced, stack);
                if (!string.IsNullOrWhiteSpace(fallback))
                    return fallback;
                throw new InvalidOperationException($"Theme role token reference is not defined: token={token} referenced={referenced}");
            });
            return result.Trim();
        }
        finally
        {
            stack.Remove(token);
        }
    }

    private static ThemeRoles Load()
    {
        var path = ResolveContractPath();
        var css = File.ReadAllText(path);
        var light = new Dictionary<string, string>(StringComparer.Ordinal);
        var dark = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match block in CssBlockRegex().Matches(css))
        {
            var selectors = block.Groups[1].Value;
            var body = block.Groups[2].Value;
            var appliesLight = SelectorApplies(selectors, "light");
            var appliesDark = SelectorApplies(selectors, "dark");
            if (!appliesLight && !appliesDark) continue;

            foreach (Match definition in CustomPropertyRegex().Matches(body))
            {
                var name = definition.Groups[1].Value.Trim();
                var value = definition.Groups[2].Value.Trim();
                if (appliesLight) light[name] = value;
                if (appliesDark) dark[name] = value;
            }
        }

        return new ThemeRoles(light, dark);
    }

    private static bool SelectorApplies(string selectorGroup, string theme)
    {
        var normalizedGroup = CssCommentRegex().Replace(selectorGroup, string.Empty);
        foreach (var raw in normalizedGroup.Split(','))
        {
            var selector = raw.Trim();
            if (selector.Length == 0) continue;
            if (string.Equals(selector, ":root", StringComparison.OrdinalIgnoreCase)) return true;

            var mentionsDark = selector.Contains("dark", StringComparison.OrdinalIgnoreCase);
            var mentionsLight = selector.Contains("light", StringComparison.OrdinalIgnoreCase);
            if (theme == "dark" && mentionsDark && !mentionsLight) return true;
            if (theme == "light" && mentionsLight && !mentionsDark) return true;
        }
        return false;
    }

    private static string ResolveContractPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "tvair-theme-contract.css"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "tvair-theme-contract.css"),
            Path.Combine(Directory.GetCurrentDirectory(), "TvAIr", "wwwroot", "tvair-theme-contract.css"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Theme contract source was not found.", "tvair-theme-contract.css");
    }

    private sealed record ThemeRoles(
        IReadOnlyDictionary<string, string> Light,
        IReadOnlyDictionary<string, string> Dark);

    [GeneratedRegex(@"(?s)([^{}]+)\{([^{}]*)\}")]
    private static partial Regex CssBlockRegex();

    [GeneratedRegex(@"(--[A-Za-z0-9_-]+)\s*:\s*([^;]+);")]
    private static partial Regex CustomPropertyRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssCommentRegex();

    [GeneratedRegex(@"var\(\s*(--[A-Za-z0-9_-]+)\s*(?:,\s*([^\)]+))?\)")]
    private static partial Regex VarReferenceRegex();
}
