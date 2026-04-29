using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Background worker that primes substrate.edge_significance for newly-added
/// edges. Calls substrate.prime_unprimed_edges_chunk(arena_id, chunk_size) in
/// a loop, iterating arenas read from substrate.significance_context.
///
/// Decoupled from ingestion: never inside a producer transaction. The
/// per-batch synchronous prime call (the thing that crashed PG with stack
/// canary failure) is GONE. Priming runs continuously on its own connection,
/// catches up on whatever the streaming pipeline has landed in substrate.edge.
///
/// Open-vocabulary by design: re-reads the arena list every cycle so newly-
/// added arenas auto-backfill via the same code path. No hardcoded subset
/// (AP-1 — arena cherry-picking is forbidden).
/// </summary>
public sealed partial class BackgroundSignificancePrimer : IAsyncDisposable
{
    /// <summary>Edges primed per chunk per arena per cycle. Same as the
    /// staging-drain chunk size — 4096 is the validated upper bound that
    /// PG18 + hartonomous extension tolerate under the bulk-INSERT pressure
    /// of priming into a partitioned edge_significance target. Larger chunks
    /// reintroduce the partition tuple-router stack-canary crash class.</summary>
    private const int PrimeChunkRows = 4096;

    /// <summary>Catch-up uses the same chunk size as normal operation.</summary>
    private const int CatchUpPrimeChunkRows = PrimeChunkRows;

    /// <summary>Idle backoff when all arenas have nothing to prime.</summary>
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(2);

    /// <summary>Refresh arena list this often (open-vocabulary).</summary>
    private static readonly TimeSpan ArenaRefresh = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<BackgroundSignificancePrimer> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    private long _edgesPrimed;
    private long _idleCycles;
    private DateTime _lastArenaRefresh = DateTime.MinValue;
    private List<int> _arenas = new();

    public BackgroundSignificancePrimer(NpgsqlDataSource dataSource, ILogger<BackgroundSignificancePrimer> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public SignificancePrimerStats Stats => new()
    {
        EdgesPrimed = _edgesPrimed,
        IdleCycles  = _idleCycles,
        ArenaCount  = _arenas.Count,
    };

    public Task StartAsync()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("BackgroundSignificancePrimer already started");
        }
        _loop = Task.Run(() => RunAsync(_shutdown.Token));
        Log.PrimerStarted(_logger);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loop is null)
        {
            return;
        }

        // Stop the background loop first.
        _shutdown.Cancel();
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        _loop = null;

        // Catch-up: prime every remaining unprimed edge before exit. Same
        // rationale as StagingFlushWorker — the streaming sink may have
        // landed edges in substrate.edge that the primer hadn't observed
        // before cancellation. Without this, edges go un-primed across
        // process exits and A* over arenas degenerates to uniform-cost.
        long catchUpTotal = 0;
        try
        {
            while (true)
            {
                long pass = await PrimePassAsync(useCatchUpChunkSize: true, default).ConfigureAwait(false);
                if (pass == 0)
                {
                    break;
                }
                catchUpTotal += pass;
            }
        }
        catch (Exception ex)
        {
            Log.PrimePassFailed(_logger, ex);
        }
        if (catchUpTotal > 0)
        {
            Log.CatchUpPrimed(_logger, catchUpTotal);
        }

        Log.PrimerStopped(_logger, _edgesPrimed, _idleCycles);
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
                long primedThisCycle;
                try
                {
                    primedThisCycle = await PrimePassAsync(useCatchUpChunkSize: false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.PrimePassFailed(_logger, ex);
                    primedThisCycle = 0;
                }

                if (primedThisCycle == 0)
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
            }
        }
        catch (Exception ex)
        {
            Log.PrimerCrashed(_logger, ex);
            throw;
        }
    }

    private async Task<long> PrimePassAsync(bool useCatchUpChunkSize, CancellationToken ct)
    {
        int chunk = useCatchUpChunkSize ? CatchUpPrimeChunkRows : PrimeChunkRows;
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Refresh arena list periodically (open-vocabulary — new arenas
        // added during a long run get picked up automatically).
        if (DateTime.UtcNow - _lastArenaRefresh > ArenaRefresh)
        {
            await RefreshArenasAsync(conn, ct).ConfigureAwait(false);
        }

        long total = 0;
        foreach (int arenaId in _arenas)
        {
            ct.ThrowIfCancellationRequested();
            Stopwatch sw = Stopwatch.StartNew();
            await using NpgsqlCommand cmd = new(
                "SELECT substrate.prime_unprimed_edges_chunk($1, $2)", conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = arenaId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chunk });
            cmd.CommandTimeout = 1800;

            object? raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            long primed = raw switch
            {
                long l => l,
                int i => i,
                _ => 0L,
            };
            if (primed > 0)
            {
                Interlocked.Add(ref _edgesPrimed, primed);
                total += primed;
                Log.ArenaPrimed(_logger, arenaId, primed, sw.Elapsed);
            }
        }
        return total;
    }

    private async Task RefreshArenasAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        List<int> next = new();
        await using NpgsqlCommand cmd = new(
            "SELECT id FROM substrate.significance_context ORDER BY id", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            next.Add(reader.GetInt32(0));
        }
        _arenas = next;
        _lastArenaRefresh = DateTime.UtcNow;
        Log.ArenasRefreshed(_logger, _arenas.Count);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "BackgroundSignificancePrimer started")]
        public static partial void PrimerStarted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "BackgroundSignificancePrimer stopped: edges_primed={Edges} idle_cycles={Idle}")]
        public static partial void PrimerStopped(ILogger logger, long edges, long idle);

        [LoggerMessage(Level = LogLevel.Critical, Message = "BackgroundSignificancePrimer CRASHED")]
        public static partial void PrimerCrashed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "BackgroundSignificancePrimer prime pass failed")]
        public static partial void PrimePassFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Primed {Edges} edges in arena {ArenaId} in {Elapsed}")]
        public static partial void ArenaPrimed(ILogger logger, int arenaId, long edges, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Refreshed arena list: {ArenaCount} arenas")]
        public static partial void ArenasRefreshed(ILogger logger, int arenaCount);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Catch-up prime on shutdown: {Edges} edges primed until empty")]
        public static partial void CatchUpPrimed(ILogger logger, long edges);
    }
}

public sealed record SignificancePrimerStats
{
    public long EdgesPrimed { get; init; }
    public long IdleCycles { get; init; }
    public int ArenaCount { get; init; }
}
