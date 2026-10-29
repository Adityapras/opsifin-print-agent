using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Agent.Hub;

public sealed record PendingOutboxEvent(
    Guid JobId,
    JobEvent Event,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? LastError);

public sealed class EventOutbox
{
    private readonly string connectionString;

    public EventOutbox(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("Path database outbox tidak memiliki direktori.");
        Directory.CreateDirectory(directory);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;

            CREATE TABLE IF NOT EXISTS event_outbox (
                event_id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                created_at_ms INTEGER NOT NULL,
                next_attempt_at_ms INTEGER NOT NULL,
                last_error TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_event_outbox_due
                ON event_outbox(next_attempt_at_ms, created_at_ms);

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> EnqueueAsync(
        Guid jobId,
        JobEvent jobEvent,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(jobEvent, AgentJsonContext.Default.JobEvent);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_outbox (
                event_id, job_id, payload_json, attempts, created_at_ms, next_attempt_at_ms)
            VALUES ($event_id, $job_id, $payload_json, 0, $now, $now)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$event_id", jobEvent.EventId.ToString("D"));
        command.Parameters.AddWithValue("$job_id", jobId.ToString("D"));
        command.Parameters.AddWithValue("$payload_json", payload);
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PendingOutboxEvent>> GetDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Batch outbox harus 1 sampai 100 event.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, payload_json, attempts, next_attempt_at_ms, last_error
            FROM event_outbox
            WHERE next_attempt_at_ms <= $now
            -- rowid mengikuti urutan INSERT bahkan ketika beberapa event lahir pada
            -- milidetik yang sama; Hub harus melihat spooled sebelum completed.
            ORDER BY rowid
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<PendingOutboxEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var jobEvent = JsonSerializer.Deserialize(
                reader.GetString(1),
                AgentJsonContext.Default.JobEvent)
                ?? throw new InvalidDataException("Payload event outbox kosong.");
            result.Add(new PendingOutboxEvent(
                Guid.Parse(reader.GetString(0)),
                jobEvent,
                reader.GetInt32(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    public async Task MarkDeliveredAsync(
        IReadOnlyCollection<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        if (eventIds.Count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(eventIds), "Maksimal 100 event per batch.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var parameterNames = new string[eventIds.Count];
        var index = 0;
        foreach (var eventId in eventIds)
        {
            var parameterName = $"$event_id_{index}";
            parameterNames[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, eventId.ToString("D"));
            index++;
        }

        command.CommandText = $"DELETE FROM event_outbox WHERE event_id IN ({string.Join(',', parameterNames)});";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RescheduleAsync(
        Guid eventId,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE event_outbox
            SET attempts = attempts + 1,
                next_attempt_at_ms = $next_attempt_at_ms,
                last_error = $last_error
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        command.Parameters.AddWithValue("$next_attempt_at_ms", nextAttemptAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$last_error",
            error.Length <= 2000 ? error : error[..2000]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM event_outbox;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
