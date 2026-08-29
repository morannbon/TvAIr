using TvAIrPlugin.Runtime;
namespace TvAIrPlugin.Storage;
public sealed record PluginStorageEntry(string Namespace, string Key, object? Value, long Revision, DateTimeOffset UpdatedAt);
public sealed record PluginStorageImportResult(string Status, bool Imported, bool SourceFound, bool TargetAlreadyExists, PluginStorageEntry? Entry);
public interface ITvAirPluginStorageApi
{
    TvAirOperationResult<PluginStorageEntry> Get(string @namespace, string key);
    TvAirOperationResult<PluginStorageEntry> Set(string @namespace, string key, object? value, long? expectedRevision = null);
    TvAirOperationResult<PluginStorageImportResult> ImportJsonFileOnce(string @namespace, string key, string legacyFileName);
    TvAirOperationResult Delete(string @namespace, string key, long? expectedRevision = null);
    bool Exists(string @namespace, string key);
    IReadOnlyList<string> ListKeys(string @namespace);
}
