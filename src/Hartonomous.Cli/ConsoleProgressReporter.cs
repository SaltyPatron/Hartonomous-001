using Hartonomous.Core.Monitoring;

namespace Hartonomous.Cli;

internal sealed class ConsoleProgressReporter : IProgressReporter
{
    public Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct)
    {
        string source = snapshot.CurrentFile is not null
            ? $" source={snapshot.CurrentFile}"
            : "";
        string batch = snapshot.CurrentBatch.HasValue
            ? $" batch={snapshot.CurrentBatch}"
            : "";
        string duplicateText = snapshot.DuplicatesSkipped > 0
            ? $" duplicates_skipped={snapshot.DuplicatesSkipped:N0}"
            : "";
        string byteText = snapshot.BytesProcessed > 0
            ? $" bytes={snapshot.BytesProcessed:N0}"
            : "";

        Console.WriteLine(
            $"Progress: {snapshot.DecomposerCode} is in {snapshot.CurrentPhase}{batch}{source}; " +
            $"produced {snapshot.EntitiesCreated:N0} entities and {snapshot.EdgesCreated:N0} edges" +
            $"{duplicateText}{byteText}.");
        return Task.CompletedTask;
    }
}
