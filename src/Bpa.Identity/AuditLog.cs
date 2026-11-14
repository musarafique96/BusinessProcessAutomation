using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Bpa.Identity;

/// <summary>
/// Writes audit events. Call it inside the same transaction as the change it records, so a change
/// can't be saved without its audit event, or the other way round.
/// Callers pass only safe fields: never passwords, tokens or sensitive personal data.
/// </summary>
public static class AuditLog
{
    public static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        string actorType,
        Guid? actorId,
        string action,
        string targetType,
        string? targetId,
        object? before,
        object? after,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_events (tenant_id, actor_type, actor_id, action, target_type, target_id, before, after)
            VALUES (@tenant_id, @actor_type, @actor_id, @action, @target_type, @target_id, @before::jsonb, @after::jsonb)
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("actor_type", actorType);
        command.Parameters.Add(new NpgsqlParameter("actor_id", NpgsqlDbType.Uuid) { Value = (object?)actorId ?? DBNull.Value });
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("target_type", targetType);
        command.Parameters.Add(new NpgsqlParameter("target_id", NpgsqlDbType.Text) { Value = (object?)targetId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Text)
        {
            Value = before is null ? DBNull.Value : JsonSerializer.Serialize(before),
        });
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Text)
        {
            Value = after is null ? DBNull.Value : JsonSerializer.Serialize(after),
        });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
