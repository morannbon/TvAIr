using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TvAIrPlugin.Assets;

namespace TvAIr.Plugin.RuntimeHost;

/// <summary>
/// Single owner for embedded plugin assets. Public logical paths never depend on assembly resource names.
/// </summary>
internal sealed class PluginAssetCatalog : ITvAirPluginAssetsApi, IDisposable
{
    private readonly Assembly _assembly;
    private readonly IReadOnlyDictionary<string, AssetEntry> _entries;
    private bool _disposed;

    public PluginAssetCatalog(
        string pluginId,
        Assembly assembly,
        IReadOnlyList<PluginAssetDefinition> declaredAssets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        PluginId = pluginId;
        var hostKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PluginId))).ToLowerInvariant()[..20];
        Origin = new Uri($"https://{hostKey}.plugin.tvair.local/assets/");

        var availableResources = assembly.GetManifestResourceNames()
            .ToHashSet(StringComparer.Ordinal);
        var definitions = declaredAssets.Count > 0
            ? declaredAssets
            : DiscoverConventionalAssets(availableResources);

        var entries = new Dictionary<string, AssetEntry>(StringComparer.OrdinalIgnoreCase);
        var entryPointCount = 0;
        foreach (var definition in definitions)
        {
            var logicalPath = NormalizeLogicalPath(definition.LogicalPath);
            if (!availableResources.Contains(definition.ResourceName))
                throw new InvalidOperationException($"Embedded plugin asset resource was not found: {definition.ResourceName}");
            if (entries.ContainsKey(logicalPath))
                throw new InvalidOperationException($"Duplicate plugin asset logical path: {logicalPath}");

            using var stream = assembly.GetManifestResourceStream(definition.ResourceName)
                ?? throw new InvalidOperationException($"Embedded plugin asset resource could not be opened: {definition.ResourceName}");
            var hashBytes = SHA256.HashData(stream);
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            var contentType = string.IsNullOrWhiteSpace(definition.ContentType)
                ? ResolveContentType(logicalPath)
                : definition.ContentType.Trim();
            var assetId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(logicalPath + "\n" + hash))).ToLowerInvariant()[..16];
            var uri = BuildAssetUri(Origin, logicalPath);
            var descriptor = new PluginAssetDescriptor(
                assetId,
                logicalPath,
                contentType,
                stream.Length,
                hash,
                hash,
                definition.CachePolicy,
                definition.IsEntryPoint,
                uri);
            entries.Add(logicalPath, new AssetEntry(definition.ResourceName, descriptor));
            if (definition.IsEntryPoint)
                entryPointCount++;
        }

        if (entryPointCount > 1)
            throw new InvalidOperationException("A plugin asset catalog can declare at most one entry point.");

        _entries = entries;
    }

    public string PluginId { get; }
    public Uri Origin { get; }

    public IReadOnlyList<PluginAssetDescriptor> List(string? prefix = null)
    {
        ThrowIfDisposed();
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix)
            ? null
            : NormalizeLogicalPath(prefix, allowDirectoryPrefix: true);
        return _entries.Values
            .Select(x => x.Descriptor)
            .Where(x => normalizedPrefix is null || x.LogicalPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public PluginAssetDescriptor? Describe(string logicalPath)
    {
        ThrowIfDisposed();
        var normalized = NormalizeLogicalPath(logicalPath);
        return _entries.TryGetValue(normalized, out var entry) ? entry.Descriptor : null;
    }

    public Uri ResolveUri(string logicalPath)
    {
        ThrowIfDisposed();
        var normalized = NormalizeLogicalPath(logicalPath);
        if (!_entries.ContainsKey(normalized))
            throw new FileNotFoundException("Plugin asset was not found.", normalized);
        return BuildAssetUri(Origin, normalized);
    }

    public Stream OpenRead(string logicalPath)
    {
        ThrowIfDisposed();
        var normalized = NormalizeLogicalPath(logicalPath);
        if (!_entries.TryGetValue(normalized, out var entry))
            throw new FileNotFoundException("Plugin asset was not found.", normalized);
        return _assembly.GetManifestResourceStream(entry.ResourceName)
            ?? throw new FileNotFoundException("Embedded plugin asset resource could not be opened.", entry.ResourceName);
    }


    internal string MaterializeRuntimeRoot()
    {
        ThrowIfDisposed();
        var catalogVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", _entries.Values.OrderBy(x => x.Descriptor.LogicalPath, StringComparer.OrdinalIgnoreCase).Select(x => x.Descriptor.LogicalPath + ":" + x.Descriptor.ContentHash))))).ToLowerInvariant()[..20];
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TvAIr", "PluginRuntime", SanitizePathSegment(PluginId), catalogVersion);
        var assetsRoot = Path.Combine(root, "assets");
        Directory.CreateDirectory(assetsRoot);
        foreach (var entry in _entries.Values)
        {
            var relative = entry.Descriptor.LogicalPath.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(assetsRoot, relative);
            var targetFull = Path.GetFullPath(target);
            var assetsFull = Path.GetFullPath(assetsRoot) + Path.DirectorySeparatorChar;
            if (!targetFull.StartsWith(assetsFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Plugin asset target escaped the runtime root.");
            Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
            if (File.Exists(targetFull))
            {
                using var existing = File.OpenRead(targetFull);
                var existingHash = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
                if (string.Equals(existingHash, entry.Descriptor.ContentHash, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var temp = targetFull + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var source = _assembly.GetManifestResourceStream(entry.ResourceName)
                ?? throw new FileNotFoundException("Embedded plugin asset resource could not be opened.", entry.ResourceName))
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(true);
            }
            File.Move(temp, targetFull, true);
        }
        return root;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "plugin" : result;
    }

    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static IReadOnlyList<PluginAssetDefinition> DiscoverConventionalAssets(IEnumerable<string> resourceNames)
    {
        var result = new List<PluginAssetDefinition>();
        foreach (var resourceName in resourceNames.OrderBy(x => x, StringComparer.Ordinal))
        {
            var markerIndex = resourceName.IndexOf(".Assets.", StringComparison.Ordinal);
            var markerLength = ".Assets.".Length;
            if (markerIndex < 0)
            {
                markerIndex = resourceName.IndexOf(".wwwroot.", StringComparison.OrdinalIgnoreCase);
                markerLength = ".wwwroot.".Length;
            }
            if (markerIndex < 0)
                continue;

            var suffix = resourceName[(markerIndex + markerLength)..];
            var logicalPath = ConventionalResourceSuffixToPath(suffix);
            result.Add(new PluginAssetDefinition
            {
                LogicalPath = logicalPath,
                ResourceName = resourceName,
                IsEntryPoint = string.Equals(logicalPath, "index.html", StringComparison.OrdinalIgnoreCase)
            });
        }
        return result;
    }

    private static string ConventionalResourceSuffixToPath(string suffix)
    {
        var lastDot = suffix.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == suffix.Length - 1)
            return suffix.Replace('.', '/');
        var stem = suffix[..lastDot].Replace('.', '/');
        var extension = suffix[lastDot..];
        return stem + extension;
    }

    private static string NormalizeLogicalPath(string path, bool allowDirectoryPrefix = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (!allowDirectoryPrefix)
            normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
            throw new ArgumentException("Plugin asset path must be relative.", nameof(path));
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".." || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("Plugin asset path contains an invalid segment.", nameof(path));
        return string.Join('/', segments) + (allowDirectoryPrefix && normalized.EndsWith('/') ? "/" : string.Empty);
    }

    private static Uri BuildAssetUri(Uri origin, string logicalPath)
    {
        var escaped = string.Join('/', logicalPath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(origin, escaped);
    }

    private static string ResolveContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private sealed record AssetEntry(string ResourceName, PluginAssetDescriptor Descriptor);
}
