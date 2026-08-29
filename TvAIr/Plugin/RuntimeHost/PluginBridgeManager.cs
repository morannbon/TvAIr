using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TvAIrPlugin.Bridge;
using TvAIrPlugin.Runtime;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed class PluginBridgeManager : ITvAirPluginBridgeApi, IDisposable
{
    private const string CurrentProtocolVersion = "1";
    private readonly PluginRuntimeManager _runtime;
    private readonly HashSet<string> _grantedCapabilities;
    private readonly ConcurrentDictionary<string, HandlerRegistration> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private int _disposed;

    public PluginBridgeManager(PluginRuntimeManager runtime, IEnumerable<string>? grantedCapabilities = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _grantedCapabilities = ResolveGrantedCapabilities(grantedCapabilities);
    }

    public string ProtocolVersion => CurrentProtocolVersion;

    private static HashSet<string> ResolveGrantedCapabilities(IEnumerable<string>? grantedCapabilities)
    {
        var resolved = new HashSet<string>(grantedCapabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // Write access includes the corresponding read surface. Keep this implication in the
        // bridge capability gate so it matches the host permission contract.
        if (resolved.Contains(TvAirRuntimeCapabilities.VideoOverlayWrite))
            resolved.Add(TvAirRuntimeCapabilities.VideoOverlayRead);

        return resolved;
    }

    public void Register<TRequest, TResponse>(
        string method,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        string requestSchemaVersion = "1",
        string responseSchemaVersion = "1",
        string? requiredCapability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(method, new HandlerRegistration(
            new(method, requestSchemaVersion, responseSchemaVersion),
            requiredCapability,
            async (json, ct) =>
            {
                var request = json.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? Activator.CreateInstance<TRequest>()
                    : json.Deserialize<TRequest>(SerializerOptions);
                if (request is null)
                    throw new PluginBridgeException(TvAirErrorCode.InvalidRequest, $"Request payload for '{method}' is invalid.");
                return await handler(request, ct).ConfigureAwait(false);
            })))
        {
            throw new InvalidOperationException($"Duplicate plugin bridge method: {method}");
        }
    }

    public IReadOnlyList<PluginBridgeCapabilityDescriptor> ListMethods()
        => _handlers.Values.Select(x => x.Descriptor).OrderBy(x => x.Method, StringComparer.Ordinal).ToArray();

    public async Task<PluginBridgeResponse> InvokeAsync(PluginBridgeRequest request, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_runtime.IsConnected)
            return Failure(request?.RequestId ?? string.Empty, TvAirErrorCode.RuntimeDisconnected, "Plugin runtime is disconnected.");
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Method))
            return Failure(request?.RequestId ?? string.Empty, TvAirErrorCode.InvalidRequest, "Bridge request id and method are required.");
        if (!string.Equals(request.ProtocolVersion, CurrentProtocolVersion, StringComparison.Ordinal))
            return Failure(request.RequestId, TvAirErrorCode.InvalidRequest, "Unsupported bridge protocol version.");
        if (!string.Equals(request.RuntimeSessionId, _runtime.RuntimeSessionId, StringComparison.Ordinal))
            return Failure(request.RequestId, TvAirErrorCode.RuntimeDisconnected, "Runtime session identity is stale.");
        if (!_handlers.TryGetValue(request.Method, out var registration))
            return Failure(request.RequestId, TvAirErrorCode.UnsupportedMethod, $"Bridge method is not available: {request.Method}");
        if (!string.IsNullOrWhiteSpace(registration.RequiredCapability) && !_grantedCapabilities.Contains(registration.RequiredCapability))
            return Failure(request.RequestId, TvAirErrorCode.PermissionDenied, $"Capability is required: {registration.RequiredCapability}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.CancellationId) && !_cancellations.TryAdd(request.CancellationId, linked))
            return Failure(request.RequestId, TvAirErrorCode.InvalidRequest, "Cancellation id is already active.");

        try
        {
            var result = await registration.Handler(request.Parameters, linked.Token).ConfigureAwait(false);
            return new PluginBridgeResponse { RequestId = request.RequestId, Succeeded = true, Result = result };
        }
        catch (OperationCanceledException)
        {
            return Failure(request.RequestId, TvAirErrorCode.OperationCancelled, "Operation was cancelled.");
        }
        catch (PluginBridgeException ex)
        {
            return Failure(request.RequestId, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            return Failure(request.RequestId, TvAirErrorCode.InvalidRequest, ex.Message);
        }
        catch (Exception ex)
        {
            return Failure(request.RequestId, TvAirErrorCode.InternalError, ex.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(request.CancellationId))
                _cancellations.TryRemove(request.CancellationId, out _);
        }
    }

    public TvAirOperationResult Cancel(string cancellationId)
    {
        if (string.IsNullOrWhiteSpace(cancellationId))
            return TvAirOperationResult.Fail(TvAirErrorCode.InvalidRequest, "Cancellation id is required.");
        if (!_cancellations.TryGetValue(cancellationId, out var source))
            return TvAirOperationResult.Fail(TvAirErrorCode.EntityNotFound, "Cancellation id was not found.");
        source.Cancel();
        return TvAirOperationResult.Ok();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var source in _cancellations.Values) source.Cancel();
        foreach (var source in _cancellations.Values) source.Dispose();
        _cancellations.Clear();
        _handlers.Clear();
    }

    private static PluginBridgeResponse Failure(string requestId, TvAirErrorCode code, string message)
        => new() { RequestId = requestId, Error = new(code, message) };

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        return options;
    }

    private sealed record HandlerRegistration(
        PluginBridgeCapabilityDescriptor Descriptor,
        string? RequiredCapability,
        Func<JsonElement, CancellationToken, Task<object?>> Handler);

    internal sealed class PluginBridgeException(TvAirErrorCode code, string message) : Exception(message)
    {
        public TvAirErrorCode Code { get; } = code;
    }
}
