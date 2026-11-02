using System.Text.Json;

namespace TvAIr.Storage;

// Physical declarations are the authority for a logical recording's output lineage.
// No directory-name guessing or second path journal is maintained.
public static class RecordingOutputPaths
{
    public static IReadOnlyList<string> Read(string plannedPath, string? controlDirectory)
    {
        if (string.IsNullOrWhiteSpace(plannedPath)) return Array.Empty<string>();
        var planned = Path.GetFullPath(plannedPath);
        var parts = new List<(int Index, string Path)>();
        var initialRedirect = false;
        var directory = string.IsNullOrWhiteSpace(controlDirectory) ? null : Path.Combine(controlDirectory, "relay");
        if (directory is not null && Directory.Exists(directory))
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var declaration = doc.RootElement;
            if (!declaration.TryGetProperty("DiskRelaySourcePath", out var source)
                || !string.Equals(source.GetString(), planned, StringComparison.OrdinalIgnoreCase)) continue;
            var path = declaration.GetProperty("OutputPath").GetString()!;
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Relay path is not absolute.");
            var index = declaration.TryGetProperty("DiskRelayPartIndex", out var part) ? part.GetInt32() : 2;
            initialRedirect |= index == 1;
            parts.Add((index, Path.GetFullPath(path)));
        }
        if (!initialRedirect) parts.Add((0, planned));
        return parts.OrderBy(x => x.Index).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
