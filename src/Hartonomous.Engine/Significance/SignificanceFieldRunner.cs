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

        // Idempotent: only insert rows that don't already exist.
        const string PrimeSql = @"
            INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
            SELECT NULL, e.id, sc.id, p.initial_mu, 350.0, 0.06, 0
            FROM substrate.edge e
            JOIN substrate.provenance p ON p.id = e.provenance_id
            CROSS JOIN substrate.significance_context sc
            WHERE sc.code IN ('semantic_relevance', 'lexical_disambiguation')
            ON CONFLICT DO NOTHING
        ";

        await using NpgsqlCommand cmd = new(PrimeSql, conn);
        cmd.CommandTimeout = 600; // bulk operation across all edges
        int rowsInserted = await cmd.ExecuteNonQueryAsync(ct);

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
                CurrentBatch = rowsInserted,
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
        public static partial void Primed(ILogger logger, int rowCount, TimeSpan elapsed);
    }
}
