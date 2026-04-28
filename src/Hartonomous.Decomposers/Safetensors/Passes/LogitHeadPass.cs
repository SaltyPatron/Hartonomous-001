using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for LOGIT_HEAD tensors. Each row of the logit
/// head matrix is one vocab token's projection direction (the substrate
/// signal that produces a specific token's logit at the model output).
/// Emits one <c>logit_projection</c> entity per row hashed by f64-canonical
/// row content. The recomposer reads these at distillation to materialize
/// a target logit head tensor.
///
/// Per docs/specs/decomposers/analysis-passes.md and
/// .claude/rules/35-inference-and-godel.md.
/// </summary>
internal sealed partial class LogitHeadPass : IModelAnalysisPass
{
    public string PassId => "model.logit_head";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double SparsityThreshold = 1e-6;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public LogitHeadPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.LogitHead)
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

            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double noiseFloor = PerRowContentPass.ComputeAdaptiveNoiseFloor(flat);

            int emitted = 0;
            int skippedSparse = 0;
            double[] thresholded = new double[cols];
            for (int rowIdx = 0; rowIdx < rows; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();

                long rowOff = (long)rowIdx * cols;

                double sumSq = 0;
                for (int c = 0; c < cols; c++)
                {
                    double raw = flat[rowOff + c];
                    double v = Math.Abs(raw) < noiseFloor ? 0.0 : raw;
                    thresholded[c] = v;
                    sumSq += v * v;
                }
                if (Math.Sqrt(sumSq) < SparsityThreshold)
                {
                    skippedSparse++;
                    continue;
                }

                CanonicalSignatureBuilder b = new(context.Compute.Common, "lgth");
                for (int c = 0; c < cols; c++)
                {
                    b.WriteDouble(thresholded[c]);
                }
                byte[] projHash = b.Finalize();

                EntityHandle proj = session.Batch.AddEntity(projHash, "logit_projection");
                session.Batch.AddEntityModelSource(proj, context.Source.ModelSourceId);

                int vertexCount = (cols + 3) / 4;
                (double, double, double, double)[] verts = new (double, double, double, double)[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    int p = v * 4;
                    verts[v] = (
                        p     < cols ? thresholded[p]     : 0.0,
                        p + 1 < cols ? thresholded[p + 1] : 0.0,
                        p + 2 < cols ? thresholded[p + 2] : 0.0,
                        p + 3 < cols ? thresholded[p + 3] : 0.0);
                }
                session.Batch.AddPhysicalityLineString4d(proj, "contour", verts.AsSpan());

                session.Batch.AddEdge("has_logit_projection", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(t.Entity, "source", 0),
                    new EdgeMemberSpec(proj, "target", 1),
                ]);


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
        [LoggerMessage(Level = LogLevel.Information, Message = "[logit-head] {Name} ({Rows}×{Cols}) starting")]
        public static partial void TensorStart(ILogger logger, string name, int rows, int cols);

        [LoggerMessage(Level = LogLevel.Information, Message = "[logit-head] {Name} complete — {Emitted} projections emitted, {SkippedSparse} rows skipped sparse")]
        public static partial void TensorComplete(ILogger logger, string name, int emitted, int skippedSparse);

        [LoggerMessage(Level = LogLevel.Information, Message = "[logit-head {ModelId}] pass complete — {TotalEmitted} logit_projection entities, {TotalSkippedSparse} rows skipped sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkippedSparse);
    }
}
