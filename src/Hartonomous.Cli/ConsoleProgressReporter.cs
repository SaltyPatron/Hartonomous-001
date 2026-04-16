using Hartonomous.Core.Monitoring;

namespace Hartonomous.Cli;

internal sealed class ConsoleProgressReporter : IProgressReporter
{
    public Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct)
    {
        string file = snapshot.CurrentFile is not null ? $" [{snapshot.CurrentFile}]" : "";
        string batch = snapshot.CurrentBatch.HasValue ? $" batch {snapshot.CurrentBatch}" : "";
        Console.Write($"\r  [{snapshot.DecomposerCode}] {snapshot.CurrentPhase}:{batch} {snapshot.EntitiesCreated:N0} entities, {snapshot.EdgesCreated:N0} edges{file}    ");
        return Task.CompletedTask;
    }
}
