using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
/// Bulk-inserts edge-level significance rows in <c>semantic_relevance</c> and
/// <c>lexical_disambiguation</c> arenas, seeding each edge's μ from its
/// provenance trust prior. Subsequent arena plays (corroboration_strength,
/// frequency_significance) refine these values as inference traverses edges
/// and Glicko-2 updates accumulate evidence.
/// </para>
/// <para>
/// Trust-prior tiers (set by <c>0005_phase1_seed.up.sql</c>):
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

        // Drive the watermark primer (substrate.prime_unprimed_edges_chunk)
        // per arena until each returns 0. This:
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
        long rowsInserted = 0;
        const int ChunkSize = 4096;

        // Snapshot arena list once (open-vocabulary at start of run).
        List<int> arenas = [];
        await using (NpgsqlCommand arenaCmd = new(
                         "SELECT id FROM substrate.significance_context ORDER BY id", conn))
        await using (NpgsqlDataReader reader = await arenaCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                arenas.Add(reader.GetInt32(0));
            }
        }

        foreach (int arenaId in arenas)
        {
            ct.ThrowIfCancellationRequested();
            while (true)
            {
                await using NpgsqlCommand primeCmd = new(
                    "SELECT substrate.prime_unprimed_edges_chunk($1, $2)", conn);
                primeCmd.Parameters.Add(new NpgsqlParameter { Value = arenaId });
                primeCmd.Parameters.Add(new NpgsqlParameter { Value = ChunkSize });
                primeCmd.CommandTimeout = 600;

                object? raw = await primeCmd.ExecuteScalarAsync(ct);
                long primed = raw switch
                {
                    long l => l,
                    int i => i,
                    _ => 0L,
                };
                rowsInserted += primed;
                if (primed == 0)
                {
                    break;
                }
            }
        }

        sw.Stop();
        Log.Primed(_logger, rowsInserted, sw.Elapsed);

        // Surface progress to the standard reporter so the phase runner records the work.
        await reporter.ReportAsync(
            new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "SignificanceField",
                EntitiesCreated = 0,
                EdgesCreated = 0,
                CurrentFile = $"primed_edge_significance",
                CurrentBatch = (int) Math.Min(rowsInserted, int.MaxValue),
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
        [LoggerMessage(Level = LogLevel.Information, Message = "SignificanceField primed {RowCount:N0} edge-significance rows in {Elapsed}")]
        public static partial void Primed(ILogger logger, long rowCount, TimeSpan elapsed);
    }
}
