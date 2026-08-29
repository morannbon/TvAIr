﻿using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TvAIr.Core;

/// <summary>LAN接続の要求元判定、セッション、ログイン試行制限の正本。</summary>
public sealed class NetworkAccessSecurity
{
    public const string SessionCookieName = "TvAIrNetworkSession";
    private const int MaxFailedAttempts = 6;
    private const int MaxSessionsPerAddress = 8;
    private const int MaxSessionsTotal = 256;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AttemptEntry> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sessionMaintenanceGate = new();
    private long _sessionGeneration;
    private long _lastCleanupTicks;

    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    public static bool IsLocalNetworkPeer(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (!IsPrivateAddress(address)) return false;

        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var local in network.GetIPProperties().UnicastAddresses)
                {
                    var localAddress = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
                    if (localAddress.AddressFamily != address.AddressFamily) continue;
                    if (IsSameSubnet(address, localAddress, local)) return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            return false;
        }

        return false;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    private static bool IsSameSubnet(IPAddress remote, IPAddress local, UnicastIPAddressInformation information)
    {
        var remoteBytes = remote.GetAddressBytes();
        var localBytes = local.GetAddressBytes();
        if (remoteBytes.Length != localBytes.Length) return false;

        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            var mask = information.IPv4Mask?.GetAddressBytes();
            if (mask is null || mask.Length != remoteBytes.Length) return false;
            for (var i = 0; i < remoteBytes.Length; i++)
                if ((remoteBytes[i] & mask[i]) != (localBytes[i] & mask[i])) return false;
            return true;
        }

