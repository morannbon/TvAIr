using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using TvAIr.Core;
using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// The only Host-side Internet execution boundary exposed to plugins.
/// Providers are registered by TvAIr code, not by plugins. All provider traffic is forced through
/// IPluginExternalLookupGateway so the capability can be isolated at one code boundary.
/// </summary>
public sealed class PluginManagedExternalLookupHost : IDisposable
{
    private const string TvMazeProviderId = "tvmaze";
    private const string TvMazeApiHost = "api.tvmaze.com";
    private const string TvMazeWebsiteUrl = "https://www.tvmaze.com/";
    private const string JikanProviderId = "jikan";
    private const string JikanApiHost = "api.jikan.moe";
    private const string JikanWebsiteUrl = "https://jikan.moe/";

    private const int GlobalConcurrency = 4;
    private const int PerPluginConcurrency = 2;
    private const int RequestsPerMinute = 30;
    private const int MaxRedirects = 3;
    private const int DefaultMaxResponseBytes = 1_048_576;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly LogRepository _log;
    private readonly PluginInternetAccessPermissionStore _permissionStore;
    private readonly PluginExternalLookupCredentialStore _credentialStore;
    private readonly IPluginExternalLookupGateway _gateway;
    private readonly IReadOnlyDictionary<string, ProviderDefinition> _providers;
    private readonly SemaphoreSlim _globalGate = new(GlobalConcurrency, GlobalConcurrency);
    private readonly ConcurrentDictionary<string, PluginState> _pluginStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public PluginManagedExternalLookupHost(LogRepository log, PluginInternetAccessPermissionStore permissionStore, PluginExternalLookupCredentialStore credentialStore, IPluginExternalLookupGateway gateway)
    {
        _log = log;
        _permissionStore = permissionStore;
        _credentialStore = credentialStore;
        _gateway = gateway;
        _providers = BuildProviderCatalog();
    }

    public bool GatewayEnabled => _gateway.Enabled;

