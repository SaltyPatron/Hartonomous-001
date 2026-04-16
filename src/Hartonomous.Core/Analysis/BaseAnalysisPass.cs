using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Core.Analysis;

public abstract partial class BaseAnalysisPass : IAnalysisPass
{
    private readonly ILogger _logger;

    public abstract string PassId { get; }
    public abstract Modality Modality { get; }
    public abstract IReadOnlyList<string> Dependencies { get; }
    public abstract IReadOnlyList<string> InputEntityTypes { get; }

    protected BaseAnalysisPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        Log.PassStarting(_logger, PassId);
        await ExecuteCoreAsync(pipeline, reporter, ct);
        Log.PassCompleted(_logger, PassId);
    }

    protected abstract Task ExecuteCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Starting pass: {PassId}")]
        public static partial void PassStarting(ILogger logger, string passId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Completed pass: {PassId}")]
        public static partial void PassCompleted(ILogger logger, string passId);
    }
}
