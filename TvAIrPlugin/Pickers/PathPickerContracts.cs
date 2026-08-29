namespace TvAIrPlugin.Pickers;

public sealed record PluginFileFilter
{
    public required string Label { get; init; }
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();
}

public sealed record PluginFilePickerRequest
{
    public string? Title { get; init; }
    public string? InitialPath { get; init; }
    public IReadOnlyList<PluginFileFilter> Filters { get; init; } = Array.Empty<PluginFileFilter>();
    /// <summary>Runtime ActionのCurrentWindowIdを渡せる汎用owner hint。空でもPickerは利用可能。</summary>
    public string? OwnerWindowId { get; init; }
}

public sealed record PluginFolderPickerRequest
{
    public string? Title { get; init; }
    public string? InitialPath { get; init; }
    /// <summary>Runtime ActionのCurrentWindowIdを渡せる汎用owner hint。空でもPickerは利用可能。</summary>
    public string? OwnerWindowId { get; init; }
}

public sealed record PluginPathPickerResult
{
    public bool Accepted { get; init; }
    public bool Cancelled { get; init; }
    public string? SelectedPath { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static PluginPathPickerResult Accept(string selectedPath)
        => new() { Accepted = true, SelectedPath = selectedPath };

    public static PluginPathPickerResult Cancel()
        => new() { Cancelled = true };

    public static PluginPathPickerResult Fail(string errorCode, string message)
        => new() { ErrorCode = errorCode, Message = message };
}

public interface ITvAirPathPickerApi
{
    Task<PluginPathPickerResult> PickFileAsync(
        PluginFilePickerRequest request,
        CancellationToken cancellationToken = default);

    Task<PluginPathPickerResult> PickFolderAsync(
        PluginFolderPickerRequest request,
        CancellationToken cancellationToken = default);
}
