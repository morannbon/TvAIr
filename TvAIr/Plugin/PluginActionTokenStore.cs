using TvAIrPlugin;

namespace TvAIr.Plugin;

/// <summary>
/// UIプラグインのHTMLから本体Action endpointへ戻るための短寿命トークン管理。
/// 認証情報ではなく、レンダリングされたプラグイン画面と本体操作要求を結び付けるCSRF/誤爆抑止用の軽量契約。
/// </summary>
public sealed class PluginActionTokenStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginActionTokenEntry> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(30);
    // release_contract: host-managed tool window は本体側safe-event host scriptから定期keepaliveされる。
    // 同じHTMLに埋め込まれた既存tokenを延命し、長時間無操作後のtoken_not_found/expiredを避ける。

    public PluginActionTokenEntry Issue(string pluginId, string routeSegment)
    {
        var entry = new PluginActionTokenEntry(
            Guid.NewGuid().ToString("N"),
            Normalize(pluginId),
            Normalize(routeSegment),
            DateTimeOffset.Now.Add(_ttl));
        lock (_gate)
        {
            CleanupUnsafe();
            _tokens[entry.Token] = entry;
        }
        return entry;
    }

    public bool Validate(string? token, string? pluginId, string? routeSegment, out string reason)
    {
        reason = "OK";
        if (string.IsNullOrWhiteSpace(token))
        {
            reason = "missing_token";
            return false;
        }

        lock (_gate)
        {
            CleanupUnsafe();
            if (!_tokens.TryGetValue(token.Trim(), out var entry))
            {
                reason = "token_not_found";
                return false;
            }
            if (entry.ExpiresAt < DateTimeOffset.Now)
            {
                _tokens.Remove(entry.Token);
                reason = "token_expired";
                return false;
            }

            var requestedPlugin = Normalize(pluginId);
            var requestedRoute = Normalize(routeSegment);
            if (!string.IsNullOrWhiteSpace(requestedPlugin)
                && !string.Equals(entry.PluginId, requestedPlugin, StringComparison.OrdinalIgnoreCase))
            {
                reason = "plugin_mismatch";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(requestedRoute)
                && !string.Equals(entry.RouteSegment, requestedRoute, StringComparison.OrdinalIgnoreCase))
            {
                reason = "route_mismatch";
                return false;
            }
            return true;
        }
    }


    public bool Renew(string? token, string? pluginId, string? routeSegment, out string reason, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.MinValue;
        reason = "OK";
        if (string.IsNullOrWhiteSpace(token))
        {
            reason = "missing_token";
            return false;
        }

        lock (_gate)
        {
            CleanupUnsafe();
            var key = token.Trim();
            if (!_tokens.TryGetValue(key, out var entry))
            {
                reason = "token_not_found";
                return false;
            }

            var requestedPlugin = Normalize(pluginId);
            var requestedRoute = Normalize(routeSegment);
            if (!string.IsNullOrWhiteSpace(requestedPlugin)
                && !string.Equals(entry.PluginId, requestedPlugin, StringComparison.OrdinalIgnoreCase))
            {
                reason = "plugin_mismatch";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(requestedRoute)
                && !string.Equals(entry.RouteSegment, requestedRoute, StringComparison.OrdinalIgnoreCase))
            {
                reason = "route_mismatch";
                return false;
            }

            expiresAt = DateTimeOffset.Now.Add(_ttl);
            _tokens[key] = entry with { ExpiresAt = expiresAt };
            return true;
        }
    }

    private void CleanupUnsafe()
    {
        var now = DateTimeOffset.Now;
        foreach (var key in _tokens.Where(x => x.Value.ExpiresAt < now).Select(x => x.Key).ToList())
        {
            _tokens.Remove(key);
        }
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().Trim('/');
}

public sealed record PluginActionTokenEntry(string Token, string PluginId, string RouteSegment, DateTimeOffset ExpiresAt);
