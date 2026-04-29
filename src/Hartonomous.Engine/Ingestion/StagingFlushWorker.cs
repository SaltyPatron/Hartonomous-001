using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Background worker that drains substrate.staging_* → substrate.* via
/// the substrate.drain_staging_*_chunk SQL functions. Runs continuously on
/// its own connection; entirely decoupled from producer transactions.
///
/// One instance per pipeline lifetime. <see cref="StartAsync"/> kicks off
/// the loop; <see cref="StopAsync"/> requests shutdown and awaits the final
/// drain pass to complete (so producers' final emits land in substrate
/// before the process exits).
///
/// Order of drain calls per cycle:
///   1. staging_entity         → substrate.entity (foundation for FKs)
///   2. staging_edge           → substrate.edge
///   3. staging_edge_member    → substrate.edge_member (FK on staging_edge order)
///   4. staging_physicality    → substrate.physicality (FK on entity)
///   5. staging_sequence       → substrate.sequence (FK on entity)
///   6. staging_entity_significance → substrate.entity_significance
///   7. staging_entity_model_source → substrate.entity_model_source
///   8. staging_junction       → substrate.entity_pos / entity_lexname / etc.
///
/// The drain functions use FOR UPDATE SKIP LOCKED, so multiple workers
/// could run safely. Default config: one worker per pipeline.
/// </summary>
public sealed partial class StagingFlushWorker : IAsyncDisposable
{
    /// <summary>
    /// Rows drained per chunk per kind. 4096 has been validated through hours
    /// of producer-side and shutdown drain runs against PG18 + the hartonomous
    /// extension without a single crash. Larger chunks (16K, 64K) reintroduced
    /// the same partition tuple-router stack-canary failure that crashed the
    /// pre-streaming pipeline — the substrate's tolerance for bulk-INSERT
    /// pressure under the partitioned target tables is bounded at ~4K rows
    /// per call. Do not bump this without validating against a UD-scale run.
    /// </summary>
    private const int DrainChunkRows = 4096;

    /// <summary>
    /// Catch-up uses the same chunk size as normal operation. The seemingly
    /// slow shutdown drain (minutes for millions of staged rows) is correctness
    /// time, not bug time. Larger chunks crash PG; small chunks complete safely.
    /// In long-running deployments the background loop runs continuously and
    /// catch-up is a non-event.
    /// </summary>
    private const int CatchUpChunkRows = DrainChunkRows;

    /// <summary>
    /// Idle sleep when all staging tables are empty. Wakes producers up
    /// quickly when work arrives without burning CPU on a tight poll loop.
    /// </summary>
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromMilliseconds(500);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<StagingFlushWorker> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    private long _entityRowsDrained;
    private long _edgeRowsDrained;
    private long _edgeMemberRowsDrained;
    private long _physicalityRowsDrained;
    private long _sequenceRowsDrained;
    private long _entitySignificanceRowsDrained;
    private long _entityModelSourceRowsDrained;
    private long _junctionRowsDrained;
    private long _idleCycles;

