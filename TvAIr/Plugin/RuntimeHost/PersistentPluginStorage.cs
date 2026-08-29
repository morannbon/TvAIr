using System.Text.Json;
using TvAIr.Core;
using TvAIrPlugin.Runtime;
using TvAIrPlugin.Storage;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class PersistentPluginStorage : ITvAirPluginStorageApi
{
    private sealed class StoredEntry
    {
        public JsonElement Value { get; set; }
        public long Revision { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
    private sealed class StoreDocument
    {
        public int FormatVersion { get; set; } = 1;
        public Dictionary<string, Dictionary<string, StoredEntry>> Namespaces { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _pluginId;
    private readonly string _dataDirectory;
    private readonly string _filePath;
    private readonly LogRepository _log;

    public PersistentPluginStorage(string pluginId, string dataDirectory, LogRepository log)
    {
        _pluginId = pluginId;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _log = log;
        var directory = Path.Combine(dataDirectory, "PluginStorage");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, _pluginId + ".json");
    }

    public TvAirOperationResult<PluginStorageEntry> Get(string @namespace, string key)
    {
        if (!TryValidate(@namespace, key, out var error)) return TvAirOperationResult<PluginStorageEntry>.Fail(TvAirErrorCode.InvalidRequest, error);
        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.Succeeded) return TvAirOperationResult<PluginStorageEntry>.Fail(loaded.Error?.Code ?? TvAirErrorCode.InternalError, loaded.Error?.Message ?? "Storage operation failed.");
            if (!loaded.Value!.Namespaces.TryGetValue(@namespace, out var section) || !section.TryGetValue(key, out var entry))
                return TvAirOperationResult<PluginStorageEntry>.Fail(TvAirErrorCode.EntityNotFound, "Storage value was not found.");
            return TvAirOperationResult<PluginStorageEntry>.Ok(ToPublic(@namespace, key, entry));
        }
    }

    public TvAirOperationResult<PluginStorageEntry> Set(string @namespace, string key, object? value, long? expectedRevision = null)
    {
        if (!TryValidate(@namespace, key, out var error)) return TvAirOperationResult<PluginStorageEntry>.Fail(TvAirErrorCode.InvalidRequest, error);
        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.Succeeded) return TvAirOperationResult<PluginStorageEntry>.Fail(loaded.Error?.Code ?? TvAirErrorCode.InternalError, loaded.Error?.Message ?? "Storage operation failed.");
            var document = loaded.Value!;
            if (!document.Namespaces.TryGetValue(@namespace, out var section)) document.Namespaces[@namespace] = section = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);
            var currentRevision = section.TryGetValue(key, out var current) ? current.Revision : 0;
            if (expectedRevision.HasValue && expectedRevision.Value != currentRevision)
                return TvAirOperationResult<PluginStorageEntry>.Fail(TvAirErrorCode.RevisionConflict, "Storage revision did not match.");
            JsonElement serialized;
            try { serialized = JsonSerializer.SerializeToElement(value, JsonOptions); }
            catch (Exception ex) { return TvAirOperationResult<PluginStorageEntry>.Fail(TvAirErrorCode.InvalidRequest, "Storage value could not be serialized: " + ex.Message); }
            var entry = new StoredEntry { Value = serialized, Revision = checked(currentRevision + 1), UpdatedAt = DateTimeOffset.UtcNow };
            section[key] = entry;
            var saved = Save(document);
            if (!saved.Succeeded) return TvAirOperationResult<PluginStorageEntry>.Fail(saved.Error?.Code ?? TvAirErrorCode.InternalError, saved.Error?.Message ?? "Storage operation failed.");
            return TvAirOperationResult<PluginStorageEntry>.Ok(ToPublic(@namespace, key, entry));
        }
    }


    public TvAirOperationResult<PluginStorageImportResult> ImportJsonFileOnce(string @namespace, string key, string legacyFileName)
    {
        if (!TryValidate(@namespace, key, out var error))
            return TvAirOperationResult<PluginStorageImportResult>.Fail(TvAirErrorCode.InvalidRequest, error);
        if (!TryResolveLegacyJsonFile(legacyFileName, out var legacyPath, out error))
            return TvAirOperationResult<PluginStorageImportResult>.Fail(TvAirErrorCode.InvalidRequest, error);

        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.Succeeded)
                return TvAirOperationResult<PluginStorageImportResult>.Fail(loaded.Error?.Code ?? TvAirErrorCode.InternalError, loaded.Error?.Message ?? "Storage operation failed.");

            var document = loaded.Value!;
            if (document.Namespaces.TryGetValue(@namespace, out var existingSection) && existingSection.TryGetValue(key, out var existing))
            {
                return TvAirOperationResult<PluginStorageImportResult>.Ok(new PluginStorageImportResult(
                    "targetAlreadyExists", false, File.Exists(legacyPath), true, ToPublic(@namespace, key, existing)));
            }

            if (!File.Exists(legacyPath))
            {
                return TvAirOperationResult<PluginStorageImportResult>.Ok(new PluginStorageImportResult(
                    "sourceNotFound", false, false, false, null));
            }

            JsonElement importedValue;
            try
            {
                using var stream = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var json = JsonDocument.Parse(stream);
                importedValue = json.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Add("PLUGIN_STORAGE", _pluginId, $"result=LEGACY_JSON_INVALID file={Path.GetFileName(legacyPath)} error={ex.GetType().Name} rule=plugin_storage_import_once");
                return TvAirOperationResult<PluginStorageImportResult>.Ok(new PluginStorageImportResult(
                    "sourceInvalid", false, true, false, null));
            }

            if (!document.Namespaces.TryGetValue(@namespace, out var section))
                document.Namespaces[@namespace] = section = new Dictionary<string, StoredEntry>(StringComparer.Ordinal);

            var entry = new StoredEntry
            {
                Value = importedValue,
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            section[key] = entry;

            var saved = Save(document);
            if (!saved.Succeeded)
                return TvAirOperationResult<PluginStorageImportResult>.Fail(saved.Error?.Code ?? TvAirErrorCode.InternalError, saved.Error?.Message ?? "Storage operation failed.");

            _log.Add("PLUGIN_STORAGE", _pluginId, $"result=LEGACY_JSON_IMPORTED file={Path.GetFileName(legacyPath)} namespace={@namespace} key={key} rule=plugin_storage_import_once");
            return TvAirOperationResult<PluginStorageImportResult>.Ok(new PluginStorageImportResult(
                "imported", true, true, false, ToPublic(@namespace, key, entry)));
        }
    }


    public TvAirOperationResult Delete(string @namespace, string key, long? expectedRevision = null)
    {
        if (!TryValidate(@namespace, key, out var error)) return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, error);
        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.Succeeded) return TvAirOperationResult.Fail(loaded.Error?.Code ?? TvAirErrorCode.InternalError, loaded.Error?.Message ?? "Storage operation failed.");
            var document = loaded.Value!;
            if (!document.Namespaces.TryGetValue(@namespace, out var section) || !section.TryGetValue(key, out var current))
                return TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Storage value was not found.");
            if (expectedRevision.HasValue && expectedRevision.Value != current.Revision)
                return TvAirOperationResult.Fail(TvAirErrorCode.RevisionConflict, "Storage revision did not match.");
            section.Remove(key);
            if (section.Count == 0) document.Namespaces.Remove(@namespace);
            return Save(document);
        }
    }

    public bool Exists(string @namespace, string key) => Get(@namespace, key).Succeeded;

    public IReadOnlyList<string> ListKeys(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace)) return Array.Empty<string>();
        lock (_gate)
        {
            var loaded = Load();
            if (!loaded.Succeeded || !loaded.Value!.Namespaces.TryGetValue(@namespace, out var section)) return Array.Empty<string>();
            return section.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
    }

    private TvAirOperationResult<StoreDocument> Load()
    {
        if (!File.Exists(_filePath)) return TvAirOperationResult<StoreDocument>.Ok(new StoreDocument());
        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<StoreDocument>(stream, JsonOptions);
            if (document is null || document.FormatVersion != 1)
                return TvAirOperationResult<StoreDocument>.Fail(TvAirErrorCode.InternalError, "Plugin storage format is invalid.");
            document.Namespaces ??= new Dictionary<string, Dictionary<string, StoredEntry>>(StringComparer.Ordinal);
            return TvAirOperationResult<StoreDocument>.Ok(document);
        }
        catch (Exception ex)
        {
            _log.Add("PLUGIN_STORAGE", _pluginId, $"result=CORRUPT_READ_BLOCKED file={Path.GetFileName(_filePath)} error={ex.GetType().Name} rule=plugin_storage_persistence_contract");
            return TvAirOperationResult<StoreDocument>.Fail(TvAirErrorCode.InternalError, "Plugin storage is unreadable; no data was overwritten.");
        }
    }

    private TvAirOperationResult Save(StoreDocument document)
    {
        var temp = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(_filePath))
            {
                try
                {
                    File.Replace(temp, _filePath, null, true);
                }
                catch (IOException)
                {
                    // File.Replace can fail on valid Windows file systems or when the destination
                    // was created without replacement metadata. A same-directory overwrite move
                    // still keeps the prepared file on the same volume and replaces only after the
                    // JSON has been fully flushed.
                    File.Move(temp, _filePath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temp, _filePath, true);
                }
            }
            else
            {
                File.Move(temp, _filePath);
            }
            return TvAirOperationResult.Ok();
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            var detail = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.Add("PLUGIN_STORAGE", _pluginId, $"result=WRITE_FAILED file={Path.GetFileName(_filePath)} error={ex.GetType().Name} message={detail} rule=plugin_storage_persistence_contract");
            return TvAirOperationResult.Fail(TvAirErrorCode.InternalError, "Plugin storage could not be written atomically.");
        }
    }

    private static PluginStorageEntry ToPublic(string ns, string key, StoredEntry entry)
        => new(ns, key, entry.Value.Clone(), entry.Revision, entry.UpdatedAt);



    private bool TryResolveLegacyJsonFile(string legacyFileName, out string path, out string error)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(legacyFileName) || legacyFileName.Length > 255)
        {
            error = "Legacy JSON file name is required and must be 255 characters or fewer.";
            return false;
        }
        if (Path.IsPathRooted(legacyFileName) || !string.Equals(Path.GetFileName(legacyFileName), legacyFileName, StringComparison.Ordinal) ||
            legacyFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !string.Equals(Path.GetExtension(legacyFileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            error = "Legacy JSON source must be a JSON file name directly under the TvAIr data directory.";
            return false;
        }
        path = Path.GetFullPath(Path.Combine(_dataDirectory, legacyFileName));
        var root = _dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
            error = "Legacy JSON source is outside the TvAIr data directory.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool TryValidate(string ns, string key, out string error)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.Length > 128) { error = "Storage namespace is required and must be 128 characters or fewer."; return false; }
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256) { error = "Storage key is required and must be 256 characters or fewer."; return false; }
        error = string.Empty; return true;
    }

}
