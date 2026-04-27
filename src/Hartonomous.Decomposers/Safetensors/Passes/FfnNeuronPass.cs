using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for FFN transformation tensors. The first concrete
/// realization of P5 from docs/build-plan.md "Corrected execution order" — per-
/// role passes are the substrate's matmul-replacement: a Track-2 transformation
/// tensor's row IS the learned function of one neuron; decomposing the tensor
/// into row-entities + typed edges with significance IS the substrate's encoding
/// of the model's inference behavior. The recomposer scatters these per-role
/// units into target tensors at distillation per Substrate Law #5.
///
/// Applies to FFN_GATE, FFN_UP, FFN_DOWN, MOE_SHARED_EXPERT roles. Each row
/// (FFN_UP/GATE: output direction = one neuron; FFN_DOWN: input row = one
/// mixing direction) emits one <c>ffn_neuron</c> entity hashed by f64-canonical
/// row content. Same row content across models collapses to ONE entity →
/// cross-model FFN-neuron corroboration via Glicko-2 on the shared entity.
///
/// Sparsity: rows whose L2 magnitude is below <c>SparsityThreshold</c> are
/// not emitted (Substrate Law #11). They encode no learned function.
///
/// Placement: the (tensor, ffn_neuron, row_index) triple is recorded via
/// substrate.sequence — `parent=tensor_entity, child=neuron_entity,
/// ordinal_position=row_index`. The projection role (gate/up/down/shared) is
/// recoverable from the tensor's tensor_tensor_role junction. The layer index
/// is recoverable from the tensor's in_layer edge (when populated).
///
/// Per docs/specs/decomposers/analysis-passes.md and
/// .claude/rules/35-inference-and-godel.md § "The invention".
/// </summary>
internal sealed partial class FfnNeuronPass : IModelAnalysisPass
{
    public string PassId => "model.ffn_neurons";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    // Rows whose L2 norm is below this threshold are noise (gradient jitter,
    // dead neurons, post-pruning sparse rows). Substrate Law #11: don't store
    // what doesn't carry meaning. The threshold is intentionally conservative
    // to preserve weak-but-real signal; tighten per-architecture if needed.
    private const double SparsityThreshold = 1e-6;

    // Noise floor is computed per-tensor from the tensor's own |x|
    // distribution via PerRowContentPass.ComputeAdaptiveNoiseFloor (Law #11).
    // No hardcoded floor — each FFN tensor's jitter boundary is its own.

    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public FfnNeuronPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int tensorOrdinal = 0;
        long totalEmitted = 0;
        long totalSkippedSparse = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorOrdinal++;

            if (!IsFfnRole(t.Classification.Role))
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                Log.SkipNon2D(_logger, t.Info.Name, t.Info.Shape.Length);
                continue;
            }

            int rows = (int)t.Info.Shape[0];
            int cols = (int)t.Info.Shape[1];
            if (rows < 1 || cols < 1)
            {
                continue;
            }

            string roleCode = t.Classification.Role.ToCode();
            Log.TensorStart(_logger, tensorOrdinal, t.Info.Name, roleCode, rows, cols);

            // Stream the tensor as f64; for each row, hash the row content,
            // emit ffn_neuron entity, attach has_ffn_neuron edge, record
            // placement via substrate.sequence (ordinal = row_index).
            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            // Per-tensor adaptive noise floor (Substrate Law #11).
            double noiseFloor = PerRowContentPass.ComputeAdaptiveNoiseFloor(flat);

            int emitted = 0;
            int skippedSparse = 0;
            double[] thresholded = new double[cols];
            for (int rowIdx = 0; rowIdx < rows; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();

                long rowOff = (long)rowIdx * cols;

                // Threshold THIS row against the per-tensor noise floor —
                // jitter goes to 0, signal stays. Then compute L2 of the
                // thresholded row; rows that are entirely jitter get
                // skipped entirely (sparsity-honest).
                double sumSq = 0;
                for (int c = 0; c < cols; c++)
                {
                    double raw = flat[rowOff + c];
                    double v = Math.Abs(raw) < noiseFloor ? 0.0 : raw;
                    thresholded[c] = v;
                    sumSq += v * v;
                }
                double l2 = Math.Sqrt(sumSq);
                if (l2 < SparsityThreshold)
                {
                    skippedSparse++;
                    continue;
                }

                // Hash by THRESHOLDED row content. Two FFN rows that mean
                // the same thing collapse to one entity even when their
                // post-training jitter differs.
                CanonicalSignatureBuilder b = new(context.Compute.Common, "ffnn");
                for (int c = 0; c < cols; c++)
                {
                    b.WriteDouble(thresholded[c]);
                }
                byte[] neuronHash = b.Finalize();

                EntityHandle neuron = session.Batch.AddEntity(neuronHash, "ffn_neuron");
                session.Batch.AddEntityModelSource(neuron, context.Source.ModelSourceId);

                // Contour physicality stores the THRESHOLDED row — substrate
                // stores no jitter, recomposer scatters thresholded values.
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
                session.Batch.AddPhysicalityLineString4d(neuron, "contour", verts.AsSpan());

                // Edge from tensor → neuron, marking the relation type.
                session.Batch.AddEdge("has_ffn_neuron", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(null, t.EntityId, "source", 0),
                    new EdgeMemberSpec(neuron, null, "target", 1),
                ]);

                // Placement: which row of THIS tensor this neuron came from.
                // substrate.sequence carries ordinal_position as INT (32-bit
                // signed) — handles vocab-scale hidden dims comfortably.
                session.Batch.AddSequence(
                    parentEntityId: t.EntityId,
                    child:  neuron,
                    position: rowIdx,
                    count: 1);

                emitted++;
                await session.MaybeFlushAsync(FlushThreshold, ct);
            }

            totalEmitted += emitted;
            totalSkippedSparse += skippedSparse;
            Log.TensorComplete(_logger, t.Info.Name, emitted, skippedSparse);
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkippedSparse);
    }

    private static bool IsFfnRole(TensorRole role) => role switch
    {
        TensorRole.FfnGate => true,
        TensorRole.FfnUp => true,
        TensorRole.FfnDown => true,
        TensorRole.MoeSharedExpert => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-neuron {Idx}] {Name} ({Role}, {Rows}×{Cols}) starting")]
        public static partial void TensorStart(ILogger logger, int idx, string name, string role, int rows, int cols);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-neuron] {Name} complete — {Emitted} neurons emitted, {SkippedSparse} rows skipped (L2 below sparsity threshold)")]
        public static partial void TensorComplete(ILogger logger, string name, int emitted, int skippedSparse);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[ffn-neuron] {Name} not 2-D (rank={Rank}); skipped")]
        public static partial void SkipNon2D(ILogger logger, string name, int rank);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-neuron {ModelId}] pass complete — {TotalEmitted} ffn_neuron entities emitted, {TotalSkippedSparse} rows skipped sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkippedSparse);
    }
}
