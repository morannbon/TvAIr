using TvAIr.Core;
using System.Text.Json;

namespace TvAIr.Plugin;

/// <summary>
/// Host-owned second-stage user permission for Plugin ExternalLookup.
/// Missing entries are denied. This remains a simple per-plugin boolean grant;
/// provider/destination safety is enforced separately by the Host gateway.
/// </summary>
public sealed class PluginInternetAccessPermissionStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private HashSet<string> _allowedPluginIds;

    public PluginInternetAccessPermissionStore(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var dir = Path.Combine(database.DataDirectory, "host-settings");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "plugin-internet-access.json");
        _allowedPluginIds = Load(_path);
    }

    public bool IsAllowed(string pluginId)
    {
        var id = PluginIdentity.Normalize(pluginId);
        lock (_gate) return _allowedPluginIds.Contains(id);
    }

    public bool SetAllowed(string pluginId, bool allowed)
    {
        var id = PluginIdentity.Normalize(pluginId);
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("pluginId is required.", nameof(pluginId));
        lock (_gate)
        {
            var changed = allowed ? _allowedPluginIds.Add(id) : _allowedPluginIds.Remove(id);
            if (!changed) return false;
            SaveLocked();
            return true;
        }
    }

    private void SaveLocked()
    {
        var temp = _path + ".tmp";
        var payload = new PermissionFile
        {
            Version = 3,
            AllowedPluginIds = _allowedPluginIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }

    private static HashSet<string> Load(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return result;
            var payload = JsonSerializer.Deserialize<PermissionFile>(File.ReadAllText(path));
            // Older scope-based grants are not silently promoted. One-time fail-closed is safer.
            if (payload?.Version != 3) return result;
            foreach (var pluginId in payload.AllowedPluginIds ?? Array.Empty<string>())
            {
                var id = PluginIdentity.Normalize(pluginId);
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id);
            }
        }
        catch
        {
            // Fail closed. Corrupt/missing permission state never becomes Internet access permission.
        }
        return result;
    }

    private sealed class PermissionFile
    {
        public int Version { get; set; }
        public string[] AllowedPluginIds { get; set; } = Array.Empty<string>();
    }
}

public sealed class PluginInternetAccessUpdateDto
{
    public string PluginId { get; set; } = string.Empty;
    public bool Allowed { get; set; }
}
