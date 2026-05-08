using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Stores the values of every 1-D tensor (shape.Length == 1) as a contour
/// physicality on the tensor entity. Covers layer norms (RMS / Layer / Batch),
/// bias vectors, mel filterbanks, codebook scales, FP8 scales — anything that
/// presents as a flat vector. Without this pass, 1-D tensors zero-fill on
/// export because no per-position decomposition emits content for them.
///
/// Encoding: the f64 values pack into a linestring4d as 4-tuples, padded to
/// a 4-tuple boundary with deterministic zeros. The recomposer reads
/// coords[0..length] as the values and writes them straight back into the
/// target tensor's wire bytes.
/// </summary>
internal sealed partial class OneDTensorPass : IModelAnalysisPass
{
    public string PassId => "model.one_d_tensor";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int MaxLength = 1 << 20;   // 1M elements upper bound (LN/RMS rarely exceed hidden_size = 16K).

    private readonly ILogger _logger;

    public OneDTensorPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int processed = 0;
        int skipped = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Info.Shape.Length != 1) { continue; }

            long lengthLong = t.Info.Shape[0];
            if (lengthLong <= 0 || lengthLong > MaxLength)
            {
                Log.SkipOutOfRange(_logger, t.Info.Name, lengthLong);
                skipped++;
                continue;
            }

            int length = (int)lengthLong;
            double[] values = SafetensorsReader.ReadTensorAsDouble(t.Info);
            if (values.Length != length)
            {
                Log.SkipShapeMismatch(_logger, t.Info.Name, length, values.Length);
                skipped++;
                continue;
            }

            int vertexCount = (length + 3) / 4;
            (double, double, double, double)[] verts = new (double, double, double, double)[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                int b = v * 4;
                verts[v] = (
                    b < length     ? values[b]     : 0.0,
                    b + 1 < length ? values[b + 1] : 0.0,
                    b + 2 < length ? values[b + 2] : 0.0,
                    b + 3 < length ? values[b + 3] : 0.0);
            }
            // Re-add the tensor entity by content hash so this batch has a
            // handle; server-side dedup preserves the existing hash identity.
            EntityHandle tensorH = session.Batch.AddEntity(t.ContentHash, "tensor");
            session.Batch.AddPhysicalityLineString4d(tensorH, "contour", verts.AsSpan());
            processed++;
        }
        Log.PassComplete(_logger, context.Source.ModelId, processed, skipped);
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[one-d-tensor] {Name} length {Length} out of range; skipped")]
        public static partial void SkipOutOfRange(ILogger logger, string name, long length);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[one-d-tensor] {Name} shape {Shape} != decoded length {Decoded}; skipped")]
        public static partial void SkipShapeMismatch(ILogger logger, string name, int shape, int decoded);

        [LoggerMessage(Level = LogLevel.Information, Message = "[one-d-tensor {ModelId}] complete — {Processed} 1-D tensors stored, {Skipped} skipped")]
        public static partial void PassComplete(ILogger logger, string modelId, int processed, int skipped);
    }
}