    public IReadOnlyList<TvAirExternalLookupProviderDto> GetProviders()
    {
        if (!_gateway.Enabled) return Array.Empty<TvAirExternalLookupProviderDto>();
        return _providers.Values
            .Where(IsRuntimeAvailable)
            .Select(x => new TvAirExternalLookupProviderDto
            {
                ProviderId = x.ProviderId,
                DisplayName = x.DisplayName,
                Operations = x.Operations.Keys.OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .OrderBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool IsRuntimeAvailable(ProviderDefinition provider)
        => provider.RuntimeEnabled
           && (!provider.RequiresCredential || _credentialStore.HasCredential(provider.ProviderId));

    public bool IsUserAllowed(string pluginId) => _permissionStore.IsAllowed(pluginId);

    public bool IsPluginAccessEffective(string pluginId, bool descriptorPermission)
        => _gateway.Enabled
           && descriptorPermission
           && _permissionStore.IsAllowed(PluginIdentity.Normalize(pluginId))
           && _providers.Values.Any(IsRuntimeAvailable);

    public IReadOnlyList<PluginExternalLookupProviderHostStatusDto> GetProviderHostStatus()
    {
        return _providers.Values
            .OrderBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(x => new PluginExternalLookupProviderHostStatusDto
            {
                ProviderId = x.ProviderId,
                DisplayName = x.DisplayName,
                WebsiteUrl = x.WebsiteUrl,
                RequiresCredential = x.RequiresCredential,
                CredentialConfigured = !x.RequiresCredential || _credentialStore.HasCredential(x.ProviderId),
                RuntimeEnabled = x.RuntimeEnabled,
                UnavailableReason = x.RuntimeEnabled ? string.Empty : x.UnavailableReason
            })
            .ToArray();
    }

    public async Task<TvAirExternalLookupResultDto> LookupAsync(
        string pluginId,
        string pluginDisplayName,
        bool descriptorPermission,
        TvAirExternalLookupRequestDto? request,
        CancellationToken pluginCancellationToken)
    {
        var id = PluginIdentity.Normalize(pluginId);
        var providerId = (request?.ProviderId ?? string.Empty).Trim();
        var operation = (request?.Operation ?? string.Empty).Trim();

        if (Volatile.Read(ref _disposed) != 0 || _shutdown.IsCancellationRequested)
            return Result(TvAirExternalLookupResultCode.HostShuttingDown, providerId, operation, "TvAIr is shutting down.");
        if (!_gateway.Enabled)
            return Result(TvAirExternalLookupResultCode.ProviderNotAllowed, providerId, operation, "Network usage is disabled by TvAIr settings.");
        if (!descriptorPermission)
            return Result(TvAirExternalLookupResultCode.PermissionDenied, providerId, operation, "UseExternalLookup permission is required.");
        if (!_permissionStore.IsAllowed(id))
            return Result(TvAirExternalLookupResultCode.PermissionDenied, providerId, operation, "Internet access is disabled for this plugin in TvAIr settings.");
        if (request is null || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(operation))
            return Result(TvAirExternalLookupResultCode.InvalidRequest, providerId, operation, "ProviderId and Operation are required.");
        if (request.Parameters is { Count: > 32 }
            || (request.Parameters?.Any(x => x.Key.Length > 64 || x.Value.Length > 512) ?? false))
            return Result(TvAirExternalLookupResultCode.InvalidRequest, providerId, operation, "Lookup parameters exceed the Host input limit.");
        if (!_providers.TryGetValue(providerId, out var provider))
            return Result(TvAirExternalLookupResultCode.ProviderNotAllowed, providerId, operation, "Provider is not registered by TvAIr Host.");
        if (!provider.Operations.TryGetValue(operation, out var operationDefinition))
            return Result(TvAirExternalLookupResultCode.OperationNotAllowed, providerId, operation, "Operation is not registered for this provider.");

        var parameters = request.Parameters ?? new Dictionary<string, string>();
        var validation = operationDefinition.ValidateParameters(parameters);
        if (validation is not null)
            return Result(TvAirExternalLookupResultCode.InvalidRequest, providerId, operation, validation);

        var credential = provider.RequiresCredential ? _credentialStore.GetCredential(provider.ProviderId) : null;
        if (provider.RequiresCredential && string.IsNullOrWhiteSpace(credential))
            return Result(TvAirExternalLookupResultCode.ProviderNotAllowed, providerId, operation, "Host credential is not configured for this provider.");
        if (!provider.RuntimeEnabled)
            return Result(TvAirExternalLookupResultCode.ProviderNotAllowed, providerId, operation, provider.UnavailableReason);

        ProviderRequestPlan requestPlan;
        try { requestPlan = operationDefinition.BuildRequest(parameters, credential); }
        catch (Exception ex) { return Result(TvAirExternalLookupResultCode.InvalidRequest, providerId, operation, ex.Message); }

        var uri = requestPlan.Uri;
        var destinationCheck = ValidateProviderUri(provider, uri);
        if (destinationCheck is not null) return Result(destinationCheck.Value.Code, providerId, operation, destinationCheck.Value.Message);

        var state = _pluginStates.GetOrAdd(id, _ => new PluginState());
        if (!state.TryConsumeRateSlot(DateTimeOffset.UtcNow, RequestsPerMinute))
            return Result(TvAirExternalLookupResultCode.RateLimited, providerId, operation, "Plugin request rate limit exceeded.");

        using var timeoutCts = new CancellationTokenSource(operationDefinition.Timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pluginCancellationToken, timeoutCts.Token, state.Stop.Token, _shutdown.Token);
        var enteredPlugin = false;
        var enteredGlobal = false;
        var current = uri;
        try
        {
            await state.Concurrency.WaitAsync(linked.Token).ConfigureAwait(false);
            enteredPlugin = true;
            await _globalGate.WaitAsync(linked.Token).ConfigureAwait(false);
            enteredGlobal = true;

            for (var redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                var response = await _gateway.SendAsync(new PluginExternalLookupGatewayRequest
                {
                    Uri = current,
                    Method = operationDefinition.Method,
                    BearerToken = requestPlan.BearerToken
                }, linked.Token).ConfigureAwait(false);
                using (response)
                {
                    if (IsRedirect(response.StatusCode))
                    {
                        if (redirect >= MaxRedirects)
                            return Result(TvAirExternalLookupResultCode.RedirectRejected, providerId, operation, "Redirect limit exceeded.");
                        if (response.Headers.Location is null)
                            return Result(TvAirExternalLookupResultCode.InvalidResponse, providerId, operation, "Redirect response has no Location header.");
                        var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                        var redirected = ValidateProviderUri(provider, next);
                        if (redirected is not null)
                            return Result(TvAirExternalLookupResultCode.RedirectRejected, providerId, operation, redirected.Value.Message);
                        current = next;
                        continue;
                    }

                    var status = (int)response.StatusCode;
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (operationDefinition.Method == HttpMethod.Head)
                    {
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.RateLimited, status, mediaType, responseBytes: 0, bodyPresent: false);
                            return Result(TvAirExternalLookupResultCode.RateLimited, providerId, operation, "Provider rate limit exceeded.", status, mediaType);
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.HttpError, status, mediaType, responseBytes: 0, bodyPresent: false);
                            return Result(TvAirExternalLookupResultCode.HttpError, providerId, operation, $"HTTP status {status}.", status, mediaType);
                        }
                        return new TvAirExternalLookupResultDto
                        {
                            Code = TvAirExternalLookupResultCode.Success,
                            HttpStatusCode = status,
                            ContentType = mediaType,
                            Body = string.Empty,
                            ProviderId = providerId,
                            Operation = operation,
                            CompletedAt = DateTimeOffset.Now
                        };
                    }
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.RateLimited, status, mediaType, responseBytes: 0, bodyPresent: response.Content.Headers.ContentLength is > 0);
                        return Result(TvAirExternalLookupResultCode.RateLimited, providerId, operation, "Provider rate limit exceeded.", status, mediaType);
                    }
                    if (!operationDefinition.AllowedContentTypes.Contains(mediaType))
                    {
                        LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.InvalidResponse, status, mediaType, responseBytes: response.Content.Headers.ContentLength is long declaredLength && declaredLength <= int.MaxValue ? (int)declaredLength : -1, bodyPresent: response.Content.Headers.ContentLength is > 0);
                        return Result(TvAirExternalLookupResultCode.InvalidResponse, providerId, operation, "Response Content-Type is not allowed.", status, mediaType);
                    }
                    var bodyResult = await ReadBoundedBodyAsync(response, operationDefinition.MaxResponseBytes, linked.Token).ConfigureAwait(false);
                    if (!bodyResult.Success)
                    {
                        LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, bodyResult.Code, status, mediaType, responseBytes: -1, bodyPresent: response.Content.Headers.ContentLength is > 0);
                        return Result(bodyResult.Code, providerId, operation, bodyResult.Message, status, mediaType);
                    }
                    var responseBytes = Encoding.UTF8.GetByteCount(bodyResult.Body);
                    var payloadValidation = ValidatePayload(mediaType, bodyResult.Body);
                    if (payloadValidation is not null)
                    {
                        LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.InvalidResponse, status, mediaType, responseBytes, bodyPresent: responseBytes > 0);
                        return Result(TvAirExternalLookupResultCode.InvalidResponse, providerId, operation, payloadValidation, status, mediaType);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        LogHttpFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.HttpError, status, mediaType, responseBytes, bodyPresent: responseBytes > 0);
                        return Result(TvAirExternalLookupResultCode.HttpError, providerId, operation, $"HTTP status {status}.", status, mediaType);
                    }

                    _log.Add("PLUGIN_EXTERNAL_LOOKUP", pluginDisplayName,
                        $"result=OK pluginId={Safe(id)} provider={Safe(providerId)} operation={Safe(operation)} status={status} bytes={responseBytes} destination={Safe(provider.DisplayName)} rule=plugin_managed_external_lookup_contract");
                    return new TvAirExternalLookupResultDto
                    {
                        Code = TvAirExternalLookupResultCode.Success,
                        HttpStatusCode = status,
                        ContentType = mediaType,
                        Body = bodyResult.Body,
                        ProviderId = providerId,
                        Operation = operation,
                        CompletedAt = DateTimeOffset.Now
                    };
                }
            }
            return Result(TvAirExternalLookupResultCode.RedirectRejected, providerId, operation, "Redirect rejected.");
        }
        catch (OperationCanceledException ex)
        {
            if (_shutdown.IsCancellationRequested)
            {
                LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.HostShuttingDown, ex, timeout: false);
                return Result(TvAirExternalLookupResultCode.HostShuttingDown, providerId, operation, "TvAIr is shutting down.");
            }
            if (state.Stop.IsCancellationRequested)
            {
                LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.PluginStopping, ex, timeout: false);
                return Result(TvAirExternalLookupResultCode.PluginStopping, providerId, operation, "Plugin communication was stopped.");
            }
            if (timeoutCts.IsCancellationRequested)
            {
                LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.Timeout, ex, timeout: true);
                return Result(TvAirExternalLookupResultCode.Timeout, providerId, operation, "External lookup timed out.");
            }
            LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.Cancelled, ex, timeout: false);
            return Result(TvAirExternalLookupResultCode.Cancelled, providerId, operation, "External lookup was cancelled.");
        }
        catch (AuthenticationException ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.TlsFailure, ex, timeout: false); return Result(TvAirExternalLookupResultCode.TlsFailure, providerId, operation, ex.Message); }
        catch (PluginExternalLookupGatewayDisabledException ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.ProviderNotAllowed, ex, timeout: false); return Result(TvAirExternalLookupResultCode.ProviderNotAllowed, providerId, operation, ex.Message); }
        catch (PluginExternalLookupDestinationRejectedException ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.DestinationRejected, ex, timeout: false); return Result(TvAirExternalLookupResultCode.DestinationRejected, providerId, operation, ex.Message); }
        catch (DestinationRejectedException ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.DestinationRejected, ex, timeout: false); return Result(TvAirExternalLookupResultCode.DestinationRejected, providerId, operation, ex.Message); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.TryAgain)
        { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.DnsFailure, ex, timeout: false); return Result(TvAirExternalLookupResultCode.DnsFailure, providerId, operation, ex.Message); }
        catch (HttpRequestException ex) when (ex.InnerException is DestinationRejectedException || ex.InnerException is PluginExternalLookupDestinationRejectedException || ex.Message.Contains("prohibited IP", StringComparison.OrdinalIgnoreCase))
        { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.DestinationRejected, ex, timeout: false); return Result(TvAirExternalLookupResultCode.DestinationRejected, providerId, operation, ex.Message); }
        catch (HttpRequestException ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.ConnectionFailure, ex, timeout: false); return Result(TvAirExternalLookupResultCode.ConnectionFailure, providerId, operation, ex.Message); }
        catch (SocketException ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.ConnectionFailure, ex, timeout: false); return Result(TvAirExternalLookupResultCode.ConnectionFailure, providerId, operation, ex.Message); }
        catch (Exception ex) { LogExceptionFailure(pluginDisplayName, id, provider, providerId, operation, operationDefinition.Method, current, TvAirExternalLookupResultCode.InvalidResponse, ex, timeout: false); return Result(TvAirExternalLookupResultCode.InvalidResponse, providerId, operation, ex.Message); }
        finally
        {
            if (enteredGlobal) _globalGate.Release();
            if (enteredPlugin) state.Concurrency.Release();
        }
    }

    public void OnPermissionChanged(string pluginId, bool allowed)
    {
        if (allowed) return;
        CancelPlugin(pluginId);
    }

    public void CancelPlugin(string pluginId)
    {
        var id = PluginIdentity.Normalize(pluginId);
        if (_pluginStates.TryRemove(id, out var state)) state.Dispose();
    }


    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    private void LogHttpFailure(
        string pluginDisplayName,
        string pluginId,
        ProviderDefinition provider,
        string providerId,
        string operation,
        HttpMethod method,
        Uri uri,
        TvAirExternalLookupResultCode code,
        int status,
        string contentType,
        int responseBytes,
        bool bodyPresent)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        _log.Add("PLUGIN_EXTERNAL_LOOKUP", pluginDisplayName,
            $"result=ERROR pluginId={Safe(pluginId)} provider={Safe(providerId)} operation={Safe(operation)} code={code} method={Safe(method.Method)} host={Safe(uri.IdnHost)} path={Safe(uri.AbsolutePath)} status={status} contentType={Safe(contentType)} bytes={responseBytes} bodyPresent={bodyPresent} timeout=False exception=- destination={Safe(provider.DisplayName)} rule=plugin_managed_external_lookup_contract");
