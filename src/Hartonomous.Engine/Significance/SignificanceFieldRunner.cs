using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Significance;

/// <summary>
/// SignificanceField phase runner — master plan item #61. Replaces the prior stub
/// where Phase.SignificanceField had no decomposer registered, so it ran as a
/// no-op and edges sat at the Glicko-2 default of 1500. With every edge scoring
/// identically, A* could not differentiate paths, and inference returned ranked
/// results that were actually all tied at 1500.
/// <para>
/// Bulk-inserts edge-level significance rows for every arena currently present
/// in <c>substrate.significance_context</c>, seeding each edge's μ from its
/// provenance trust prior. Subsequent arena plays refine these values as
/// inference traverses edges and Glicko-2 updates accumulate evidence.
/// </para>
/// <para>
/// Trust-prior tiers (set by canonical provenance seed data):
///   * authoritative_standard (Unicode, ISO 639): 100000
///   * academic_curated (Princeton WordNet): 95000
///   * academic_consortium (UD, OMW): 90000-92000
///   * community_curated (Wiktionary): 68000
///   * community_contributed (Tatoeba): 50000
///   * model_derived: per-model
///   * user_session: 1000
/// </para>
/// </summary>
public sealed partial class SignificanceFieldRunner : IDecomposer
{
    private readonly string _connectionString;
    private readonly ILogger<SignificanceFieldRunner> _logger;

    public SignificanceFieldRunner(string connectionString, ILogger<SignificanceFieldRunner> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public string ProvenanceCode => "system_computed";

    public string DisplayName => "SignificanceField — edge-level significance priming";

    public IReadOnlyList<Phase> Phases => [Phase.SignificanceField];

    public Task ValidateSourceAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        await using NpgsqlConnection conn = new(_connectionString);
        await conn.OpenAsync(ct);

        // Drive the phase-owned primer (substrate.prime_unprimed_edges_chunk)
        // per arena until each has scanned the current edge set. This:
        //   - Targets the actual schema: substrate.edge_significance
        //     (composite key: context_type_id, edge_type_id, edge_hash) NOT
        //     the obsolete substrate.significance table that was split into
        //     entity_significance + edge_significance per migration 0009.
        //   - Uses the compound trust-prior formula via the function:
        //       μ₀ = COALESCE(pea.initial_mu,
        //                     p.initial_mu × et.semantic_weight × p.derivation_decay)
        //       σ₀ = COALESCE(pea.initial_sigma, p.initial_sigma)
        //   - Cross-products against EVERY arena currently in
        //     significance_context (open-vocabulary, AP-1).
        //   - Reuses the watermark forward-scan shape (no anti-join, no merge
        //     join, no spill — the buggy LEFT JOIN/IS NULL/LIMIT plan that
        //     hit the PG18 batched-HashJoin path is gone).
        await using (NpgsqlCommand resetCmd = NpgsqlSubstrateCommand.CreateFunction(
                         conn,
                         SubstrateFunctionNames.ResetArenaPrimingState))
        {
            resetCmd.CommandTimeout = 600;
            await resetCmd.ExecuteScalarAsync(ct);
        }

        long rowsScanned = 0;
        const int ChunkSize = 4096;

        // Snapshot arena list once (open-vocabulary at start of run).
        List<int> arenas = [];
        await using (NpgsqlCommand arenaCmd = NpgsqlSubstrateCommand.CreateFunction(
                 conn,
                 SubstrateFunctionNames.SignificanceContextIds))
        await using (NpgsqlDataReader reader = await arenaCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                arenas.Add(reader.GetInt32(0));
            }
        }
        if (arenas.Count == 0)
        {
            throw new InvalidOperationException(
                "SignificanceField found zero significance arenas; phase cannot complete without edge significance arena coverage.");
        }

        foreach (int arenaId in arenas)
        {
            ct.ThrowIfCancellationRequested();
            while (true)
            {
                await using NpgsqlCommand primeCmd = NpgsqlSubstrateCommand.CreateFunction(
                    conn,
                    SubstrateFunctionNames.PrimeUnprimedEdgesChunk,
                    new object?[] { arenaId, ChunkSize });
                primeCmd.CommandTimeout = 600;

                object? raw = await primeCmd.ExecuteScalarAsync(ct);
                long scanned = raw switch
                {
                    long l => l,
                    int i => i,
                    _ => 0L,
                };
                rowsScanned += scanned;
                if (scanned == 0)
                {
                    break;
                }
            }
        }

        sw.Stop();
        Log.Primed(_logger, rowsScanned, sw.Elapsed);

        // Surface progress to the standard reporter so the phase runner records the work.
        await reporter.ReportAsync(
            new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "SignificanceField",
                EntitiesCreated = 0,
                EdgesCreated = 0,
                CurrentFile = $"primed_edge_significance",
                CurrentBatch = (int)Math.Min(rowsScanned, int.MaxValue),
            },
            ct);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "SignificanceField scanned {RowCount:N0} edge rows for significance priming in {Elapsed}")]
        public static partial void Primed(ILogger logger, long rowCount, TimeSpan elapsed);
    }
}
