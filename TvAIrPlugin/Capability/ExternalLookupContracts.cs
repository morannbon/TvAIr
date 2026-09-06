namespace TvAIrPlugin;

/// <summary>
/// Host-managed external lookup. Plugins never receive a raw network client; they can only request
/// operations that the TvAIr Host has registered for a provider.
/// </summary>
public interface ITvAirExternalLookupApi
{
    TvAirExternalLookupCapabilityDto GetCapability();
    Task<TvAirExternalLookupResultDto> LookupAsync(TvAirExternalLookupRequestDto request, CancellationToken cancellationToken = default);
}

public sealed class TvAirExternalLookupCapabilityDto
{
    public bool PluginDeclaredPermission { get; init; }
    public bool UserAllowed { get; init; }
    public bool Available => PluginDeclaredPermission && UserAllowed && Providers.Count > 0;
    public IReadOnlyList<TvAirExternalLookupProviderDto> Providers { get; init; } = Array.Empty<TvAirExternalLookupProviderDto>();
}

public sealed class TvAirExternalLookupProviderDto
{
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> Operations { get; init; } = Array.Empty<string>();
}

public sealed class TvAirExternalLookupRequestDto
{
    public string ProviderId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public sealed class TvAirExternalLookupResultDto
{
    public TvAirExternalLookupResultCode Code { get; init; }
    public bool Success => Code == TvAirExternalLookupResultCode.Success;
    public int? HttpStatusCode { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;
}

public enum TvAirExternalLookupResultCode
{
    Success = 0,
    InvalidRequest,
    PermissionDenied,
    ProviderNotAllowed,
    OperationNotAllowed,
    UnsupportedScheme,
    DestinationRejected,
    RedirectRejected,
    Timeout,
    Cancelled,
    DnsFailure,
    ConnectionFailure,
    TlsFailure,
    ResponseTooLarge,
    InvalidResponse,
    HttpError,
    RateLimited,
    HostShuttingDown,
    PluginStopping
}
