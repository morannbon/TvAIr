using Microsoft.Data.Sqlite;
using TvAIr.Core;

namespace TvAIr.Epg;

public sealed class ServiceLogoStore
{
    private readonly Database database;
    private readonly LogRepository log;

    public ServiceLogoStore(Database database, LogRepository log)
    {
        this.database = database;
        this.log = log;
    }

    public ServiceLogoDisplayPaths ResolveDisplayLogoPaths(ushort networkId, ushort transportStreamId, ushort serviceId, string? serviceName = null)
    {
        try
        {
            var titleBar = ResolveInventoryPath(networkId, transportStreamId, serviceId, new[] { 2, 5, 3, 4, 0, 1 }, out var titleType);
            var center = ResolveInventoryPath(networkId, transportStreamId, serviceId, new[] { 5, 2 }, out var centerType);

            // 既存代表ロゴは旧DB互換の保険として残す。中央表示はtype5/type2だけ、タイトルバーは小さいtypeも許可する。
            if (string.IsNullOrWhiteSpace(titleBar))
            {
                titleBar = ResolveRepresentativePath(networkId, transportStreamId, serviceId);
                titleType = -1;
            }

            var found = !string.IsNullOrWhiteSpace(titleBar) || !string.IsNullOrWhiteSpace(center);
            if (found)
            {
                log.Add("SERVICE_LOGO_RESOLVE", "FOUND",
                    $"service={Safe(serviceName)} nid={networkId} tsid={transportStreamId} sid={serviceId} " +
                    $"titleBarPath={Safe(titleBar)} titleBarType={(titleType.HasValue ? titleType.Value.ToString() : "-")} " +
                    $"centerPath={Safe(center)} centerType={(centerType.HasValue ? centerType.Value.ToString() : "-")} " +
                    $"source=inventory displayTargets=worker_titlebar_center rule=v0.10.45_cleanup_log_contract_polish");
            }
            return new ServiceLogoDisplayPaths(titleBar, center, titleType, centerType);
        }
        catch (Exception ex)
        {
            log.Add("SERVICE_LOGO_RESOLVE", "ERROR", $"service={Safe(serviceName)} nid={networkId} tsid={transportStreamId} sid={serviceId} error={ex.GetType().Name}:{Safe(ex.Message)} rule=v0.10.45_cleanup_log_contract_polish");
            return ServiceLogoDisplayPaths.Empty;
        }
    }

    public string? ResolveExistingLogoPath(ushort networkId, ushort transportStreamId, ushort serviceId, string? serviceName = null)
    {
        return ResolveDisplayLogoPaths(networkId, transportStreamId, serviceId, serviceName).TitleBarLogoPath;
    }

