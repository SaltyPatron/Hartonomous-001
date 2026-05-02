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

    /// <summary>
    /// Number of consecutive failures the catch-up shutdown drain tolerates
    /// before giving up. Each retry opens a fresh connection so transient
    /// PG drops (SEGV recovery, container restart, idle_in_transaction
    /// timeout) don't strand staging rows on shutdown. Five * 2s backoff
    /// covers ~10s of PG unavailability.
    /// </summary>
    private const int MaxFinalDrainRetries = 5;

    /// <summary>
    /// Startup-residue drain attempts. Higher than shutdown's MaxFinalDrainRetries
    /// because failing here is a hard stop (no producer can run); we'd rather
    /// block boot for several minutes waiting for PG than declare unrecoverable
    /// loss. Combined with exponential backoff this caps at ~5 minutes.
    /// </summary>
    private const int MaxStartupDrainAttempts = 12;

    private static readonly TimeSpan FinalDrainRetryBackoff = TimeSpan.FromSeconds(2);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<StagingFlushWorker> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    private long _totalRowsDrained;
    private long _idleCycles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _rowsDrainedByFunction = new();

    /// <summary>
    /// Whether the most recent <see cref="StopAsync"/> drained staging to
    /// empty AND the residue probe confirmed it. False until StopAsync runs;
    /// false if the catch-up loop exhausted retries with rows still in
    /// staging. The CLI uses this to set a non-zero exit code so
    /// orchestration scripts notice that the substrate is incomplete and
    /// the next CLI invocation must run before any downstream consumer
    /// trusts the substrate.
    /// </summary>
    public bool LastShutdownDrainCompleted { get; private set; }

    public StagingFlushWorker(NpgsqlDataSource dataSource, ILogger<StagingFlushWorker> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public StagingFlushStats Stats => new()
    {
        TotalRowsDrained     = _totalRowsDrained,
        RowsDrainedByFunction = new System.Collections.Generic.Dictionary<string, long>(_rowsDrainedByFunction),
        IdleCycles           = _idleCycles,
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

    /// <summary>
    /// Synchronous barrier: drain any residue left in substrate.staging_* by
    /// a prior CLI invocation that exited before its catch-up drain finished.
    /// Must be called BEFORE producers emit new content. Without this barrier,
    /// the previous run's stranded rows interleave with the new run's emits;
    /// monitor.phase_status records phases as completed while the substrate
    /// is missing rows from the last run, and the user has no way to detect
    /// the drift short of comparing emission vs drain counters across runs.
    ///
    /// Staging is persistent (migration 0019: substrate.staging_* survives
    /// pipeline restart and PG restart), so this method is the recovery path
    /// for any failure mode that left rows behind — including SEGV-induced
    /// connection drops the CLI saw "Attempted to read past the end of the
    /// stream" on the previous shutdown.
    ///
    /// Drains the same way StopAsync's catch-up does, but keeps retrying with
    /// exponential backoff (up to MaxStartupDrainAttempts) before throwing.
    /// Throwing here is correct: if startup-residue drain cannot succeed,
    /// proceeding to producer emit would compound the loss. Surface the
    /// underlying issue (typically PG instability) to the operator.
    /// </summary>
    public async Task DrainPreExistingResidueAsync(CancellationToken ct)
    {
        long initialResidue;
        try
        {
            initialResidue = await ProbeStagingResidueAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // If we can't even probe, propagate — the alternative is
            // unknowingly racing the producer against unknown residue.
            throw new InvalidOperationException(
                "Failed to probe substrate.staging_* on startup; cannot guarantee substrate coherence.",
                ex);
        }

        if (initialResidue == 0)
        {
            return;
        }

        Log.StartupResidueDetected(_logger, initialResidue);

        long drained = 0;
        int consecutiveFailures = 0;
        TimeSpan backoff = FinalDrainRetryBackoff;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long pass;
            try
            {
                pass = await DrainPassAsync(useCatchUpChunkSize: true, ct).ConfigureAwait(false);
                consecutiveFailures = 0;
                backoff = FinalDrainRetryBackoff;
            }
            catch (Exception ex) // BOUNDARY: startup-residue drain — retry transient PG drops
            {
                consecutiveFailures++;
                Log.StartupDrainPassFailed(_logger, ex, consecutiveFailures, MaxStartupDrainAttempts);
                if (consecutiveFailures >= MaxStartupDrainAttempts)
                {
                    throw new InvalidOperationException(
                        $"Could not drain pre-existing staging residue after {MaxStartupDrainAttempts} attempts. " +
                        "Substrate is incomplete from a prior run; producer emission would compound the loss. " +
                        "Investigate the underlying database failure (PG SEGV, container exit, network) and retry.",
                        ex);
                }
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                // Exponential backoff capped at 30s, total max wait
                // ~5 min across MaxStartupDrainAttempts attempts.
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 30_000));
                continue;
            }

            if (pass == 0)
            {
                break;
            }
            drained += pass;
        }

        long finalResidue = await ProbeStagingResidueAsync(ct).ConfigureAwait(false);
        if (finalResidue > 0)
        {
            // The drain loop exited claiming "0 rows in pass" but the probe
            // sees rows — should not happen; defensively flag.
            throw new InvalidOperationException(
                $"Startup-residue drain claimed completion but {finalResidue} rows remain in staging. " +
                "Refusing to start producer.");
        }
        Log.StartupResidueDrained(_logger, initialResidue, drained);
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
        // Each pass opens a fresh connection (DrainPassAsync line 214), so a
        // transient PG drop kills only one pass — we retry up to
        // MaxFinalDrainRetries times before declaring the staging stuck.
        // Without retry, the previous body bailed on the FIRST connection
        // hiccup and silently abandoned millions of staged rows; the
        // post-shutdown residue probe at the end of this method now reports
        // the actual remaining row counts so the lie ("drained until empty"
        // when the loop had thrown) cannot recur.
        long catchUpTotal = 0;
        bool catchUpClean = false;
        Exception? lastError = null;
        int consecutiveFailures = 0;
        while (true)
        {
            long pass;
            try
            {
                pass = await DrainPassAsync(useCatchUpChunkSize: true, default).ConfigureAwait(false);
                consecutiveFailures = 0;
                lastError = null;
            }
            catch (Exception ex) // BOUNDARY: catch-up shutdown drain — retry transient PG drops
            {
                consecutiveFailures++;
                lastError = ex;
                Log.FinalDrainPassFailed(_logger, ex, consecutiveFailures, MaxFinalDrainRetries);
                if (consecutiveFailures >= MaxFinalDrainRetries)
                {
                    Log.FinalDrainFailed(_logger, ex);
                    break;
                }
                try
                {
                    await Task.Delay(FinalDrainRetryBackoff).ConfigureAwait(false);
                }
                catch (OperationCanceledException) // BOUNDARY: cancellation during retry backoff
                {
                    break;
                }
                continue;
            }
            if (pass == 0)
            {
                catchUpClean = true;
                break;
            }
            catchUpTotal += pass;
        }

        // Probe each staging table to report actual residue. The previous
        // "drained until empty" message fired even when the loop exited
        // via exception, hiding silent data loss. Now we tell the truth:
        // either staging is empty, or here's exactly how many rows of which
        // kind are stranded.
        long stagingResidue = 0;
        try
        {
            stagingResidue = await ProbeStagingResidueAsync(default).ConfigureAwait(false);
        }
        catch (Exception ex) // BOUNDARY: residue probe is diagnostic; failure must not mask drain status
        {
            Log.ResidueProbeFailed(_logger, ex);
        }

        LastShutdownDrainCompleted = catchUpClean && stagingResidue == 0;

        if (catchUpTotal > 0)
        {
            if (LastShutdownDrainCompleted)
            {
                Log.CatchUpDrainedClean(_logger, catchUpTotal);
            }
            else
            {
                Log.CatchUpDrainedDirty(_logger, catchUpTotal, stagingResidue);
            }
        }
        else if (stagingResidue > 0)
        {
            // Producer never observed any catch-up work but staging still has
            // rows — must have been left over from before this worker started,
            // or the very first drain attempt failed.
            Log.CatchUpDrainedDirty(_logger, 0, stagingResidue);
        }
        else
        {
            // No catch-up needed and no residue — the typical clean path.
            LastShutdownDrainCompleted = true;
        }

        string breakdown = string.Join(' ',
            _rowsDrainedByFunction.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        Log.WorkerStopped(_logger, _totalRowsDrained, breakdown, _idleCycles);
    }

    /// <summary>
    /// Sum row counts across all eight staging tables. Cheap (each is a
    /// single COUNT against an unindexed table that's expected to be small
    /// or empty after a successful drain). Diagnostic only — failure of
    /// this probe is logged but does not change the drain outcome.
    /// </summary>
    private async Task<long> ProbeStagingResidueAsync(CancellationToken ct)
    {
        // substrate.staging_residue() auto-discovers every staging_* table.
        // Adding a staging table doesn't require a code change here.
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT table_name, rows FROM substrate.staging_residue() WHERE rows > 0", conn);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long total = 0;
        var nonEmpty = new System.Collections.Generic.List<(string Name, long Rows)>(16);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = r.GetString(0);
            long rows = r.GetInt64(1);
            total += rows;
            nonEmpty.Add((name, rows));
        }
        if (total > 0)
        {
            string detail = string.Join(' ', nonEmpty.Select(t => $"{t.Name}={t.Rows}"));
            Log.StagingResidue(_logger, detail, total);
        }
        return total;
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
    /// One full pass over EVERY staging table. substrate.drain_all_staging
    /// auto-discovers drain functions via pg_proc — adding a staging table
    /// does not require touching this method. Returns total rows drained
    /// across all kinds.
    /// </summary>
    private async Task<long> DrainPassAsync(bool useCatchUpChunkSize, CancellationToken ct)
    {
        int chunk = useCatchUpChunkSize ? CatchUpChunkRows : DrainChunkRows;
        Stopwatch sw = Stopwatch.StartNew();
        long total = 0;
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT function_name, rows_drained FROM substrate.drain_all_staging($1)", conn);
        cmd.Parameters.AddWithValue(chunk);
        cmd.CommandTimeout = 1800;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string fn = r.GetString(0);
            long drained = r.GetInt64(1);
            if (drained > 0)
            {
                total += drained;
                _rowsDrainedByFunction.AddOrUpdate(fn, drained, (_, prev) => prev + drained);
                Log.ChunkDrained(_logger, fn, drained, sw.Elapsed);
            }
        }
        Interlocked.Add(ref _totalRowsDrained, total);
        return total;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "StagingFlushWorker started")]
        public static partial void WorkerStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "StagingFlushWorker stopped: total={Total} idle_cycles={Idle} breakdown={Breakdown}")]
        public static partial void WorkerStopped(ILogger logger, long total, string breakdown, long idle);

        [LoggerMessage(Level = LogLevel.Critical, Message = "StagingFlushWorker CRASHED")]
        public static partial void WorkerCrashed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "StagingFlushWorker drain pass failed")]
        public static partial void DrainPassFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "StagingFlushWorker final drain failed")]
        public static partial void FinalDrainFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Drained {Rows} rows via {Function} in {Elapsed}")]
        public static partial void ChunkDrained(ILogger logger, string function, long rows, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Information, Message = "Catch-up drain on shutdown: {Rows} rows drained, staging tables verified empty")]
        public static partial void CatchUpDrainedClean(ILogger logger, long rows);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Catch-up drain on shutdown: {Rows} rows drained but {Residue} rows REMAIN in staging — drain did not complete")]
        public static partial void CatchUpDrainedDirty(ILogger logger, long rows, long residue);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Final drain pass failed (attempt {Attempt}/{MaxAttempts}); will retry after backoff")]
        public static partial void FinalDrainPassFailed(ILogger logger, Exception ex, int attempt, int maxAttempts);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Staging residue probe failed; final drain status may be inaccurate")]
        public static partial void ResidueProbeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Staging residue: total={Total} detail={Detail}")]
        public static partial void StagingResidue(ILogger logger, string detail, long total);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Startup: detected {Rows} pre-existing rows in substrate.staging_* (left from a prior run). Draining to empty before producer emit.")]
        public static partial void StartupResidueDetected(ILogger logger, long rows);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Startup: {InitialRows} pre-existing residue rows drained ({DrainedRows} rows pulled). Substrate now coherent; producer may emit.")]
        public static partial void StartupResidueDrained(ILogger logger, long initialRows, long drainedRows);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Startup-residue drain pass failed (attempt {Attempt}/{MaxAttempts}); will retry with backoff")]
        public static partial void StartupDrainPassFailed(ILogger logger, Exception ex, int attempt, int maxAttempts);
    }
}
