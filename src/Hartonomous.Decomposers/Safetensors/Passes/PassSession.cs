using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Default <see cref="IPassSession"/>. Threads a single batch lifecycle through
/// every pass invocation in a single model run. Auto-flushes through the
/// pipeline when the batch grows past <see cref="IIngestionBatch.EntityCount"/>
/// or <see cref="IIngestionBatch.EdgeCount"/> threshold; emits a
/// <see cref="ProgressSnapshot"/> on every commit so the monitor view stays
/// live during long passes.
/// </summary>
internal sealed partial class PassSession : IPassSession
{
    private readonly IIngestionPipeline _pipeline;
    private readonly IProgressReporter _reporter;
    private readonly ModelPassContext _context;
    private readonly ILogger _logger;
    private readonly string _passId;

    private IIngestionBatch _batch;
    private EntityHandle _modelEntity;
    private long _entitiesCreated;
    private long _edgesCreated;
    private int _batchNum;

    public PassSession(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        ModelPassContext context,
        ILogger logger,
        string passId)
    {
        _pipeline = pipeline;
        _reporter = reporter;
        _context = context;
        _logger = logger;
        _passId = passId;
        _batch = pipeline.CreateBatch(_context.ProvenanceCode);
        _modelEntity = ReseedModelEntity(_batch);
    }

    public IIngestionBatch Batch => _batch;

    public EntityHandle ModelEntity => _modelEntity;

    public long EntitiesCreated => _entitiesCreated + _batch.EntityCount;

    public long EdgesCreated => _edgesCreated + _batch.EdgeCount;

    public async Task MaybeFlushAsync(int threshold, CancellationToken ct)
    {
        if (_batch.EntityCount >= threshold || _batch.EdgeCount >= threshold)
        {
            await FlushAsync(ct);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        int entitiesThisBatch = _batch.EntityCount;
        int edgesThisBatch = _batch.EdgeCount;
        if (entitiesThisBatch == 0 && edgesThisBatch == 0)
        {
            return;
        }

        _batchNum++;
        Log.BatchSubmit(_logger, _passId, _batchNum, entitiesThisBatch, edgesThisBatch);
        await _pipeline.SubmitBatchAsync(_batch, ct);
        _entitiesCreated += entitiesThisBatch;
        _edgesCreated += edgesThisBatch;

        await _reporter.ReportAsync(new ProgressSnapshot
        {
            DecomposerCode = _context.ProvenanceCode,
            CurrentPhase = $"pass:{_passId}",
            EntitiesCreated = _entitiesCreated,
            EdgesCreated = _edgesCreated,
            CurrentFile = _context.Source.ModelId,
            CurrentBatch = _batchNum,
        }, ct);

        _batch = _pipeline.CreateBatch(_context.ProvenanceCode);
        _modelEntity = ReseedModelEntity(_batch);
    }

    private EntityHandle ReseedModelEntity(IIngestionBatch batch)
    {
        // The model_architecture entity must exist in every batch so passes can
        // attach edges/junctions to it without round-tripping. Content addressing
        // means re-emitting the same hash dedupes server-side; the junction insert
        // is idempotent via ON CONFLICT DO NOTHING in 0021.
        EntityHandle handle = batch.AddEntity(new Hash32(_context.Architecture.ContentHash), "model_architecture");
        batch.AddEntityModelSource(handle, _context.Source.ModelSourceId);
        return handle;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Pass {PassId} batch {BatchNum} submitting: {Entities}E {Edges}Ed")]
        public static partial void BatchSubmit(ILogger logger, string passId, int batchNum, int entities, int edges);
    }
}
