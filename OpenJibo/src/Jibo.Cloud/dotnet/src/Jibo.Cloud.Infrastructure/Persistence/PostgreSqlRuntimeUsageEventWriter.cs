using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>Dormant adapter for the invoker-rights state function. Its data source must be provisioned separately.</summary>
public sealed class PostgreSqlRuntimeUsageEventWriter : IRuntimeUsageEventWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _functionName;

    public PostgreSqlRuntimeUsageEventWriter(NpgsqlDataSource dataSource, string stateSchema)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (stateSchema is null || stateSchema.Length > 63 ||
            !Regex.IsMatch(stateSchema, "\\A[a-z_][a-z0-9_]*\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("State schema is invalid.", nameof(stateSchema));
        _functionName = $"\"{stateSchema}\".\"recordruntimeusageevent\"";
    }

    public async Task<RuntimeUsageWriteResult> WriteAsync(
        RuntimeUsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT resolvedmanagedrobotid, resolvedusagedate, accumulatorrevision, wasreplay
            FROM {_functionName}(
                @subject, @event_id, @occurred, @environment,
                @successful_turns, @failed_turns, @http_requests, @http_request_bytes,
                @http_response_bytes, @ws_in_messages, @ws_out_messages,
                @ws_in_bytes, @ws_out_bytes, @audio_input_bytes, @incomplete_reason)
            """, connection);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Bytea, usageEvent.SourceSubjectHmac.ToArray());
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, usageEvent.EventId);
        command.Parameters.AddWithValue("occurred", NpgsqlDbType.TimestampTz, usageEvent.OccurredUtc);
        command.Parameters.AddWithValue("environment", NpgsqlDbType.Text, usageEvent.ServiceEnvironment);
        command.Parameters.AddWithValue("successful_turns", NpgsqlDbType.Bigint, usageEvent.SuccessfulTurns);
        command.Parameters.AddWithValue("failed_turns", NpgsqlDbType.Bigint, usageEvent.FailedTurns);
        command.Parameters.AddWithValue("http_requests", NpgsqlDbType.Bigint, usageEvent.HttpRequests);
        command.Parameters.AddWithValue("http_request_bytes", NpgsqlDbType.Bigint, usageEvent.HttpRequestBytes);
        command.Parameters.AddWithValue("http_response_bytes", NpgsqlDbType.Bigint, usageEvent.HttpResponseBytes);
        command.Parameters.AddWithValue("ws_in_messages", NpgsqlDbType.Bigint, usageEvent.WebSocketInboundMessages);
        command.Parameters.AddWithValue("ws_out_messages", NpgsqlDbType.Bigint, usageEvent.WebSocketOutboundMessages);
        command.Parameters.AddWithValue("ws_in_bytes", NpgsqlDbType.Bigint, usageEvent.WebSocketInboundBytes);
        command.Parameters.AddWithValue("ws_out_bytes", NpgsqlDbType.Bigint, usageEvent.WebSocketOutboundBytes);
        command.Parameters.AddWithValue("audio_input_bytes", NpgsqlDbType.Bigint, usageEvent.AudioInputBytes);
        command.Parameters.Add(new NpgsqlParameter("incomplete_reason", NpgsqlDbType.Text)
        {
            Value = (object?)usageEvent.IncompleteReasonCode ?? DBNull.Value
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Runtime usage event write returned no result.");
        var result = new RuntimeUsageWriteResult(
            reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetInt64(2), reader.GetBoolean(3));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Runtime usage event write returned multiple results.");
        return result;
    }
}
