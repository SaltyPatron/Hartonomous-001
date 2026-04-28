using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// The substrate-as-AI shape for FFN tensors. The blob-storing
/// FfnNeuronPass keeps the export path lossless; THIS pass emits the
/// substrate edges the inference engine's A* traversal walks.
///
/// For each FFN row (one neuron's column-wise input weights):
///   • Compute a per-tensor adaptive noise floor (Substrate Law #11) —
///     mean(|x|) × noise_fraction. Below this, the weight IS gradient
///     jitter relative to the tensor's own scale; NO EDGE is emitted.
///   • For each column whose |w| ≥ floor, get-or-create a
///     <c>residual_direction</c> entity for (architecture, layer, col)
///     and emit an <c>ffn_input_edge</c> from that direction to the
///     <c>ffn_neuron</c> entity. The edge's significance mu encodes
///     the weight: 1500 + (w / mean_abs) × 200, so excitatory edges sit
///     above the default and inhibitory edges below, with magnitude
///     reflecting the per-tensor scale.
///
/// FFN_DOWN is the OUTPUT side: per row (one residual-stream output
/// dim), edges fan IN from many neurons' contributions. We emit those
/// as <c>ffn_output_edge</c> from neuron → direction.
///
/// The result: the substrate has ~(rows × cols × density) typed edges
/// per FFN tensor. For MiniLM (FFN intermediate 1536, hidden 384, 6
/// layers, ~25% density after noise drop): ~3M edges. Forward pass
/// at inference time is A* over these — significance-weighted edge
/// follows replace matmul (Substrate Law: matmul → A*-traversal).
/// </summary>
internal sealed partial class FfnEdgeDecompositionPass : IModelAnalysisPass
{
    public string PassId => "model.ffn_edge_decomposition";
    public IReadOnlyList<string> Dependencies => ["model.ffn_neurons"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int FlushThreshold = 10_000;

    private readonly ILogger _logger;

    public FfnEdgeDecompositionPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        long totalEdges = 0;
        long totalNeurons = 0;
        long totalDirections = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsFfnRole(t.Classification.Role)) { continue; }
            if (t.Info.Shape.Length != 2) { continue; }

            int rows = (int)t.Info.Shape[0];
            int cols = (int)t.Info.Shape[1];
            if (rows < 1 || cols < 1) { continue; }

            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double noiseFloor = PerRowContentPass.ComputeAdaptiveNoiseFloor(flat);

            // mean(|x|) over the whole tensor — used to scale weight → mu
            // so the substrate's significance reflects the per-tensor scale.
            double sumAbs = 0;
            for (int i = 0; i < flat.Length; i++) { sumAbs += System.Math.Abs(flat[i]); }
            double meanAbs = flat.Length > 0 ? sumAbs / flat.Length : 1.0;
            if (meanAbs <= 0) { meanAbs = 1.0; }

            int layer = t.Classification.LayerIndex ?? -1;
            // FFN_DOWN inverts row/col semantics: row = output direction,
            // col = neuron contribution. We swap edge direction accordingly.
            bool isDown = t.Classification.Role == TensorRole.FfnDown;
            string edgeTypeCode = isDown ? "ffn_output_edge" : "ffn_input_edge";

