using Microsoft.Data.Sqlite;

namespace TvAIr.Core;

/// <summary>Rules own bindings until recording starts; recovery always owns its saved snapshot.</summary>
internal static class KeywordReservationBindingProjection
{
    internal static string RuleValue(string ruleIdExpression)
        => $"COALESCE((SELECT plugin_binding_json FROM keyword_rules WHERE id = {ruleIdExpression}), '[]')";

    internal static IReadOnlyList<int> SynchronizeScheduled(SqliteConnection connection, SqliteTransaction transaction, int? ruleId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var value = RuleValue("r.source_rule_id");
        command.CommandText = $"""
            UPDATE reservations AS r
            SET plugin_binding_json = {value}, data_version = data_version + 1, updated_at = $now
            WHERE r.source = 'keyword' AND r.status = 'scheduled'
              AND r.recovery_parent_reservation_id IS NULL AND COALESCE(r.recording_started_at, '') = ''
              AND ($rule IS NULL OR r.source_rule_id = $rule)
              AND EXISTS (SELECT 1 FROM keyword_rules WHERE id = r.source_rule_id)
              AND r.plugin_binding_json IS NOT {value}
              AND NOT EXISTS (
                SELECT 1 FROM reservations active
                WHERE active.status IN ('starting','recording','stopping')
                  AND COALESCE(active.user_chain_root_id, active.id) = COALESCE(r.user_chain_root_id, r.id))
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$rule", (object?)ruleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        var changed = new List<int>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) changed.Add(reader.GetInt32(0));
        return changed;
    }
}
