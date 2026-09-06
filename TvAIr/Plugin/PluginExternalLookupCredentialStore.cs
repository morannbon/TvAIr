using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TvAIr.Core;

namespace TvAIr.Plugin;

/// <summary>
/// Host-owned credentials for registered ExternalLookup providers.
/// Secrets are DPAPI-protected for the current Windows user and are never exposed to plugins.
/// </summary>
public sealed class PluginExternalLookupCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TvAIr.PluginExternalLookup.v1");
    // The currently registered ExternalLookup providers (TVmaze/Jikan) do not use Host credentials.
    // Keep the store/API boundary for future credentialed providers, but legacy TMDB/NHK values are ignored.
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _encryptedByProvider;

    public PluginExternalLookupCredentialStore(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var dir = Path.Combine(database.DataDirectory, "host-settings");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "plugin-external-lookup-credentials.json");
        _encryptedByProvider = Load(_path);
    }

    public bool Supports(string providerId) => SupportedProviders.Contains(NormalizeProviderId(providerId));

    public bool HasCredential(string providerId)
    {
        var id = NormalizeProviderId(providerId);
        lock (_gate)
            return _encryptedByProvider.TryGetValue(id, out var encrypted) && !string.IsNullOrWhiteSpace(Decrypt(encrypted));
    }

    public string? GetCredential(string providerId)
    {
        var id = NormalizeProviderId(providerId);
        lock (_gate)
        {
            if (!_encryptedByProvider.TryGetValue(id, out var encrypted)) return null;
            return Decrypt(encrypted);
        }
    }

    public bool SetCredential(string providerId, string? credential)
    {
        var id = NormalizeProviderId(providerId);
        if (!SupportedProviders.Contains(id))
            throw new ArgumentException("Provider does not use a Host-managed credential.", nameof(providerId));

        var value = (credential ?? string.Empty).Trim();
        lock (_gate)
        {
            bool changed;
            if (value.Length == 0)
            {
                changed = _encryptedByProvider.Remove(id);
            }
            else
            {
                if (value.Length > 512) throw new ArgumentException("Credential exceeds the Host limit.", nameof(credential));
                var encrypted = Encrypt(value);
                changed = !_encryptedByProvider.TryGetValue(id, out var current) || !string.Equals(current, encrypted, StringComparison.Ordinal);
                _encryptedByProvider[id] = encrypted;
            }

            if (changed) SaveLocked();
            return changed;
        }
    }

    private void SaveLocked()
    {
        var temp = _path + ".tmp";
        var payload = new CredentialFile
        {
            Version = 1,
            Credentials = _encryptedByProvider
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
        };
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }

    private static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return result;
            var payload = JsonSerializer.Deserialize<CredentialFile>(File.ReadAllText(path));
            if (payload?.Version != 1 || payload.Credentials is null) return result;
            foreach (var item in payload.Credentials)
            {
                var id = NormalizeProviderId(item.Key);
                if (SupportedProviders.Contains(id) && !string.IsNullOrWhiteSpace(item.Value))
                    result[id] = item.Value;
            }
        }
        catch
        {
            // Fail closed. Missing/corrupt/unreadable credentials never enable external access.
        }
        return result;
    }

    private static string Encrypt(string plainText)
    {
        var data = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
    }

    private static string? Decrypt(string encryptedBase64)
    {
        try
        {
            var data = Convert.FromBase64String(encryptedBase64);
            var plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProviderId(string? providerId) => (providerId ?? string.Empty).Trim().ToLowerInvariant();

    private sealed class CredentialFile
    {
        public int Version { get; set; }
        public Dictionary<string, string> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class PluginExternalLookupCredentialUpdateDto
{
    public string ProviderId { get; set; } = string.Empty;
    public string? Credential { get; set; }
}