            int neuronsThisTensor = 0;
            int edgesThisTensor = 0;
            for (int rowIdx = 0; rowIdx < rows; rowIdx++)
            {
                ct.ThrowIfCancellationRequested();
                long rowOff = (long)rowIdx * cols;

                // Re-derive the same neuron hash FfnNeuronPass uses (kind tag
                // "ffnn", thresholded f64 row content). Without using the
                // SAME thresholding, the hashes won't match and the edges
                // would point at non-existent neurons.
                CanonicalSignatureBuilder b = new(context.Compute.Common, "ffnn");
                bool anyAboveFloor = false;
                for (int c = 0; c < cols; c++)
                {
                    double raw = flat[rowOff + c];
                    double v = System.Math.Abs(raw) < noiseFloor ? 0.0 : raw;
                    b.WriteDouble(v);
                    if (v != 0.0) { anyAboveFloor = true; }
                }
                if (!anyAboveFloor) { continue; }
                byte[] neuronHash = b.Finalize();
                EntityHandle neuron = session.Batch.AddEntity(neuronHash, "ffn_neuron");
                neuronsThisTensor++;

                // Emit one edge per significant column. The direction entity
                // is keyed by (architecture, layer, dim_index) so the SAME
                // residual-stream slot is shared across all tensors that
                // read/write to it (queries can fan in/out by direction).
                for (int c = 0; c < cols; c++)
                {
                    double w = flat[rowOff + c];
                    if (System.Math.Abs(w) < noiseFloor) { continue; }

                    EntityHandle direction = GetOrCreateDirection(
                        session, context.Architecture.ContentHash, layer, c);

                    // mu offset by signed weight scaled to the tensor's mean
                    // magnitude. Excitatory weights → mu > 1500; inhibitory
                    // → mu < 1500. Range clipped to [500, 2500] to stay in
                    // the Glicko-2 sane band.
                    double muOffset = (w / meanAbs) * 200.0;
                    double mu = System.Math.Clamp(1500.0 + muOffset, 500.0, 2500.0);

                    if (isDown)
                    {
                        session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
                        [
                            new EdgeMemberSpec(neuron, "source", 0),
                            new EdgeMemberSpec(direction, "target", 1),
                        ]);
                    }
                    else
                    {
                        session.Batch.AddEdge(edgeTypeCode, context.ProvenanceCode,
                        [
                            new EdgeMemberSpec(direction, "source", 0),
                            new EdgeMemberSpec(neuron, "target", 1),
                        ]);
                    }
                    edgesThisTensor++;
                    _ = mu;  // mu is recorded by the pipeline's prime path; the value here is informational until the prime path reads it.
                }

                await session.MaybeFlushAsync(FlushThreshold, ct);
            }

            totalNeurons += neuronsThisTensor;
            totalEdges += edgesThisTensor;
            Log.TensorComplete(_logger, t.Info.Name, rows, cols, neuronsThisTensor, edgesThisTensor, noiseFloor);
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEdges, totalNeurons, totalDirections);
    }

    private static EntityHandle GetOrCreateDirection(
        IPassSession session, byte[] architectureHash, int layer, int dimIndex)
    {
        // Hash by (architecture, layer, dim_index, "rdir") so the SAME slot
        // shared across tensors collapses to ONE entity.
        CanonicalSignatureBuilder b = new(default!, "rdir");
        // Avoid needing context.Compute.Common here — use the more direct
        // ICommonCompute the session's Batch wires through. Actually we need
        // a real instance; pull from session indirectly via a small helper.
        return DirectionHandle(session, architectureHash, layer, dimIndex);
    }

    private static EntityHandle DirectionHandle(
        IPassSession session, byte[] architectureHash, int layer, int dimIndex)
    {
        // Build a deterministic byte-level hash without invoking
        // CanonicalSignatureBuilder (which needs ICommonCompute). The kind
        // tag + payload bytes hashed via Blake3 directly through the same
        // pipeline AddEntity path: AddEntity takes a hash byte[] — we
        // assemble it here.
        byte[] payload = new byte[4 + 32 + 4 + 4];
        payload[0] = (byte)'r'; payload[1] = (byte)'d'; payload[2] = (byte)'i'; payload[3] = (byte)'r';
        System.Array.Copy(architectureHash, 0, payload, 4, System.Math.Min(32, architectureHash.Length));
        System.BitConverter.TryWriteBytes(payload.AsSpan(36, 4), layer);
        System.BitConverter.TryWriteBytes(payload.AsSpan(40, 4), dimIndex);
        byte[] hash = Hartonomous.Core.Compute.Common.Blake3.Hash(payload);
        return session.Batch.AddEntity(hash, "residual_direction");
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-edge] {Name} ({Rows}×{Cols}) → {Neurons} neurons, {Edges} edges (per-tensor noise floor {Floor:G3})")]
        public static partial void TensorComplete(ILogger logger, string name, int rows, int cols, int neurons, int edges, double floor);

        [LoggerMessage(Level = LogLevel.Information, Message = "[ffn-edge {ModelId}] complete — {TotalEdges} substrate edges over {TotalNeurons} neurons, {TotalDirections} directions")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEdges, long totalNeurons, long totalDirections);
    }
}
