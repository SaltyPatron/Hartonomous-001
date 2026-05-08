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

    /// <summary>
    /// Computes the noise floor for ONE tensor adaptively from its own
    /// value-magnitude distribution. Substrate Law #11: gradient jitter is
    /// not content. The "what is jitter" boundary is per-tensor — an
    /// embedding tensor's noise floor is a different magnitude than an FFN
    /// tensor's noise floor than a layer-norm scale's. A single repo-wide
    /// constant is conventional pattern-match wrong.
    ///
    /// The measurement: floor = noiseFraction · mean(|x|) over all elements
    /// of the tensor. Single-pass O(n), no sort, no histogram. Values whose
    /// |x| is below this floor are gradient jitter relative to the tensor's
    /// own scale — they get written as 0 into the row's content hash AND
    /// the contour physicality, so two models whose meaningful signal is
    /// the same but whose post-training jitter differs collapse to ONE
    /// entity.
    ///
    /// noiseFraction defaults to 0.10 — values that are less than 10% of
    /// the tensor's own average magnitude are noise. This is per-tensor
    /// adaptive, not a global constant.
    /// </summary>
    public static double ComputeAdaptiveNoiseFloor(double[] flat, double noiseFraction = 0.10)
    {
        if (flat.Length == 0) { return 0.0; }
        double sumAbs = 0.0;
        for (int i = 0; i < flat.Length; i++)
        {
            sumAbs += Math.Abs(flat[i]);
        }
        double meanAbs = sumAbs / flat.Length;
        return meanAbs * noiseFraction;
    }


    public static async Task<(long Emitted, long SkippedSparse)> RunPerRowAsync(
        ModelPassContext context,
        IPassSession session,
        TensorHandle t,
        string canonicalKindTag4,
        string entityTypeCode,
        string edgeTypeCode,
        double sparsityThreshold,
        int flushThreshold,
        CancellationToken ct,
        double? noiseFloorOverride = null)
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

        double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
        // Per-tensor adaptive noise floor (Substrate Law #11) — computed from
        // THIS tensor's own |x| distribution, not a hardcoded constant.
        double noiseFloor = noiseFloorOverride ?? ComputeAdaptiveNoiseFloor(flat);

        int emitted = 0;
        int skippedSparse = 0;
        // Reusable thresholded-row buffer.
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
            if (Math.Sqrt(sumSq) < sparsityThreshold)
            {
                skippedSparse++;
                continue;
            }

            CanonicalSignatureBuilder b = new(context.Compute.Common, canonicalKindTag4);
            for (int c = 0; c < cols; c++)
            {
                b.WriteDouble(thresholded[c]);
            }
            byte[] hash = b.Finalize();

            EntityHandle row = session.Batch.AddEntity(hash, entityTypeCode);
            session.Batch.AddEntityModelSource(row, context.Source.ModelSourceId);

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
            session.Batch.AddPhysicalityLineString4d(row, "contour", verts.AsSpan());

            // Per-role-unit edge carries model_per_role_unit_circuit
            // attestation_type — this is structural model evidence (Track 2:
            // per-role units are what carries the model's learned function).
            // The model_trust arena's mu derives from row energy (sumSq) so
            // higher-magnitude rows get higher initial Glicko-2 priors,
            // reflecting that they encode stronger learned signal.
            double rowEnergy = Math.Sqrt(sumSq);
            // Map to mu band 1500..2500 via row energy / max-row energy
            // approximation (clamp to keep within Glicko-2 sane range).
            double mu = Math.Clamp(1500.0 + (rowEnergy * 100.0), 1500.0, 2500.0);
            EdgeSignificanceSpec[] sigSpecs =
            [
                new EdgeSignificanceSpec("model_trust", "model_per_role_unit_circuit", mu),
                new EdgeSignificanceSpec("attention_pattern_confidence", "model_per_role_unit_circuit", mu),
            ];
            session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
                [
                    new EdgeMemberSpec(t.Entity, "source", 0),
                    new EdgeMemberSpec(row, "target", 1),
                ],
                sigSpecs);

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
        CancellationToken ct,
        double? noiseFloorOverride = null)
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

        double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
        // Per-tensor adaptive noise floor — same Law #11 rule as RunPerRowAsync.
        double noiseFloor = noiseFloorOverride ?? ComputeAdaptiveNoiseFloor(flat);

        int emitted = 0;
        int skippedSparse = 0;
        double[] thresholded = new double[cols];
        for (int outerIdx = 0; outerIdx < outer; outerIdx++)
        {
            ct.ThrowIfCancellationRequested();

            long rowOff = (long)outerIdx * cols;

            double sumSq = 0;
            for (int c = 0; c < cols; c++)
            {
                double raw = flat[rowOff + c];
                double v = Math.Abs(raw) < noiseFloor ? 0.0 : raw;
                thresholded[c] = v;
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
                b.WriteDouble(thresholded[c]);
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
                    p     < cols ? thresholded[p]     : 0.0,
                    p + 1 < cols ? thresholded[p + 1] : 0.0,
                    p + 2 < cols ? thresholded[p + 2] : 0.0,
                    p + 3 < cols ? thresholded[p + 3] : 0.0);
            }
            session.Batch.AddPhysicalityLineString4d(unit, "contour", verts.AsSpan());

            // Same model_per_role_unit_circuit attestation_type as
            // RunPerRowAsync — rank-N tensors (conv kernels, codec stages,
            // per-block diffusion / conformer) are still per-role units of
            // Track 2 transformation tensors. Initial mu scales with row
            // energy.
            double unitEnergy = Math.Sqrt(sumSq);
            double mu = Math.Clamp(1500.0 + (unitEnergy * 100.0), 1500.0, 2500.0);
            EdgeSignificanceSpec[] sigSpecs =
            [
                new EdgeSignificanceSpec("model_trust", "model_per_role_unit_circuit", mu),
                new EdgeSignificanceSpec("attention_pattern_confidence", "model_per_role_unit_circuit", mu),
            ];
            session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
                [
                    new EdgeMemberSpec(t.Entity, "source", 0),
                    new EdgeMemberSpec(unit, "target", 1),
                ],
                sigSpecs);


            emitted++;
            await session.MaybeFlushAsync(flushThreshold, ct);
        }

        return (emitted, skippedSparse);
    }
}
