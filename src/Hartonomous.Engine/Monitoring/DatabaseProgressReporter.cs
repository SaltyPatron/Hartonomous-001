using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Engine.Monitoring;

public sealed partial class DatabaseProgressReporter : IProgressReporter
{
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<DatabaseProgressReporter> _logger;

    public DatabaseProgressReporter(ISessionStore sessionStore, ILogger<DatabaseProgressReporter> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    public async Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct)
    {
        await _sessionStore.ReportProgressAsync(snapshot, ct);

        Log.ProgressReported(_logger, snapshot.DecomposerCode, snapshot.CurrentBatch ?? 0, snapshot.EntitiesCreated);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Progress reported: {DecomposerCode} batch {Batch} ({Entities} entities)")]
        public static partial void ProgressReported(ILogger logger, string decomposerCode, int batch, long entities);
    }
}
