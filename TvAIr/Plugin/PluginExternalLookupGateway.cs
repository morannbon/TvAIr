using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace TvAIr.Plugin;

/// <summary>
/// Single physical outbound network gateway for Plugin ExternalLookup.
/// The runtime NetworkUsageGate is the master boundary: OFF prevents new non-loopback traffic
/// and cancels in-flight requests without rewriting lower Plugin permission state.
/// </summary>
public interface IPluginExternalLookupGateway
{
    bool Enabled { get; }
    Task<HttpResponseMessage> SendAsync(PluginExternalLookupGatewayRequest request, CancellationToken cancellationToken);
}

public sealed class PluginExternalLookupGatewayRequest
{
    public required Uri Uri { get; init; }
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public string? BearerToken { get; init; }
}

internal sealed class PluginExternalLookupGateway : IPluginExternalLookupGateway, IDisposable
{
    private readonly TvAIr.Core.NetworkUsageGate _networkUsage;
    private readonly HttpClient _httpClient;

    public PluginExternalLookupGateway(TvAIr.Core.NetworkUsageGate networkUsage)
    {
        _networkUsage = networkUsage ?? throw new ArgumentNullException(nameof(networkUsage));
        _httpClient = CreateHttpClient();
    }

    public bool Enabled => _networkUsage.Enabled;

    public async Task<HttpResponseMessage> SendAsync(PluginExternalLookupGatewayRequest request, CancellationToken cancellationToken)
    {
        if (!_networkUsage.Enabled)
            throw new PluginExternalLookupGatewayDisabledException("Network usage is disabled by TvAIr settings.");

        using var networkCancellation = _networkUsage.CreateLinkedCancellation(cancellationToken);

        using var message = new HttpRequestMessage(request.Method, request.Uri);
        message.Headers.UserAgent.ParseAdd("TvAIr/1.2.1 PluginManagedExternalLookup");
        message.Headers.AcceptEncoding.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(request.BearerToken))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);

        return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, networkCancellation.Token).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 32,
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                var allowed = addresses.Where(IsPublicUnicastAddress).ToArray();
                if (allowed.Length != addresses.Length || allowed.Length == 0)
                    throw new PluginExternalLookupDestinationRejectedException("DNS resolved to a prohibited IP address.");

                Exception? last = null;
                foreach (var address in allowed)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        socket.Dispose();
                    }
                }
                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsPublicUnicastAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10 || b[0] == 127 || b[0] == 0) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] >= 224) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;
            if (address.IsIPv4MappedToIPv6) return IsPublicUnicastAddress(address.MapToIPv4());
            return true;
        }
        return false;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed class PluginExternalLookupGatewayDisabledException(string message) : HttpRequestException(message);
internal sealed class PluginExternalLookupDestinationRejectedException(string message) : HttpRequestException(message);
