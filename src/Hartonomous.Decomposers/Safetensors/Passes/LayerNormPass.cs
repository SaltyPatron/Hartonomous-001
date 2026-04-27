using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for layer-norm-family tensors (LayerNorm, RmsNorm,
/// BatchNorm). Supersedes the layer-norm portion of OneDTensorPass.
///
/// OneDTensorPass attached the scale vector as a contour physicality on the
/// source tensor entity — that breaks cross-model dedup because tensor
/// entities are content+dtype+shape (different models don't dedupe even
/// with identical scale vectors). This pass emits a SEPARATE
/// <c>layer_norm_scale</c> entity hashed by f64-canonical scale-vector
/// content only. Same scale vector across models → ONE entity → Glicko-2
/// corroboration on layer-norm learned scales.
///
/// Edge <c>has_layer_norm_scale</c> from tensor → layer_norm_scale. The
/// scale vector content is also stored as a contour physicality on the
/// scale entity so the recomposer can scatter it back into a target
/// LN/RMS/Batch-norm tensor at distillation.
///
/// No sparsity filter: layer-norm scales carry learned meaning even when
/// values cluster near 1.0; there are no "dead" scales the way FFN has
/// dead neurons. Full vector content is identity.
///
/// Per docs/specs/decomposers/analysis-passes.md and
/// .claude/rules/35-inference-and-godel.md.
/// </summary>
internal sealed partial class LayerNormPass : IModelAnalysisPass
{
    public string PassId => "model.layer_norm";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int MaxLength = 1 << 20;
    private const int FlushThreshold = 1_000;

    private readonly ILogger _logger;

    public LayerNormPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int processed = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsNormRole(t.Classification.Role))
            {
                continue;
            }
            if (t.Info.Shape.Length != 1)
            {
                continue;
            }

            int length = (int)t.Info.Shape[0];
            if (length <= 0 || length > MaxLength) { continue; }

            double[] values = SafetensorsReader.ReadTensorAsDouble(t.Info);
            if (values.Length != length) { continue; }

            // Hash by f64 scale-vector content only (no dtype/shape).
            // Two models with identical scale vectors collapse to ONE entity.
            CanonicalSignatureBuilder b = new(context.Compute.Common, "lnsc");
            for (int i = 0; i < length; i++)
            {
                b.WriteDouble(values[i]);
            }
            byte[] scaleHash = b.Finalize();

            EntityHandle scale = session.Batch.AddEntity(scaleHash, "layer_norm_scale");
            session.Batch.AddEntityModelSource(scale, context.Source.ModelSourceId);

            // Pack scale vector as contour physicality so the recomposer can
            // reproduce the values losslessly at distillation.
            int vertexCount = (length + 3) / 4;
            (double, double, double, double)[] verts = new (double, double, double, double)[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                int p = v * 4;
                verts[v] = (
                    p     < length ? values[p]     : 0.0,
                    p + 1 < length ? values[p + 1] : 0.0,
                    p + 2 < length ? values[p + 2] : 0.0,
                    p + 3 < length ? values[p + 3] : 0.0);
            }
            session.Batch.AddPhysicalityLineString4d(scale, "contour", verts.AsSpan());

            // Edge from source tensor → scale entity. Layer index + norm
            // position (pre_attn/post_attn/etc.) recoverable from tensor's
            // tensor_tensor_role junction and in_layer edge when populated.
            session.Batch.AddEdge("has_layer_norm_scale", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(scale, null, "target", 1),
            ]);

            processed++;
            await session.MaybeFlushAsync(FlushThreshold, ct);
        }

        Log.PassComplete(_logger, context.Source.ModelId, processed);
    }

    private static bool IsNormRole(TensorRole role) => role switch
    {
        TensorRole.LayerNorm => true,
        TensorRole.RmsNorm => true,
        TensorRole.BatchNorm => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[layer-norm {ModelId}] complete — {Processed} layer_norm_scale entities emitted")]
        public static partial void PassComplete(ILogger logger, string modelId, int processed);
    }
}
