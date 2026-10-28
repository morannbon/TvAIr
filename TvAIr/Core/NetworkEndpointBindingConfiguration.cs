using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace TvAIr.Core;

/// <summary>
/// Kestrel の物理待受 endpoint を「ネットワーク利用」正本へ追従させる構成正本。
///
/// NETWORK_USAGE_MASTER_CLOSED_NETWORK_INVARIANT — 変更禁止:
/// 「ネットワーク利用 OFF」は TvAIr 内部の localhost/loopback IPC を止める設定ではない。
/// 脅威境界は外部（非loopback）であり、OFF時は LAN/Internet の内向き・外向き通信を遮断しつつ、
/// EPG・録画・予約・ProgramGuide・Plugin Host 等のローカル本体機能は loopback 上で継続動作させる。
///
/// したがって OFF / LAN非許可時に 0.0.0.0 で待受け、HTTP middleware で 403 を返す設計へ戻してはならない。
/// 403 はアプリ層の拒否であり、外部peerからsocketへ到達できる攻撃面を残すため「完全閉域」ではない。
/// LAN公開が実効的に許可された時だけ、private LAN interface のアドレスを物理endpointへ追加する。
/// </summary>
public sealed class NetworkEndpointBindingConfiguration : IDisposable
{
    private readonly IniSettingsService _settings;
    private readonly NetworkUsageGate _networkUsage;
    private readonly ReloadableNetworkEndpointConfigurationProvider _provider;
    private readonly object _gate = new();
    private bool _disposed;

    public NetworkEndpointBindingConfiguration(IniSettingsService settings, NetworkUsageGate networkUsage)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _networkUsage = networkUsage ?? throw new ArgumentNullException(nameof(networkUsage));
        _provider = new ReloadableNetworkEndpointConfigurationProvider();
        ApplyCurrentPolicy(notify: false);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public IConfigurationSource ConfigurationSource => new ExistingProviderConfigurationSource(_provider);

    public NetworkEndpointBindingSnapshot ApplyCurrentPolicy()
        => ApplyCurrentPolicy(notify: true);

    private NetworkEndpointBindingSnapshot ApplyCurrentPolicy(bool notify)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var policy = _settings.CaptureOsPolicySnapshot();
            var networkEnabled = policy.NetworkUsageEnabled && _networkUsage.Enabled;
            var lanEffective = networkEnabled && policy.LanAccessEnabled && policy.HasNetworkPassword;

            var endpoints = new List<IPAddress> { IPAddress.Loopback };
            if (lanEffective)
                endpoints.AddRange(GetBindablePrivateLanAddresses());

            var unique = endpoints
                .Distinct(IPAddressEqualityComparer.Instance)
                .OrderBy(a => IPAddress.IsLoopback(a) ? 0 : 1)
                .ThenBy(a => a.ToString(), StringComparer.Ordinal)
                .ToArray();

            _provider.Apply(policy.RuntimePort, unique, notify);
            return new NetworkEndpointBindingSnapshot(
                networkEnabled,
                lanEffective,
                unique.Select(a => a.ToString()).ToArray());
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        try
        {
            // DHCP / adapter up-down 後も「LAN許可時のみprivate interfaceを公開」という同じ正本へ収束させる。
            // NetworkUsage OFF時は再評価してもloopback endpointだけなので、外部公開へ戻ることはない。
            ApplyCurrentPolicy();
        }
        catch
        {
            // OS network-change callback上の例外でTvAIrを終了させない。
            // 次回設定適用または次のnetwork-changeで同じ正本から再評価する。
        }
    }

    private static IReadOnlyList<IPAddress> GetBindablePrivateLanAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

                    // 現行LAN公開契約はprivate subnet peerだけを認証対象にする。
                    // 物理bindも同じ境界へ揃え、public/global addressへlistenerを公開しない。
                    if (address.AddressFamily == AddressFamily.InterNetwork
                        && NetworkAccessSecurity.IsPrivateNetworkAddress(address))
                        result.Add(address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // private LAN addressを確定できない場合は公開を広げない。loopback-onlyへfail closedする。
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
    }

    private sealed class IPAddressEqualityComparer : IEqualityComparer<IPAddress>
    {
        public static readonly IPAddressEqualityComparer Instance = new();
        public bool Equals(IPAddress? x, IPAddress? y) => x?.Equals(y) ?? y is null;
        public int GetHashCode(IPAddress obj) => obj.GetHashCode();
    }

    private sealed class ExistingProviderConfigurationSource : IConfigurationSource
    {
        private readonly IConfigurationProvider _provider;
        public ExistingProviderConfigurationSource(IConfigurationProvider provider) => _provider = provider;
        public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
    }

    private sealed class ReloadableNetworkEndpointConfigurationProvider : ConfigurationProvider
    {
        private readonly object _providerGate = new();
        private string _signature = string.Empty;

        public void Apply(int port, IReadOnlyList<IPAddress> endpoints, bool notify)
        {
            var next = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < endpoints.Count; i++)
            {
                var address = endpoints[i];
                var host = address.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"[{address}]"
                    : address.ToString();
                next[$"Kestrel:Endpoints:TvAIr{i}:Url"] = $"http://{host}:{port}";
            }

            var signature = string.Join("|", next.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Value}"));

            lock (_providerGate)
            {
                if (string.Equals(signature, _signature, StringComparison.Ordinal)) return;
                Data = next;
                _signature = signature;
            }

            if (notify) OnReload();
        }
    }
}

public sealed record NetworkEndpointBindingSnapshot(
    bool NetworkUsageEnabled,
    bool LanAccessEffective,
    IReadOnlyList<string> BoundAddresses);
