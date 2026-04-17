using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Errors;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Core.Decomposition;

public abstract partial class BaseDecomposer : IDecomposer
{
    private readonly DecomposerConfig _config;
    private readonly ILogger _logger;

    protected ILogger Logger => _logger;

    public abstract string ProvenanceCode { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<Phase> Phases { get; }

    protected BaseDecomposer(DecomposerConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public virtual Task ValidateSourceAsync(CancellationToken ct)
    {
        foreach (string path in GetSourcePaths())
        {
            if (!Path.Exists(path))
            {
                throw new SourceValidationException($"[{ProvenanceCode}] Source not found: {path}");
            }
        }
        return Task.CompletedTask;
    }

    public async Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        Log.DecompositionStarting(_logger, DisplayName);
        await DecomposeCoreAsync(pipeline, reporter, ct);
        Log.DecompositionCompleted(_logger, DisplayName);
    }

    protected abstract Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);

    protected abstract IReadOnlyList<string> GetSourcePaths();

    protected static byte[] ComputeHash(ReadOnlySpan<byte> content) => Blake3.Hash(content);

    protected static byte[] ComputeHash(string content)
        => Blake3.Hash(Encoding.UTF8.GetBytes(content).AsSpan());

    protected static byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes)
    {
        byte[] concat = new byte[childHashes.Length * Blake3.HashLen];
        for (int i = 0; i < childHashes.Length; i++)
        {
            childHashes[i].CopyTo(concat.AsSpan(i * Blake3.HashLen));
        }
        return Merkle.Hash(concat.AsSpan());
    }

    protected static byte[] ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<byte[]> participantHashes)
    {
        byte[] buffer = new byte[4 + participantHashes.Length * 32];
        BitConverter.TryWriteBytes(buffer, edgeTypeId);
        for (int i = 0; i < participantHashes.Length; i++)
        {
            participantHashes[i].CopyTo(buffer.AsSpan(4 + i * 32));
        }
        return ComputeHash(buffer.AsSpan());
    }

    protected static async Task SubmitAndReportAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        IIngestionBatch batch,
        ProgressSnapshot snapshot,
        CancellationToken ct)
    {
        await pipeline.SubmitBatchAsync(batch, ct);
        await reporter.ReportAsync(snapshot, ct);
    }

    protected int BatchSize => _config.BatchSize;

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Starting decomposition: {Decomposer}")]
        public static partial void DecompositionStarting(ILogger logger, string decomposer);

        [LoggerMessage(Level = LogLevel.Information, Message = "Completed decomposition: {Decomposer}")]
        public static partial void DecompositionCompleted(ILogger logger, string decomposer);
    }
}
