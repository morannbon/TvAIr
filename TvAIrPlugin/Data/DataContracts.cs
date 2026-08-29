using TvAIrPlugin.Runtime;

namespace TvAIrPlugin.Data;

public sealed record TvAirDataSourceDescriptor(string SourceId, string EntityType, string SchemaVersion);

public sealed record TvAirSnapshotOpenRequest(string SourceId, string? KnownRevision = null)
{
    public IReadOnlyList<string> SourceIds { get; init; } = Array.Empty<string>();
}
public sealed record TvAirSnapshotDescriptor(
    string? SnapshotId,
    string SourceId,
    string EntityType,
    string SchemaVersion,
    string Revision,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int ItemCount,
    bool NotModified)
{
    public IReadOnlyList<string> SourceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> SourceRevisions { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> SourceItemCounts { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}
public sealed record TvAirSnapshotItem(string SourceId, object Value);
public sealed record TvAirSnapshotReadRequest(string SnapshotId, int Limit = 500, string? Cursor = null);
public sealed record TvAirSnapshotPage(
    string SnapshotId,
    IReadOnlyList<object> Items,
    string? NextCursor,
    bool HasMore,
    string Revision,
    string SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    int ItemCount)
{
    public IReadOnlyList<string> SourceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> SourceRevisions { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> SourceItemCounts { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}
public sealed record TvAirSnapshotCloseRequest(string SnapshotId);

public interface ITvAirDataApi
{
    IReadOnlyList<TvAirDataSourceDescriptor> ListSources();
    TvAirOperationResult<TvAirSnapshotDescriptor> OpenSnapshot(TvAirSnapshotOpenRequest request);
    TvAirOperationResult<TvAirSnapshotPage> ReadSnapshot(TvAirSnapshotReadRequest request);
    TvAirOperationResult CloseSnapshot(string snapshotId);
}