    public StagingFlushWorker(NpgsqlDataSource dataSource, ILogger<StagingFlushWorker> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public StagingFlushStats Stats => new()
    {
        EntityRowsDrained               = _entityRowsDrained,
        EdgeRowsDrained                 = _edgeRowsDrained,
        EdgeMemberRowsDrained           = _edgeMemberRowsDrained,
        PhysicalityRowsDrained          = _physicalityRowsDrained,
        SequenceRowsDrained             = _sequenceRowsDrained,
        EntitySignificanceRowsDrained   = _entitySignificanceRowsDrained,
        EntityModelSourceRowsDrained    = _entityModelSourceRowsDrained,
        JunctionRowsDrained             = _junctionRowsDrained,
        IdleCycles                      = _idleCycles,
    };

    public Task StartAsync()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("StagingFlushWorker already started");
        }
        _loop = Task.Run(() => RunAsync(_shutdown.Token));
        Log.WorkerStarted(_logger);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loop is null)
        {
            return;
        }

        // Stop the background loop FIRST so we own the drain connection
        // exclusively for the catch-up phase. Otherwise the loop and our
        // catch-up both connect and contend.
        _shutdown.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        _loop = null;

        // Drain-until-empty: keep draining until a full pass returns 0 rows.
        // Catch-up uses larger chunks (CatchUpChunkRows) since the producer
        // is gone and round-trips are pure overhead.
        long catchUpTotal = 0;
        try
        {
            while (true)
            {
                long pass = await DrainPassAsync(useCatchUpChunkSize: true, default).ConfigureAwait(false);
                if (pass == 0)
                {
                    break;
                }
                catchUpTotal += pass;
            }
        }
        catch (Exception ex)
        {
            Log.FinalDrainFailed(_logger, ex);
        }
        if (catchUpTotal > 0)
        {
            Log.CatchUpDrained(_logger, catchUpTotal);
        }

        Log.WorkerStopped(_logger,
            _entityRowsDrained, _edgeRowsDrained, _edgeMemberRowsDrained,
            _physicalityRowsDrained, _sequenceRowsDrained,
            _entitySignificanceRowsDrained, _entityModelSourceRowsDrained,
            _junctionRowsDrained, _idleCycles);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long drainedThisCycle;
                try
                {
                    drainedThisCycle = await DrainPassAsync(useCatchUpChunkSize: false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Don't crash the worker on a transient PG error — log,
                    // back off, retry. Persistent errors will surface as
                    // staging tables filling without draining.
                    Log.DrainPassFailed(_logger, ex);
                    drainedThisCycle = 0;
                }

                if (drainedThisCycle == 0)
                {
                    Interlocked.Increment(ref _idleCycles);
                    try
                    {
                        await Task.Delay(IdleBackoff, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }
                // Otherwise immediately go again — there might be more.
            }
        }
        catch (Exception ex)
        {
            Log.WorkerCrashed(_logger, ex);
            throw;
        }
    }

    /// <summary>
    /// One full pass: drain all 8 staging tables. Returns total rows drained
    /// across all kinds. Each drain runs in its own statement (its own implicit
    /// transaction) so a stuck table doesn't block the others.
    /// </summary>
    private async Task<long> DrainPassAsync(bool useCatchUpChunkSize, CancellationToken ct)
    {
        int chunk = useCatchUpChunkSize ? CatchUpChunkRows : DrainChunkRows;
        long total = 0;
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        total += await DrainOneAsync(conn, "substrate.drain_staging_entity_chunk", chunk,
            n => Interlocked.Add(ref _entityRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_edge_chunk", chunk,
            n => Interlocked.Add(ref _edgeRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_edge_member_chunk", chunk,
            n => Interlocked.Add(ref _edgeMemberRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_physicality_chunk", chunk,
            n => Interlocked.Add(ref _physicalityRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_sequence_chunk", chunk,
            n => Interlocked.Add(ref _sequenceRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_entity_significance_chunk", chunk,
            n => Interlocked.Add(ref _entitySignificanceRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_entity_model_source_chunk", chunk,
            n => Interlocked.Add(ref _entityModelSourceRowsDrained, n), ct).ConfigureAwait(false);
        total += await DrainOneAsync(conn, "substrate.drain_staging_junction_chunk", chunk,
            n => Interlocked.Add(ref _junctionRowsDrained, n), ct).ConfigureAwait(false);

        return total;
    }

    private async Task<long> DrainOneAsync(
        NpgsqlConnection conn,
        string functionName,
        int chunkRows,
        Action<long> updateCounter,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        await using NpgsqlCommand cmd = new($"SELECT {functionName}($1)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = chunkRows });
        cmd.CommandTimeout = 1800; // 30 min — should never approach this for ChunkRows=4096

        object? raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        long drained = raw switch
        {
            long l => l,
            int i => i,
            _ => 0L,
        };
        if (drained > 0)
        {
            updateCounter(drained);
            Log.ChunkDrained(_logger, functionName, drained, sw.Elapsed);
        }
        return drained;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "StagingFlushWorker started")]
        public static partial void WorkerStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "StagingFlushWorker stopped: entity={Entity} edge={Edge} edge_member={EdgeMember} physicality={Physicality} sequence={Sequence} entity_significance={EntitySig} entity_model_source={ModelSource} junction={Junction} idle_cycles={Idle}")]
        public static partial void WorkerStopped(ILogger logger,
            long entity, long edge, long edgeMember, long physicality, long sequence,
            long entitySig, long modelSource, long junction, long idle);

        [LoggerMessage(Level = LogLevel.Critical, Message = "StagingFlushWorker CRASHED")]
        public static partial void WorkerCrashed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "StagingFlushWorker drain pass failed")]
        public static partial void DrainPassFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "StagingFlushWorker final drain failed")]
        public static partial void FinalDrainFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Drained {Rows} rows via {Function} in {Elapsed}")]
        public static partial void ChunkDrained(ILogger logger, string function, long rows, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Information, Message = "Catch-up drain on shutdown: {Rows} rows drained until empty")]
        public static partial void CatchUpDrained(ILogger logger, long rows);
    }
}