    private string? ResolveInventoryPath(ushort networkId, ushort transportStreamId, ushort serviceId, IReadOnlyList<int> preferredTypes, out int? logoType)
    {
        logoType = null;
        using var con = database.Open();
        foreach (var type in preferredTypes)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT logo_path FROM service_logo_inventory
                WHERE network_id=$nid AND transport_stream_id=$tsid AND service_id=$sid AND logo_type=$type
                ORDER BY logo_version DESC, updated_at DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$nid", (int)networkId);
            cmd.Parameters.AddWithValue("$tsid", (int)transportStreamId);
            cmd.Parameters.AddWithValue("$sid", (int)serviceId);
            cmd.Parameters.AddWithValue("$type", type);
            var value = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
            {
                logoType = type;
                return value;
            }
        }
        return null;
    }

    private string? ResolveRepresentativePath(ushort networkId, ushort transportStreamId, ushort serviceId)
    {
        using var con = database.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT logo_path FROM service_logos
            WHERE network_id=$nid AND transport_stream_id=$tsid AND service_id=$sid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$nid", (int)networkId);
        cmd.Parameters.AddWithValue("$tsid", (int)transportStreamId);
        cmd.Parameters.AddWithValue("$sid", (int)serviceId);
        var value = cmd.ExecuteScalar() as string;
        return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
    }

    public int UpsertMany(IEnumerable<ServiceLogoRecord> records)
    {
        var list = records
            .Where(r => !string.IsNullOrWhiteSpace(r.LogoPath) && File.Exists(r.LogoPath))
            .GroupBy(r => (r.NetworkId, r.TransportStreamId, r.ServiceId))
            .Select(g => g.OrderByDescending(x => x.LogoVersion).ThenByDescending(x => x.UpdatedAt).First())
            .ToList();
        if (list.Count == 0) return 0;
        using var con = database.Open();
        using var tx = con.BeginTransaction();
        foreach (var r in list)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO service_logos
                    (network_id, transport_stream_id, service_id, service_name, logo_id, logo_version, download_data_id, logo_path, source, updated_at)
                VALUES
                    ($nid, $tsid, $sid, $name, $logoId, $ver, $download, $path, $source, $updated)
                ON CONFLICT(network_id, transport_stream_id, service_id) DO UPDATE SET
                    service_name=excluded.service_name,
                    logo_id=excluded.logo_id,
                    logo_version=excluded.logo_version,
                    download_data_id=excluded.download_data_id,
                    logo_path=excluded.logo_path,
                    source=excluded.source,
                    updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$nid", (int)r.NetworkId);
            cmd.Parameters.AddWithValue("$tsid", (int)r.TransportStreamId);
            cmd.Parameters.AddWithValue("$sid", (int)r.ServiceId);
            cmd.Parameters.AddWithValue("$name", r.ServiceName ?? string.Empty);
            cmd.Parameters.AddWithValue("$logoId", r.LogoId);
            cmd.Parameters.AddWithValue("$ver", r.LogoVersion);
            cmd.Parameters.AddWithValue("$download", r.DownloadDataId);
            cmd.Parameters.AddWithValue("$path", r.LogoPath);
            cmd.Parameters.AddWithValue("$source", r.Source);
            cmd.Parameters.AddWithValue("$updated", r.UpdatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public int UpsertInventory(IEnumerable<ServiceLogoInventoryRecord> records)
    {
        var list = records
            .Where(r => !string.IsNullOrWhiteSpace(r.LogoPath) && File.Exists(r.LogoPath))
            .GroupBy(r => (r.NetworkId, r.TransportStreamId, r.ServiceId, r.LogoId, r.LogoType))
            .Select(g => g.OrderByDescending(x => x.LogoVersion).ThenByDescending(x => x.UpdatedAt).First())
            .ToList();
        if (list.Count == 0) return 0;

        using var con = database.Open();
        using var tx = con.BeginTransaction();
        foreach (var r in list)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO service_logo_inventory
                    (network_id, transport_stream_id, service_id, service_name, logo_id, logo_type, logo_version, download_data_id, logo_path, source, updated_at)
                VALUES
                    ($nid, $tsid, $sid, $name, $logoId, $type, $ver, $download, $path, $source, $updated)
                ON CONFLICT(network_id, transport_stream_id, service_id, logo_id, logo_type) DO UPDATE SET
                    service_name=excluded.service_name,
                    logo_version=excluded.logo_version,
                    download_data_id=excluded.download_data_id,
                    logo_path=excluded.logo_path,
                    source=excluded.source,
                    updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$nid", (int)r.NetworkId);
            cmd.Parameters.AddWithValue("$tsid", (int)r.TransportStreamId);
            cmd.Parameters.AddWithValue("$sid", (int)r.ServiceId);
            cmd.Parameters.AddWithValue("$name", r.ServiceName ?? string.Empty);
            cmd.Parameters.AddWithValue("$logoId", r.LogoId);
            cmd.Parameters.AddWithValue("$type", r.LogoType);
            cmd.Parameters.AddWithValue("$ver", r.LogoVersion);
            cmd.Parameters.AddWithValue("$download", r.DownloadDataId);
            cmd.Parameters.AddWithValue("$path", r.LogoPath);
            cmd.Parameters.AddWithValue("$source", r.Source);
            cmd.Parameters.AddWithValue("$updated", r.UpdatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public IReadOnlyList<ServiceLogoInventorySummary> GetInventorySummaries(int limit = 500)
    {
        var summaries = new List<ServiceLogoInventorySummary>();
        try
        {
            using var con = database.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                SELECT network_id, transport_stream_id, service_id,
                       MAX(service_name) AS service_name,
                       MAX(logo_id) AS logo_id,
                       GROUP_CONCAT(DISTINCT logo_type) AS logo_types,
                       MAX(updated_at) AS updated_at
                FROM service_logo_inventory
                GROUP BY network_id, transport_stream_id, service_id
                ORDER BY network_id, transport_stream_id, service_id
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var types = ParseTypes(reader[5]?.ToString());
                summaries.Add(new ServiceLogoInventorySummary(
                    NetworkId: Convert.ToUInt16(reader.GetInt32(0)),
                    TransportStreamId: Convert.ToUInt16(reader.GetInt32(1)),
                    ServiceId: Convert.ToUInt16(reader.GetInt32(2)),
                    ServiceName: reader[3]?.ToString() ?? string.Empty,
                    LogoId: reader.IsDBNull(4) ? -1 : reader.GetInt32(4),
                    AvailableTypes: types,
                    UpdatedAt: reader[6]?.ToString() ?? string.Empty));
            }
        }
        catch (Exception ex)
        {
            log.Add("SERVICE_LOGO_INVENTORY", "ERROR", $"result=ERROR phase=summary error={ex.GetType().Name}:{Safe(ex.Message)} rule=v0.9.66_logo_inventory_acquisition_only");
        }
        return summaries;
    }

    private static IReadOnlyList<int> ParseTypes(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1)
            .Where(x => x >= 0)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record ServiceLogoDisplayPaths(string? TitleBarLogoPath, string? CenterLogoPath, int? TitleBarLogoType, int? CenterLogoType)
{
    public static ServiceLogoDisplayPaths Empty { get; } = new(null, null, null, null);
}

public sealed record ServiceLogoRecord(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string? ServiceName,
    int LogoId,
    int LogoVersion,
    int DownloadDataId,
    string LogoPath,
    string Source,
    DateTimeOffset UpdatedAt);

public sealed record ServiceLogoInventoryRecord(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string? ServiceName,
    int LogoId,
    int LogoType,
    int LogoVersion,
    int DownloadDataId,
    string LogoPath,
    string Source,
    DateTimeOffset UpdatedAt);

public sealed record ServiceLogoInventorySummary(
    ushort NetworkId,
    ushort TransportStreamId,
    ushort ServiceId,
    string ServiceName,
    int LogoId,
    IReadOnlyList<int> AvailableTypes,
    string UpdatedAt)
{
    public bool HasCenterPreferred => AvailableTypes.Contains(5);
    public bool HasCenterFallback => AvailableTypes.Contains(2);
    public bool HasTitleBarSource => AvailableTypes.Contains(5) || AvailableTypes.Contains(2) || AvailableTypes.Contains(3) || AvailableTypes.Contains(4);
}
