using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for TOKEN_EMBEDDING tensors. Each row of the
/// embedding matrix is one token's full hidden_dim direction; this pass
/// emits one <c>embedding_position</c> entity per row, hashed by f64-
/// canonical row content. The recomposer reads back these rows at
/// distillation to materialize the target embedding tensor's bytes
/// losslessly (per Substrate Law #5: distillation = WHERE clause export
/// of a NEW student model from accumulated substrate knowledge).
///
/// Distinct from EmbeddingFireflyPass: that pass produces the 4D firefly
/// PHYSICALITY (Laplacian eigenmap + Gram-Schmidt + L2 norm) attached
/// to the SHARED bpe_token entity. This pass stores the FULL ROW CONTENT
/// (hidden_dim values) as the source-of-truth for distillation.
///
/// Sparsity: rows whose L2 magnitude is below SparsityThreshold are not
/// emitted (Substrate Law #11). Embedding rows almost never go below this
/// threshold in practice — the filter is defensive against pathological
/// post-pruning models.
///
/// Per docs/specs/decomposers/analysis-passes.md and
/// .claude/rules/35-inference-and-godel.md.
/// </summary>
internal sealed partial class EmbeddingPositionPass : IModelAnalysisPass
{
    public string PassId => "model.embedding_positions";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double SparsityThreshold = 1e-6;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public EmbeddingPositionPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        long totalEmitted = 0;
        long totalSkippedSparse = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            if (t.Classification.Role != TensorRole.TokenEmbedding)
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                continue;
            }

            int rows = (int)t.Info.Shape[0];
            int cols = (int)t.Info.Shape[1];
            if (rows < 1 || cols < 1) { continue; }

            Log.TensorStart(_logger, t.Info.Name, rows, cols);

            EntityHandle tensorHandle = session.Batch.AddEntity(t.ContentHash, "tensor");
            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);

            int emitted = 0;
            int skippedSparse = 0;
            for (int rowIdx = 0; rowIdx < rows; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();

                long rowOff = (long)rowIdx * cols;

                double sumSq = 0;
                for (int c = 0; c < cols; c++)
                {
                    double v = flat[rowOff + c];
                    sumSq += v * v;
                }
                if (Math.Sqrt(sumSq) < SparsityThreshold)
                {
                    skippedSparse++;
                    continue;
                }

                CanonicalSignatureBuilder b = new(context.Compute.Common, "epos");
                for (int c = 0; c < cols; c++)
                {
                    b.WriteDouble(flat[rowOff + c]);
                }
                byte[] posHash = b.Finalize();

                EntityHandle pos = session.Batch.AddEntity(posHash, "embedding_position");
                session.Batch.AddEntityModelSource(pos, context.Source.ModelSourceId);

                int vertexCount = (cols + 3) / 4;
                (double, double, double, double)[] verts = new (double, double, double, double)[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    int p = v * 4;
                    verts[v] = (
                        p     < cols ? flat[rowOff + p]     : 0.0,
                        p + 1 < cols ? flat[rowOff + p + 1] : 0.0,
                        p + 2 < cols ? flat[rowOff + p + 2] : 0.0,
                        p + 3 < cols ? flat[rowOff + p + 3] : 0.0);
                }
                session.Batch.AddPhysicalityLineString4d(pos, "contour", verts.AsSpan());

                session.Batch.AddEdge("has_embedding_position", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(null, t.EntityId, "source", 0),
                    new EdgeMemberSpec(pos, null, "target", 1),
                ]);

                session.Batch.AddSequence(parent: tensorHandle, child: pos, position: rowIdx, count: 1);

                emitted++;
                await session.MaybeFlushAsync(FlushThreshold, ct);
            }

            totalEmitted += emitted;
            totalSkippedSparse += skippedSparse;
            Log.TensorComplete(_logger, t.Info.Name, emitted, skippedSparse);
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkippedSparse);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-position] {Name} ({Rows}×{Cols}) starting")]
        public static partial void TensorStart(ILogger logger, string name, int rows, int cols);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-position] {Name} complete — {Emitted} positions emitted, {SkippedSparse} rows skipped sparse")]
        public static partial void TensorComplete(ILogger logger, string name, int emitted, int skippedSparse);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-position {ModelId}] pass complete — {TotalEmitted} embedding_position entities, {TotalSkippedSparse} rows skipped sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkippedSparse);
    }
}
