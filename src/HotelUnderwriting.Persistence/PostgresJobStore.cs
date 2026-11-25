using HotelUnderwriting.Application;
using Npgsql;
using NpgsqlTypes;

namespace HotelUnderwriting.Persistence;

/// <summary>The durable queue: chunks are rows claimed with FOR UPDATE SKIP LOCKED, so any number of processes compete safely.</summary>
public sealed class PostgresJobStore(string connectionString, string schema = "public") : IJobStore
{
    private readonly string _cs = PostgresJson.ConnectionFor(connectionString, schema);

    private NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(_cs);
        conn.Open();
        return conn;
    }

    public (JobRecord Job, bool Created) Create(JobRecord job, string? idempotencyKey, LedgerEntry ledgerEntry)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(ledgerEntry);
        using var conn = Open();
        using var tx = conn.BeginTransaction(); // disposed without Commit = rollback
        int inserted;
        using (var cmd = new NpgsqlCommand(
            "insert into jobs (id, idempotency_key, request_hash, request, correlation_id, created_utc, case_count, chunk_count) values (@id, @key, @hash, @req, @corr, @at, @cases, @chunks) on conflict (idempotency_key) do nothing", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", job.Id);
            cmd.Parameters.AddWithValue("key", (object?)idempotencyKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("hash", job.RequestHash);
            cmd.Parameters.AddWithValue("req", job.Request.ToJson());
            cmd.Parameters.AddWithValue("corr", job.CorrelationId);
            cmd.Parameters.AddWithValue("at", job.CreatedUtc.ToUniversalTime());
            cmd.Parameters.AddWithValue("cases", job.CaseCount);
            cmd.Parameters.AddWithValue("chunks", job.ChunkCount);
            inserted = cmd.ExecuteNonQuery();
        }
        if (inserted == 0) // the key exists (a concurrent insert has committed by now)
        {
            using var find = new NpgsqlCommand(JobSelect + " where idempotency_key = @key", conn, tx);
            find.Parameters.AddWithValue("key", idempotencyKey!);
            using var r = find.ExecuteReader();
            var existing = r.Read() ? ReadJob(r) : throw new InvalidOperationException("Job vanished after a conflicting insert.");
            return existing.RequestHash == job.RequestHash ? (existing, false) : throw new IdempotencyConflictException("The idempotency key was already used with a different request.");
        }
        using (var cmd = new NpgsqlCommand(
            "insert into job_chunks (job_id, chunk_index, first_case, case_count) select @id, g, g * @size, least(@size, @cases - g * @size) from generate_series(0, @chunks - 1) g", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", job.Id);
            cmd.Parameters.AddWithValue("size", SweepLimits.ChunkSize);
            cmd.Parameters.AddWithValue("cases", job.CaseCount);
            cmd.Parameters.AddWithValue("chunks", job.ChunkCount);
            cmd.ExecuteNonQuery();
        }
        PostgresLedger.Insert(conn, tx, ledgerEntry);
        tx.Commit();
        return (job, true);
    }

    private const string JobSelect = "select id, request, request_hash, correlation_id, created_utc, case_count, chunk_count from jobs";

    private static JobRecord ReadJob(NpgsqlDataReader r) =>
        new(r.GetString(0), SweepRequest.FromJson(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetInt32(5), r.GetInt32(6));

    public JobRecord? TryGetJob(string jobId)
    {
        if (jobId.Any(char.IsControl)) return null; // PostgreSQL cannot store NUL: such an id is an unknown id
        using var conn = Open();
        using var cmd = new NpgsqlCommand(JobSelect + " where id = @id", conn);
        cmd.Parameters.AddWithValue("id", jobId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadJob(r) : null;
    }

    public JobProgress? GetProgress(string jobId)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand(@"
            select (select count(*) from job_chunks where job_id = @id),
                   (select count(*) from job_chunks where job_id = @id and state = 'done'),
                   (select count(*) from job_chunks where job_id = @id and state = 'queued'),
                   (select count(*) from job_chunks where job_id = @id and state = 'leased'),
                   (select count(*) from job_chunks where job_id = @id and state = 'dead'),
                   exists (select 1 from job_chunks where job_id = @id and attempts > 0),
                   (select count(*) from job_results where job_id = @id),
                   (select count(*) from job_results where job_id = @id and error is not null)
            where exists (select 1 from jobs where id = @id)", conn);
        cmd.Parameters.AddWithValue("id", jobId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new JobProgress(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5), r.GetInt32(6), r.GetInt32(7)) : null;
    }

    public JobChunk? TryClaimChunk(DateTimeOffset now, TimeSpan lease, string workerId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        while (true)
        {
            long id; string jobId; int first, count, attempts;
            using (var pick = new NpgsqlCommand(
                "select id, job_id, first_case, case_count, attempts from job_chunks where state = 'queued' or (state = 'leased' and lease_until <= @now) order by id limit 1 for update skip locked", conn, tx))
            {
                pick.Parameters.AddWithValue("now", now.ToUniversalTime());
                using var r = pick.ExecuteReader();
                if (!r.Read()) { r.Close(); tx.Commit(); return null; }
                (id, jobId, first, count, attempts) = (r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
            }
            if (attempts >= SweepLimits.MaxAttempts)
            {
                using var dead = new NpgsqlCommand("update job_chunks set state = 'dead', lease_owner = null, lease_until = null where id = @id", conn, tx);
                dead.Parameters.AddWithValue("id", id);
                dead.ExecuteNonQuery();
                continue;
            }
            using var take = new NpgsqlCommand("update job_chunks set state = 'leased', attempts = attempts + 1, lease_owner = @w, lease_until = @until where id = @id", conn, tx);
            take.Parameters.AddWithValue("w", workerId);
            take.Parameters.AddWithValue("until", (now + lease).ToUniversalTime());
            take.Parameters.AddWithValue("id", id);
            take.ExecuteNonQuery();
            tx.Commit();
            return new JobChunk(id, jobId, first, count, attempts + 1);
        }
    }

    public bool CompleteChunk(long chunkId, string workerId, IReadOnlyList<CaseResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        string? jobId;
        using (var done = new NpgsqlCommand("update job_chunks set state = 'done', lease_owner = null, lease_until = null where id = @id and state = 'leased' and lease_owner = @w returning job_id", conn, tx))
        {
            done.Parameters.AddWithValue("id", chunkId);
            done.Parameters.AddWithValue("w", workerId);
            jobId = done.ExecuteScalar() as string;
        }
        if (jobId is null) return false; // lease lost: store nothing
        using (var cmd = new NpgsqlCommand(
            "insert into job_results (job_id, case_index, run_key, error) select @job, i, k, e from unnest(@idx, @keys, @errs) as t(i, k, e) on conflict (job_id, case_index) do update set run_key = excluded.run_key, error = excluded.error", conn, tx))
        {
            cmd.Parameters.AddWithValue("job", jobId);
            cmd.Parameters.Add(new NpgsqlParameter("idx", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = results.Select(r => r.CaseIndex).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = results.Select(r => r.RunKey).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("errs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = results.Select(r => r.Error).ToArray() });
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public void FailChunk(long chunkId, string workerId, string error)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand("update job_chunks set state = 'queued', lease_owner = null, lease_until = null, last_error = @e where id = @id and state = 'leased' and lease_owner = @w", conn);
        cmd.Parameters.AddWithValue("id", chunkId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.AddWithValue("e", error);
        cmd.ExecuteNonQuery();
    }

    public void ReleaseChunk(long chunkId, string workerId)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand("update job_chunks set state = 'queued', lease_owner = null, lease_until = null, attempts = attempts - 1 where id = @id and state = 'leased' and lease_owner = @w", conn);
        cmd.Parameters.AddWithValue("id", chunkId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.ExecuteNonQuery();
    }

    public void DeferChunk(long chunkId, string workerId, DateTimeOffset until)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand("update job_chunks set lease_until = @until, attempts = attempts - 1 where id = @id and state = 'leased' and lease_owner = @w", conn);
        cmd.Parameters.AddWithValue("id", chunkId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.AddWithValue("until", until.ToUniversalTime());
        cmd.ExecuteNonQuery();
    }

    public bool RenewLease(long chunkId, string workerId, DateTimeOffset now, TimeSpan lease)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand("update job_chunks set lease_until = @until where id = @id and state = 'leased' and lease_owner = @w and lease_until > @now", conn);
        cmd.Parameters.AddWithValue("id", chunkId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("until", (now + lease).ToUniversalTime());
        return cmd.ExecuteNonQuery() == 1;
    }

    public (int Queued, double OldestQueuedAgeSeconds, int Dead) GetQueueStats(DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand(@"
            select count(*) filter (where c.state = 'queued' or (c.state = 'leased' and c.lease_until <= @now)),
                   coalesce(extract(epoch from (@now - min(j.created_utc) filter (where c.state = 'queued' or (c.state = 'leased' and c.lease_until <= @now)))), 0)::float8,
                   count(*) filter (where c.state = 'dead')
            from job_chunks c join jobs j on j.id = c.job_id where c.state <> 'done'", conn);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        using var r = cmd.ExecuteReader();
        r.Read();
        return ((int)r.GetInt64(0), Math.Max(0, r.GetDouble(1)), (int)r.GetInt64(2));
    }

    public IReadOnlyList<CaseResult> GetResults(string jobId, int afterCaseIndex, int limit)
    {
        using var conn = Open();
        using var cmd = new NpgsqlCommand("select case_index, run_key, error from job_results where job_id = @id and case_index > @after order by case_index limit @limit", conn);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("after", afterCaseIndex);
        cmd.Parameters.AddWithValue("limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<CaseResult>();
        while (r.Read()) list.Add(new CaseResult(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }
}
