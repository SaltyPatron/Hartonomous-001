using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §I.Normalization + §IV. Records every
/// Normalization-primitive tensor's γ/β contour as physicality on the tensor
/// entity itself, plus fires entity_significance with attestation_type
/// <c>model_layer_norm_evidence</c>. No edges between content entities — norm
/// scale is per-tensor parameter data, not a token-pair relationship.
///
/// Same pattern handles LayerNorm γ, LayerNorm β, RMSNorm γ, BatchNorm γ/β/
/// running_mean/running_var (each as its own tensor entity with its own contour).
///
/// Synthesizers reverse this: read the contour back from substrate, average
/// across consensus sources, project into target architecture's norm tensor.
/// </summary>
internal sealed partial class NormalizationPrimitivePass : IModelAnalysisPass
{
    public string PassId => "primitive.normalization";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public NormalizationPrimitivePass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        long emitted = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            if (!context.TensorClassifications.TryGetValue(t, out TensorClassification? cls)) { continue; }
            if (cls.Primitive != PrimitiveKind.Normalization) { continue; }
            if (t.Info.Shape.Length != 1) { continue; }

            int len = (int)t.Info.Shape[0];
            if (len < 1) { continue; }

            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);

            int vertexCount = (len + 3) / 4;
            (double, double, double, double)[] verts = new (double, double, double, double)[vertexCount];
            for (int v = 0; v < vertexCount; v++)
            {
                int p = v * 4;
                verts[v] = (
                    p     < len ? flat[p]     : 0.0,
                    p + 1 < len ? flat[p + 1] : 0.0,
                    p + 2 < len ? flat[p + 2] : 0.0,
                    p + 3 < len ? flat[p + 3] : 0.0);
            }

            // Tensor γ-scale is the tensor's own internal real-coord shape
            // (per-feature scale values laid out as a LINESTRINGZM in
            // contiguous 4-tuples). It is NOT a trajectory through other
            // entities; it is the tensor entity's canonical structural
            // fingerprint. Route to physicality_entity_shape (id 15) via
            // AddEntityShape — distinct from the content_trajectory
            // mantissa-packed surface that text/audio/image compositions
            // emit into. Fréchet shape matching across tensors with
            // similar γ-scale profiles becomes a structural-shape query
            // against the entity_shape partition.
            Hartonomous.Core.Geometry.Point4D[] shapeVerts =
                new Hartonomous.Core.Geometry.Point4D[verts.Length];
            for (int v = 0; v < verts.Length; v++)
            {
                (double x1, double x2, double x3, double x4) = verts[v];
                shapeVerts[v] = new Hartonomous.Core.Geometry.Point4D(x1, x2, x3, x4);
            }
            session.Batch.AddEntityShape(t.Entity, shapeVerts.AsSpan());
            session.Batch.AddSignificance(
                t.Entity, "model_trust", ModelDerivedTrustMu, "model_layer_norm_evidence");
            session.Batch.AddEntityModelSource(t.Entity, context.Source.ModelSourceId);

            emitted++;
            if (emitted % FlushThreshold == 0)
            {
                await session.MaybeFlushAsync(FlushThreshold, ct);
            }
        }

        Log.Complete(_logger, context.Source.ModelId, emitted);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[normalization-primitive {ModelId}] complete — {Emitted} norm tensors recorded")]
        public static partial void Complete(ILogger logger, string modelId, long emitted);
    }
}
