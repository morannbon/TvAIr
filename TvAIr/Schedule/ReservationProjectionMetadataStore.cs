using Microsoft.Data.Sqlite;
using TvAIr.Core;
using TvAIr.Epg.Projection;

namespace TvAIr.Schedule;

/// <summary>
/// 予約がどの投影イベントから作られたかを、予約本体とは別境界で保持する。
/// epg_events には一切書かず、OverlayOnly 予約の後日昇格・照合に必要な由来だけを保存する。
/// </summary>
public sealed record ReservationProjectionMetadata(
    int ReservationId,
    string ProjectionState,
    string ProjectionSourceKind,
    string ProjectionSourcePluginId,
    string ProjectionSourceEventKey,
    string ProjectedEventId,
    ushort OverlayNetworkId,
    ushort OverlayTransportStreamId,
    ushort OverlayServiceId,
    ushort OverlayEventId,
    DateTime? OverlayStartTime,
    DateTime? OverlayEndTime,
    string OverlayTitle,
    ushort MatchedDbNetworkId,
    ushort MatchedDbTransportStreamId,
    ushort MatchedDbServiceId,
    ushort MatchedDbEventId,
    DateTime? PromotedAt,
    DateTime? SourceMissingAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class ReservationProjectionMetadataStore
{
    private readonly Database db;

    public ReservationProjectionMetadataStore(Database db)
    {
        this.db = db;
    }

    public ReservationProjectionMetadata? Get(int reservationId)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT reservation_id,
                   projection_state,
                   projection_source_kind,
                   projection_source_plugin_id,
                   projection_source_event_key,
                   projected_event_id,
                   overlay_network_id,
                   overlay_transport_stream_id,
                   overlay_service_id,
                   overlay_event_id,
                   overlay_start_time,
                   overlay_end_time,
                   overlay_title,
                   matched_db_network_id,
                   matched_db_transport_stream_id,
                   matched_db_service_id,
                   matched_db_event_id,
                   promoted_at,
                   source_missing_at,
                   created_at,
                   updated_at
            FROM reservation_projection_metadata
            WHERE reservation_id = $reservationId;
            """;
        cmd.Parameters.AddWithValue("$reservationId", reservationId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }


    public IReadOnlyList<ReservationProjectionMetadata> GetPendingPromotionCandidates()
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT reservation_id,
                   projection_state,
                   projection_source_kind,
                   projection_source_plugin_id,
                   projection_source_event_key,
                   projected_event_id,
                   overlay_network_id,
                   overlay_transport_stream_id,
                   overlay_service_id,
                   overlay_event_id,
                   overlay_start_time,
                   overlay_end_time,
                   overlay_title,
                   matched_db_network_id,
                   matched_db_transport_stream_id,
                   matched_db_service_id,
                   matched_db_event_id,
                   promoted_at,
                   source_missing_at,
                   created_at,
                   updated_at
            FROM reservation_projection_metadata
            WHERE (promoted_at IS NULL OR promoted_at = '')
              AND projection_source_kind = 'ExternalEpg'
              AND projection_state IN ('OverlayOnly', 'DbWithOverlay', 'SourceMissing')
            ORDER BY overlay_start_time, reservation_id;
            """;
        using var reader = cmd.ExecuteReader();
        var items = new List<ReservationProjectionMetadata>();
        while (reader.Read()) items.Add(Read(reader));
        return items;
    }

    public void Upsert(ReservationProjectionMetadata metadata)
    {
        var now = DateTime.Now;
        var createdAt = metadata.CreatedAt == default ? now : metadata.CreatedAt;
        var updatedAt = metadata.UpdatedAt == default ? now : metadata.UpdatedAt;

        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reservation_projection_metadata (
                reservation_id,
                projection_state,
                projection_source_kind,
                projection_source_plugin_id,
                projection_source_event_key,
                projected_event_id,
                overlay_network_id,
                overlay_transport_stream_id,
                overlay_service_id,
                overlay_event_id,
                overlay_start_time,
                overlay_end_time,
                overlay_title,
                matched_db_network_id,
                matched_db_transport_stream_id,
                matched_db_service_id,
                matched_db_event_id,
                promoted_at,
                source_missing_at,
                created_at,
                updated_at
            ) VALUES (
                $reservationId,
                $projectionState,
                $projectionSourceKind,
                $projectionSourcePluginId,
                $projectionSourceEventKey,
                $projectedEventId,
                $overlayNetworkId,
                $overlayTransportStreamId,
                $overlayServiceId,
                $overlayEventId,
                $overlayStartTime,
                $overlayEndTime,
                $overlayTitle,
                $matchedDbNetworkId,
                $matchedDbTransportStreamId,
                $matchedDbServiceId,
                $matchedDbEventId,
                $promotedAt,
                $sourceMissingAt,
                $createdAt,
                $updatedAt
            )
            ON CONFLICT(reservation_id) DO UPDATE SET
                projection_state = excluded.projection_state,
                projection_source_kind = excluded.projection_source_kind,
                projection_source_plugin_id = excluded.projection_source_plugin_id,
                projection_source_event_key = excluded.projection_source_event_key,
                projected_event_id = excluded.projected_event_id,
                overlay_network_id = excluded.overlay_network_id,
                overlay_transport_stream_id = excluded.overlay_transport_stream_id,
                overlay_service_id = excluded.overlay_service_id,
                overlay_event_id = excluded.overlay_event_id,
                overlay_start_time = excluded.overlay_start_time,
                overlay_end_time = excluded.overlay_end_time,
                overlay_title = excluded.overlay_title,
                matched_db_network_id = excluded.matched_db_network_id,
                matched_db_transport_stream_id = excluded.matched_db_transport_stream_id,
                matched_db_service_id = excluded.matched_db_service_id,
                matched_db_event_id = excluded.matched_db_event_id,
                promoted_at = excluded.promoted_at,
                source_missing_at = excluded.source_missing_at,
                updated_at = excluded.updated_at;
            """;
        AddParameters(cmd, metadata with { CreatedAt = createdAt, UpdatedAt = updatedAt });
        cmd.ExecuteNonQuery();
    }

    public void UpsertFromProjectedEvent(int reservationId, ProjectedProgramEvent projectedEvent)
    {
        var now = DateTime.Now;
        Upsert(new ReservationProjectionMetadata(
            ReservationId: reservationId,
            ProjectionState: projectedEvent.ProjectionState,
            ProjectionSourceKind: projectedEvent.SourceKind,
            ProjectionSourcePluginId: projectedEvent.SourcePluginId,
            ProjectionSourceEventKey: projectedEvent.SourceEventKey,
            ProjectedEventId: projectedEvent.Key.Value,
            OverlayNetworkId: projectedEvent.NetworkId,
            OverlayTransportStreamId: projectedEvent.TransportStreamId,
            OverlayServiceId: projectedEvent.ServiceId,
            OverlayEventId: projectedEvent.EventId,
            OverlayStartTime: projectedEvent.Start,
            OverlayEndTime: projectedEvent.End,
            OverlayTitle: projectedEvent.Title,
            MatchedDbNetworkId: 0,
            MatchedDbTransportStreamId: 0,
            MatchedDbServiceId: 0,
            MatchedDbEventId: 0,
            PromotedAt: null,
            SourceMissingAt: null,
            CreatedAt: now,
            UpdatedAt: now));
    }

    public void RebindExternalSource(int reservationId, ExternalEpgEvent externalEvent)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservation_projection_metadata
            SET projection_state = 'OverlayOnly',
                projection_source_kind = 'ExternalEpg',
                projection_source_plugin_id = $sourcePluginId,
                projection_source_event_key = $sourceEventKey,
                projected_event_id = $projectedEventId,
                overlay_network_id = $networkId,
                overlay_transport_stream_id = $transportStreamId,
                overlay_service_id = $serviceId,
                overlay_event_id = $eventId,
                overlay_start_time = $startTime,
                overlay_end_time = $endTime,
                overlay_title = $title,
                source_missing_at = '',
                updated_at = $updatedAt
            WHERE reservation_id = $reservationId
              AND (promoted_at IS NULL OR promoted_at = '');
            """;
        cmd.Parameters.AddWithValue("$reservationId", reservationId);
        cmd.Parameters.AddWithValue("$sourcePluginId", Safe(externalEvent.SourcePluginId));
        cmd.Parameters.AddWithValue("$sourceEventKey", Safe(externalEvent.SourceEventKey));
        cmd.Parameters.AddWithValue("$projectedEventId", BuildProjectedEventId(externalEvent));
        cmd.Parameters.AddWithValue("$networkId", externalEvent.NetworkId);
        cmd.Parameters.AddWithValue("$transportStreamId", externalEvent.TransportStreamId);
        cmd.Parameters.AddWithValue("$serviceId", externalEvent.ServiceId);
        cmd.Parameters.AddWithValue("$eventId", externalEvent.EventId);
        cmd.Parameters.AddWithValue("$startTime", ToDbTime(externalEvent.Start));
        cmd.Parameters.AddWithValue("$endTime", ToDbTime(externalEvent.End));
        cmd.Parameters.AddWithValue("$title", Safe(externalEvent.Title));
        cmd.Parameters.AddWithValue("$updatedAt", ToDbTime(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    private static string BuildProjectedEventId(ExternalEpgEvent externalEvent)
        => ProjectedEventKey.FromExternal(externalEvent).Value;

    public void MarkPromoted(int reservationId, ushort networkId, ushort transportStreamId, ushort serviceId, ushort eventId, DateTime promotedAt)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservation_projection_metadata
            SET projection_state = 'PromotedToDbEvent',
                matched_db_network_id = $networkId,
                matched_db_transport_stream_id = $transportStreamId,
                matched_db_service_id = $serviceId,
                matched_db_event_id = $eventId,
                promoted_at = $promotedAt,
                updated_at = $updatedAt
            WHERE reservation_id = $reservationId;
            """;
        cmd.Parameters.AddWithValue("$reservationId", reservationId);
        cmd.Parameters.AddWithValue("$networkId", networkId);
        cmd.Parameters.AddWithValue("$transportStreamId", transportStreamId);
        cmd.Parameters.AddWithValue("$serviceId", serviceId);
        cmd.Parameters.AddWithValue("$eventId", eventId);
        cmd.Parameters.AddWithValue("$promotedAt", ToDbTime(promotedAt));
        cmd.Parameters.AddWithValue("$updatedAt", ToDbTime(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    public void MarkSourceMissing(int reservationId, DateTime sourceMissingAt)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE reservation_projection_metadata
            SET projection_state = 'SourceMissing',
                source_missing_at = $sourceMissingAt,
                updated_at = $updatedAt
            WHERE reservation_id = $reservationId
              AND (promoted_at IS NULL OR promoted_at = '');
            """;
        cmd.Parameters.AddWithValue("$reservationId", reservationId);
        cmd.Parameters.AddWithValue("$sourceMissingAt", ToDbTime(sourceMissingAt));
        cmd.Parameters.AddWithValue("$updatedAt", ToDbTime(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    public void Delete(int reservationId)
    {
        using var con = db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM reservation_projection_metadata WHERE reservation_id = $reservationId;";
        cmd.Parameters.AddWithValue("$reservationId", reservationId);
        cmd.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand cmd, ReservationProjectionMetadata metadata)
    {
        cmd.Parameters.AddWithValue("$reservationId", metadata.ReservationId);
        cmd.Parameters.AddWithValue("$projectionState", Safe(metadata.ProjectionState));
        cmd.Parameters.AddWithValue("$projectionSourceKind", Safe(metadata.ProjectionSourceKind));
        cmd.Parameters.AddWithValue("$projectionSourcePluginId", Safe(metadata.ProjectionSourcePluginId));
        cmd.Parameters.AddWithValue("$projectionSourceEventKey", Safe(metadata.ProjectionSourceEventKey));
        cmd.Parameters.AddWithValue("$projectedEventId", Safe(metadata.ProjectedEventId));
        cmd.Parameters.AddWithValue("$overlayNetworkId", metadata.OverlayNetworkId);
        cmd.Parameters.AddWithValue("$overlayTransportStreamId", metadata.OverlayTransportStreamId);
        cmd.Parameters.AddWithValue("$overlayServiceId", metadata.OverlayServiceId);
        cmd.Parameters.AddWithValue("$overlayEventId", metadata.OverlayEventId);
        cmd.Parameters.AddWithValue("$overlayStartTime", ToDbTime(metadata.OverlayStartTime));
        cmd.Parameters.AddWithValue("$overlayEndTime", ToDbTime(metadata.OverlayEndTime));
        cmd.Parameters.AddWithValue("$overlayTitle", Safe(metadata.OverlayTitle));
        cmd.Parameters.AddWithValue("$matchedDbNetworkId", metadata.MatchedDbNetworkId);
        cmd.Parameters.AddWithValue("$matchedDbTransportStreamId", metadata.MatchedDbTransportStreamId);
        cmd.Parameters.AddWithValue("$matchedDbServiceId", metadata.MatchedDbServiceId);
        cmd.Parameters.AddWithValue("$matchedDbEventId", metadata.MatchedDbEventId);
        cmd.Parameters.AddWithValue("$promotedAt", ToDbTime(metadata.PromotedAt));
        cmd.Parameters.AddWithValue("$sourceMissingAt", ToDbTime(metadata.SourceMissingAt));
        cmd.Parameters.AddWithValue("$createdAt", ToDbTime(metadata.CreatedAt));
        cmd.Parameters.AddWithValue("$updatedAt", ToDbTime(metadata.UpdatedAt));
    }

    private static ReservationProjectionMetadata Read(SqliteDataReader reader)
        => new(
            ReservationId: reader.GetInt32(0),
            ProjectionState: reader.GetString(1),
            ProjectionSourceKind: reader.GetString(2),
            ProjectionSourcePluginId: reader.GetString(3),
            ProjectionSourceEventKey: reader.GetString(4),
            ProjectedEventId: reader.GetString(5),
            OverlayNetworkId: ToUShort(reader.GetInt32(6)),
            OverlayTransportStreamId: ToUShort(reader.GetInt32(7)),
            OverlayServiceId: ToUShort(reader.GetInt32(8)),
            OverlayEventId: ToUShort(reader.GetInt32(9)),
            OverlayStartTime: FromDbTime(reader.GetString(10)),
            OverlayEndTime: FromDbTime(reader.GetString(11)),
            OverlayTitle: reader.GetString(12),
            MatchedDbNetworkId: ToUShort(reader.GetInt32(13)),
            MatchedDbTransportStreamId: ToUShort(reader.GetInt32(14)),
            MatchedDbServiceId: ToUShort(reader.GetInt32(15)),
            MatchedDbEventId: ToUShort(reader.GetInt32(16)),
            PromotedAt: FromDbTime(reader.GetString(17)),
            SourceMissingAt: FromDbTime(reader.GetString(18)),
            CreatedAt: FromDbTime(reader.GetString(19)) ?? DateTime.MinValue,
            UpdatedAt: FromDbTime(reader.GetString(20)) ?? DateTime.MinValue);

    private static ushort ToUShort(int value)
        => value < 0 ? (ushort)0 : value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;

    private static string Safe(string? text)
        => (text ?? string.Empty).Trim();

    private static string ToDbTime(DateTime? value)
        => value.HasValue ? value.Value.ToString("O") : string.Empty;

    private static DateTime? FromDbTime(string? value)
        => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
