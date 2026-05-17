using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Context shared by every Unicode pass. Carries:
///  - Pipeline: the canonical producer surface (CreateBatch → AddEntity/AddEdge → SubmitBatchAsync).
///    New producer passes (the rewrite target) use this.
///  - DataSource / Connection: legacy NpgsqlConnection access for passes still
///    dispatching to populate_*_from_ext SRFs. Removed once all 13 passes are
///    rewritten as producers (Step E completion gate).
/// </summary>
internal sealed record UnicodePassContext(
    IIngestionPipeline Pipeline,
    NpgsqlDataSource DataSource,
    NpgsqlConnection Connection,
    IProgressReporter Reporter,
    string ProvenanceCode,
    string SourceDirectory,
    ILogger Logger)
{
    public Task ReportAsync(string passId, long entities, long edges, CancellationToken ct)
        => Reporter.ReportAsync(new ProgressSnapshot
        {
            DecomposerCode = ProvenanceCode,
            CurrentPhase = $"pass:{passId}",
            EntitiesCreated = entities,
            EdgesCreated = edges,
            CurrentFile = SourceDirectory,
        }, ct);
}
