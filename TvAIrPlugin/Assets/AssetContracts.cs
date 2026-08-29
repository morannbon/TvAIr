namespace TvAIrPlugin.Assets;

public enum PluginAssetCachePolicy
{
    NoStore,
    Revalidate,
    Immutable
}

public sealed record PluginAssetDefinition
{
    public required string LogicalPath { get; init; }
    public required string ResourceName { get; init; }
    public string? ContentType { get; init; }
    public PluginAssetCachePolicy CachePolicy { get; init; } = PluginAssetCachePolicy.Revalidate;
    public bool IsEntryPoint { get; init; }
}

public sealed record PluginAssetDescriptor(
    string AssetId,
    string LogicalPath,
    string ContentType,
    long ContentLength,
    string Version,
    string ContentHash,
    PluginAssetCachePolicy CachePolicy,
    bool IsEntryPoint,
    Uri Uri);

public interface ITvAirPluginAssetsApi
{
    Uri Origin { get; }
    IReadOnlyList<PluginAssetDescriptor> List(string? prefix = null);
    PluginAssetDescriptor? Describe(string logicalPath);
    Uri ResolveUri(string logicalPath);
    Stream OpenRead(string logicalPath);
}