        var prefixLength = information.PrefixLength;
        if (prefixLength <= 0 || prefixLength > remoteBytes.Length * 8) return false;
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var i = 0; i < fullBytes; i++)
            if (remoteBytes[i] != localBytes[i]) return false;
        if (remainingBits == 0) return true;
        var prefixMask = (byte)(0xFF << (8 - remainingBits));
        return (remoteBytes[fullBytes] & prefixMask) == (localBytes[fullBytes] & prefixMask);
    }

    // NETWORK_ACCESS_SESSION_GENERATION_CONTRACT
    // ログイン開始時のgenerationをセッション確定時にも照合する。LAN無効化または資格情報変更と
    // ログインが競合した場合、失効処理後に旧認証結果から新しいセッションを追加してはならない。
    public long CaptureSessionGeneration()
        => Interlocked.Read(ref _sessionGeneration);

    public bool TryCreateSession(
        TimeSpan lifetime,
        IPAddress? remoteAddress,
        string credentialSecret,
        long expectedGeneration,
        out (string Token, DateTimeOffset ExpiresAt) session)
    {
        CleanupIfDue();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var key = HashToken(token);
        var createdAt = DateTimeOffset.UtcNow;
        var expiresAt = createdAt.Add(lifetime);
        var addressKey = AddressKey(remoteAddress);
        lock (_sessionMaintenanceGate)
        {
            if (expectedGeneration != Interlocked.Read(ref _sessionGeneration))
            {
                session = default;
                return false;
            }

            TrimSessionCapacity(addressKey);
            _sessions[key] = new SessionEntry(createdAt, expiresAt, addressKey, CredentialKey(credentialSecret));
        }

        session = (token, expiresAt);
        return true;
    }

    private void TrimSessionCapacity(string addressKey)
    {
        foreach (var item in _sessions
                     .Where(x => string.Equals(x.Value.AddressKey, addressKey, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.Value.CreatedAt)
                     .Take(Math.Max(0, _sessions.Count(x => string.Equals(x.Value.AddressKey, addressKey, StringComparison.OrdinalIgnoreCase)) - MaxSessionsPerAddress + 1)))
            _sessions.TryRemove(item.Key, out _);

        foreach (var item in _sessions
                     .OrderBy(x => x.Value.CreatedAt)
                     .Take(Math.Max(0, _sessions.Count - MaxSessionsTotal + 1)))
            _sessions.TryRemove(item.Key, out _);
    }

    public bool ValidateSession(string? token, IPAddress? remoteAddress, string credentialSecret, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        CleanupIfDue();
        var key = HashToken(token);
        if (!_sessions.TryGetValue(key, out var entry)) return false;
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(entry.AddressKey, AddressKey(remoteAddress), StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(entry.CredentialKey),
                Convert.FromHexString(CredentialKey(credentialSecret))))
        {
            _sessions.TryRemove(key, out _);
            return false;
        }
        expiresAt = entry.ExpiresAt;
        return true;
    }

    public void RevokeSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        _sessions.TryRemove(HashToken(token), out _);
    }

    // NETWORK_ACCESS_INVALIDATION_TRANSACTION_CONTRACT
    // LAN無効化または認証資格情報変更の失効境界は、generation更新・既存セッション削除・
    // 必要時のlogin failure/block消去までを同じ保守ゲート内で一括確定する。
    // generation更新と失敗状態消去を別々に行うと、その隙間で新generationのログインが記録した
    // failureまで旧資格情報由来として消去できるため、分割入口へ戻してはならない。
    public (int RevokedSessions, int ClearedLoginFailures) InvalidateAccessState(bool clearLoginFailures)
    {
        lock (_sessionMaintenanceGate)
        {
            Interlocked.Increment(ref _sessionGeneration);

            var revoked = 0;
            foreach (var key in _sessions.Keys)
                if (_sessions.TryRemove(key, out _)) revoked++;

            var cleared = 0;
            if (clearLoginFailures)
            {
                foreach (var key in _attempts.Keys)
                    if (_attempts.TryRemove(key, out _)) cleared++;
            }

            return (revoked, cleared);
        }
    }

    public bool IsLoginBlocked(IPAddress? address, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var key = AddressKey(address);
        if (!_attempts.TryGetValue(key, out var entry)) return false;
        var now = DateTimeOffset.UtcNow;
        if (entry.BlockedUntil > now)
        {
            retryAfter = entry.BlockedUntil - now;
            return true;
        }
        if (entry.WindowStartedAt + AttemptWindow <= now)
            _attempts.TryRemove(key, out _);
        return false;
    }

    // NETWORK_ACCESS_LOGIN_ATTEMPT_GENERATION_CONTRACT
    // 資格情報変更と並行していた旧generationの認証結果から、変更後に失敗回数を再登録したり、
    // 新generationで蓄積された失敗回数を成功扱いで消去してはならない。
    // セッション確定と同じgeneration境界で、失敗記録・成功時消去も直列化する。
    public bool TryRecordLoginFailure(IPAddress? address, long expectedGeneration)
    {
        CleanupIfDue();
        lock (_sessionMaintenanceGate)
        {
            if (expectedGeneration != Interlocked.Read(ref _sessionGeneration)) return false;

            var key = AddressKey(address);
            var now = DateTimeOffset.UtcNow;
            _attempts.AddOrUpdate(key,
                _ => new AttemptEntry(now, 1, DateTimeOffset.MinValue),
                (_, current) =>
                {
                    var next = current.WindowStartedAt + AttemptWindow <= now
                        ? new AttemptEntry(now, 1, DateTimeOffset.MinValue)
                        : current with { FailedCount = current.FailedCount + 1 };
                    return next.FailedCount >= MaxFailedAttempts
                        ? next with { BlockedUntil = now.Add(BlockDuration) }
                        : next;
                });
            return true;
        }
    }

    public bool TryClearLoginFailures(IPAddress? address, long expectedGeneration)
    {
        lock (_sessionMaintenanceGate)
        {
            if (expectedGeneration != Interlocked.Read(ref _sessionGeneration)) return false;
            _attempts.TryRemove(AddressKey(address), out _);
            return true;
        }
    }

    private void CleanupIfDue()
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var previous = Interlocked.Read(ref _lastCleanupTicks);
        if (previous != 0 && nowTicks - previous < TimeSpan.FromMinutes(5).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastCleanupTicks, nowTicks, previous) != previous) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var item in _sessions)
            if (item.Value.ExpiresAt <= now) _sessions.TryRemove(item.Key, out _);
        foreach (var item in _attempts)
            if (item.Value.BlockedUntil <= now && item.Value.WindowStartedAt + AttemptWindow <= now)
                _attempts.TryRemove(item.Key, out _);
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string CredentialKey(string credentialSecret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialSecret ?? string.Empty)));

    private static string AddressKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private sealed record SessionEntry(DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string AddressKey, string CredentialKey);
    private sealed record AttemptEntry(DateTimeOffset WindowStartedAt, int FailedCount, DateTimeOffset BlockedUntil);
}
