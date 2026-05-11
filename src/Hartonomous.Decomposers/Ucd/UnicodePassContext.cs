using Hartonomous.Core.Monitoring;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

internal sealed record UnicodePassContext(
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