#endif
    }

    [System.Diagnostics.Conditional("TVAIR_DEVELOPER_DIAGNOSTICS")]
    private void LogExceptionFailure(
        string pluginDisplayName,
        string pluginId,
        ProviderDefinition provider,
        string providerId,
        string operation,
        HttpMethod method,
        Uri uri,
        TvAirExternalLookupResultCode code,
        Exception exception,
        bool timeout)
    {
#if TVAIR_DEVELOPER_DIAGNOSTICS
        _log.Add("PLUGIN_EXTERNAL_LOOKUP", pluginDisplayName,
            $"result=ERROR pluginId={Safe(pluginId)} provider={Safe(providerId)} operation={Safe(operation)} code={code} method={Safe(method.Method)} host={Safe(uri.IdnHost)} path={Safe(uri.AbsolutePath)} status=- contentType=- bytes=-1 bodyPresent=False timeout={timeout} exception={Safe(exception.GetType().Name)} destination={Safe(provider.DisplayName)} rule=plugin_managed_external_lookup_contract");
#endif
    }

    private static async Task<BodyReadResult> ReadBoundedBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            return BodyReadResult.Fail(TvAirExternalLookupResultCode.ResponseTooLarge, "Response exceeds the configured size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) return BodyReadResult.Fail(TvAirExternalLookupResultCode.ResponseTooLarge, "Response exceeds the configured size limit.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return BodyReadResult.Ok(utf8.GetString(memory.ToArray()));
        }
        catch (DecoderFallbackException)
        {
            return BodyReadResult.Fail(TvAirExternalLookupResultCode.InvalidResponse, "Response is not valid UTF-8.");
        }
    }

    private static string? ValidatePayload(string mediaType, string body)
    {
        if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            && !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body, new System.Text.Json.JsonDocumentOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = System.Text.Json.JsonCommentHandling.Disallow
            });
            var stack = new Stack<System.Text.Json.JsonElement>();
            stack.Push(document.RootElement);
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (element.ValueKind == System.Text.Json.JsonValueKind.String && (element.GetString()?.Length ?? 0) > 65536)
                    return "JSON string exceeds the Host limit.";
                if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var property in element.EnumerateObject()) { if (property.Name.Length > 256) return "JSON property name exceeds the Host limit."; stack.Push(property.Value); }
                else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in element.EnumerateArray()) stack.Push(item);
            }
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return "Response JSON is invalid or exceeds the allowed nesting depth.";
        }
    }

    private static (TvAirExternalLookupResultCode Code, string Message)? ValidateProviderUri(ProviderDefinition provider, Uri uri)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return (TvAirExternalLookupResultCode.UnsupportedScheme, "Only HTTPS destinations are allowed.");
        if (!provider.AllowedHosts.Contains(uri.IdnHost))
            return (TvAirExternalLookupResultCode.DestinationRejected, "Destination host is outside the provider allowlist.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (TvAirExternalLookupResultCode.DestinationRejected, "URI user information is not allowed.");
        if (uri.Port != 443)
            return (TvAirExternalLookupResultCode.DestinationRejected, "Only HTTPS port 443 is allowed in the initial capability.");
        return null;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static TvAirExternalLookupResultDto Result(TvAirExternalLookupResultCode code, string providerId, string operation, string message, int? status = null, string contentType = "")
        => new() { Code = code, ProviderId = providerId, Operation = operation, Message = message, HttpStatusCode = status, ContentType = contentType, CompletedAt = DateTimeOffset.Now };

    private static IReadOnlyDictionary<string, ProviderDefinition> BuildProviderCatalog()
    {
        // EXTERNAL_LOOKUP_PROVIDER_SINGLE_SOURCE_CONTRACT:
        // Provider registration is Host-owned. Plugins can choose only ProviderId/Operation and bounded parameters.
        // Domains, concrete URLs, methods, credentials and security policy never come from plugin input.
        return new Dictionary<string, ProviderDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [TvMazeProviderId] = new ProviderDefinition
            {
                ProviderId = TvMazeProviderId,
                DisplayName = "TVmaze",
                WebsiteUrl = TvMazeWebsiteUrl,
                AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TvMazeApiHost },
                RequiresCredential = false,
                RuntimeEnabled = true,
                Operations = new Dictionary<string, OperationDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SearchShow"] = new OperationDefinition
                    {
                        MaxResponseBytes = 512 * 1024,
                        ValidateParameters = p => RequireOnly(p, new[] { "query" }, new[] { "query" }, ValidateSearchQuery),
                        BuildRequest = (p, _) => new ProviderRequestPlan(HttpsUri(TvMazeApiHost, "/search/shows", new Dictionary<string, string> { ["q"] = p["query"].Trim() }))
                    },
                    ["GetEpisodes"] = new OperationDefinition
                    {
                        MaxResponseBytes = 768 * 1024,
                        ValidateParameters = p => RequirePositiveIdOnly(p, "showId"),
                        BuildRequest = (p, _) =>
                        {
                            var showId = p["showId"].Trim();
                            return new ProviderRequestPlan(HttpsUri(TvMazeApiHost, $"/shows/{showId}/episodes"));
                        }
                    },
                    ["GetAliases"] = new OperationDefinition
                    {
                        MaxResponseBytes = 256 * 1024,
                        ValidateParameters = p => RequirePositiveIdOnly(p, "showId"),
                        BuildRequest = (p, _) =>
                        {
                            var showId = p["showId"].Trim();
                            return new ProviderRequestPlan(HttpsUri(TvMazeApiHost, $"/shows/{showId}/akas"));
                        }
                    }
                }
            },
            [JikanProviderId] = new ProviderDefinition
            {
                ProviderId = JikanProviderId,
                DisplayName = "Jikan",
                WebsiteUrl = JikanWebsiteUrl,
                AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { JikanApiHost },
                RequiresCredential = false,
                RuntimeEnabled = true,
                Operations = new Dictionary<string, OperationDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SearchAnime"] = new OperationDefinition
                    {
                        MaxResponseBytes = 512 * 1024,
                        ValidateParameters = p => RequireOnly(p, new[] { "query" }, new[] { "query" }, ValidateSearchQuery),
                        BuildRequest = (p, _) => new ProviderRequestPlan(
                            HttpsUri(JikanApiHost, "/v4/anime", new Dictionary<string, string>
                            {
                                ["q"] = p["query"].Trim(),
                                ["limit"] = "8"
                            }))
                    }
                }
            }
        };
    }

    private static Uri HttpsUri(string host, string path, IReadOnlyDictionary<string, string>? query = null)
    {
        var builder = new UriBuilder(Uri.UriSchemeHttps, host, 443, path);
        if (query is { Count: > 0 })
            builder.Query = string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return builder.Uri;
    }

    private static string? RequireOnly(IReadOnlyDictionary<string, string> p, IReadOnlyCollection<string> required, IReadOnlyCollection<string> allowed, Func<string, string?>? valueValidator = null)
    {
        foreach (var key in p.Keys)
            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase)) return $"Parameter '{key}' is not allowed.";
        foreach (var key in required)
            if (!p.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return $"Parameter '{key}' is required.";
        if (valueValidator is not null)
            foreach (var key in required)
            {
                var error = valueValidator(p[key]);
                if (error is not null) return error;
            }
        return null;
    }

    private static string? ValidateSearchQuery(string value)
    {
        var length = value.Trim().Length;
        return length is < 1 or > 160 ? "Search query must be 1-160 characters." : null;
    }

    private static string? RequirePositiveIdOnly(IReadOnlyDictionary<string, string> p, string key)
    {
        var basic = RequireOnly(p, new[] { key }, new[] { key });
        if (basic is not null) return basic;
        return int.TryParse(p[key], out var id) && id > 0 ? null : $"Parameter '{key}' must be a positive integer.";
    }

    private static string Safe(string? value)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        foreach (var item in _pluginStates) item.Value.Dispose();
        _pluginStates.Clear();
        // _globalGate/_shutdown are intentionally left for GC because host disposal can race the final finally block of an in-flight request.
    }

    internal sealed class ProviderDefinition
    {
        public required string ProviderId { get; init; }
        public required string DisplayName { get; init; }
        public required string WebsiteUrl { get; init; }
        public required HashSet<string> AllowedHosts { get; init; }
        public bool RequiresCredential { get; init; }
        public bool RuntimeEnabled { get; init; } = true;
        public string UnavailableReason { get; init; } = "Provider is not currently available.";
        public required IReadOnlyDictionary<string, OperationDefinition> Operations { get; init; }
    }

    internal sealed class OperationDefinition
    {
        public HttpMethod Method { get; init; } = HttpMethod.Get;
        public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;
        public TimeSpan? Timeout { get; init; }
        public HashSet<string> AllowedContentTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "application/json" };
        public required Func<IReadOnlyDictionary<string, string>, string?, ProviderRequestPlan> BuildRequest { get; init; }
        public Func<IReadOnlyDictionary<string, string>, string?> ValidateParameters { get; init; } = _ => null;
    }

    internal readonly record struct ProviderRequestPlan(Uri Uri, string? BearerToken = null);

    private sealed class PluginState : IDisposable
    {
        private readonly object _rateGate = new();
        private readonly Queue<DateTimeOffset> _recent = new();
        public SemaphoreSlim Concurrency { get; } = new(PerPluginConcurrency, PerPluginConcurrency);
        public CancellationTokenSource Stop { get; } = new();

        public bool TryConsumeRateSlot(DateTimeOffset now, int limit)
        {
            lock (_rateGate)
            {
                var cutoff = now.AddMinutes(-1);
                while (_recent.Count > 0 && _recent.Peek() < cutoff) _recent.Dequeue();
                if (_recent.Count >= limit) return false;
                _recent.Enqueue(now);
                return true;
            }
        }

        public void Dispose()
        {
            // Do not dispose synchronization primitives while an in-flight request may still execute its finally block.
            // Removal from the host dictionary makes this state unreachable by future requests; GC reclaims it after users finish.
            try { Stop.Cancel(); } catch { }
            lock (_rateGate) _recent.Clear();
        }
    }

    private sealed class DestinationRejectedException(string message) : HttpRequestException(message);
    private readonly record struct BodyReadResult(bool Success, TvAirExternalLookupResultCode Code, string Body, string Message)
    {
        public static BodyReadResult Ok(string body) => new(true, TvAirExternalLookupResultCode.Success, body, string.Empty);
        public static BodyReadResult Fail(TvAirExternalLookupResultCode code, string message) => new(false, code, string.Empty, message);
    }
}

public sealed class PluginExternalLookupProviderHostStatusDto
{
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string WebsiteUrl { get; init; } = string.Empty;
    public bool RequiresCredential { get; init; }
    public bool CredentialConfigured { get; init; }
    public bool RuntimeEnabled { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;
}
