using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Shared per-row-content emission helper used by FfnNeuronPass,
/// EmbeddingPositionPass, LogitHeadPass, AttentionComponentPass,
/// MoERouteDirectionPass, MoeExpertNeuronPass, etc. Each is the same
/// pattern: iterate rows of a 2-D Track-2 tensor, sparsity-filter by
/// row L2, hash by f64 row content (cross-model dedup), emit per-role
/// entity + has_role edge + sequence row + contour physicality with
/// row content for the recomposer.
///
/// Centralizing the loop avoids drift across passes — every per-row
/// pass uses the identical hashing canonicalization, the same sparsity
/// threshold conventions, and the same placement encoding via
/// substrate.sequence. Per-pass differences are: tensor-role filter,
/// canonical signature kind tag, entity type code, edge type code.
/// </summary>
internal static class PerRowContentPass
{
    public const double DefaultSparsityThreshold = 1e-6;
    public const int DefaultFlushThreshold = 5_000;

    public static async Task<(long Emitted, long SkippedSparse)> RunPerRowAsync(
        ModelPassContext context,
        IPassSession session,
        TensorHandle t,
        string canonicalKindTag4,
        string entityTypeCode,
        string edgeTypeCode,
        double sparsityThreshold,
        int flushThreshold,
        CancellationToken ct)
    {
        if (t.Info.Shape.Length != 2)
        {
            return (0, 0);
        }

        int rows = (int)t.Info.Shape[0];
        int cols = (int)t.Info.Shape[1];
        if (rows < 1 || cols < 1)
        {
            return (0, 0);
        }

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
            if (Math.Sqrt(sumSq) < sparsityThreshold)
            {
                skippedSparse++;
                continue;
            }

            CanonicalSignatureBuilder b = new(context.Compute.Common, canonicalKindTag4);
            for (int c = 0; c < cols; c++)
            {
                b.WriteDouble(flat[rowOff + c]);
            }
            byte[] hash = b.Finalize();

            EntityHandle row = session.Batch.AddEntity(hash, entityTypeCode);
            session.Batch.AddEntityModelSource(row, context.Source.ModelSourceId);

            // Row content as contour physicality so the recomposer can scatter
            // the values back into a target tensor at distillation.
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
            session.Batch.AddPhysicalityLineString4d(row, "contour", verts.AsSpan());

            session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(row, null, "target", 1),
            ]);

            session.Batch.AddSequence(parent: tensorHandle, child: row, position: rowIdx, count: 1);

            emitted++;
            await session.MaybeFlushAsync(flushThreshold, ct);
        }

        return (emitted, skippedSparse);
    }

    /// <summary>
    /// Rank-N variant: flattens all trailing dimensions into a single "cols"
    /// stride per outer-index unit. Used by per-output-channel passes for
    /// 4-D conv kernels (out_c, in_c, kh, kw → outer=out_c, cols=in_c*kh*kw),
    /// per-block diffusion / conformer tensors, and audio codec stages.
    /// Otherwise identical to RunPerRowAsync — same canonical hashing,
    /// sparsity threshold, sequence placement, edge typing.
    /// </summary>
    public static async Task<(long Emitted, long SkippedSparse)> RunPerOuterIndexAsync(
        ModelPassContext context,
        IPassSession session,
        TensorHandle t,
        string canonicalKindTag4,
        string entityTypeCode,
        string edgeTypeCode,
        double sparsityThreshold,
        int flushThreshold,
        CancellationToken ct)
    {
        if (t.Info.Shape.Length < 2)
        {
            return (0, 0);
        }

        int outer = (int)t.Info.Shape[0];
        long cols64 = 1;
        for (int d = 1; d < t.Info.Shape.Length; d++)
        {
            cols64 *= t.Info.Shape[d];
        }
        if (outer < 1 || cols64 < 1 || cols64 > int.MaxValue)
        {
            return (0, 0);
        }
        int cols = (int)cols64;

        EntityHandle tensorHandle = session.Batch.AddEntity(t.ContentHash, "tensor");
        double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);

        int emitted = 0;
        int skippedSparse = 0;
        for (int outerIdx = 0; outerIdx < outer; outerIdx++)
        {
            ct.ThrowIfCancellationRequested();

            long rowOff = (long)outerIdx * cols;

            double sumSq = 0;
            for (int c = 0; c < cols; c++)
            {
                double v = flat[rowOff + c];
                sumSq += v * v;
            }
            if (Math.Sqrt(sumSq) < sparsityThreshold)
            {
                skippedSparse++;
                continue;
            }

            CanonicalSignatureBuilder b = new(context.Compute.Common, canonicalKindTag4);
            for (int c = 0; c < cols; c++)
            {
                b.WriteDouble(flat[rowOff + c]);
            }
            byte[] hash = b.Finalize();

            EntityHandle unit = session.Batch.AddEntity(hash, entityTypeCode);
            session.Batch.AddEntityModelSource(unit, context.Source.ModelSourceId);

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
            session.Batch.AddPhysicalityLineString4d(unit, "contour", verts.AsSpan());

            session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(unit, null, "target", 1),
            ]);

            session.Batch.AddSequence(parent: tensorHandle, child: unit, position: outerIdx, count: 1);

            emitted++;
            await session.MaybeFlushAsync(flushThreshold, ct);
        }

        return (emitted, skippedSparse);
    }
}
