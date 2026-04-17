using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Estimate activation range statistics from weight norms (L2, L∞) combined
/// with architectural constants (hidden size, head dim). No actual forward
/// pass is executed — this is purely weight-derived. Used by inference-time
/// arena code to scale per-layer significance.
///
/// For each tensor, computes L2 and L∞ over the streamed weight values and
/// derives an estimated activation min/max as ±L∞·√fan_in / √hidden_size for
/// projection matrices. Conservative — meant as a prior, not a calibration.
///
/// Entity: <c>activation_range</c>. Signature: parent tensor hash + L2 + L∞
/// + estimated min + estimated max.
///
/// Depends on <c>model.weight_distribution</c> per the spec — this pass reads
/// the same tensor bytes but its semantics layer on the moments and so the
/// distribution entity must exist first for downstream consumers to join.
///
/// Per docs/specs/decomposers/analysis-passes.md § "ActivationRangePass".
/// </summary>
internal sealed partial class ActivationRangePass : IModelAnalysisPass
{
    public string PassId => "model.activation_range";
    public IReadOnlyList<string> Dependencies => ["model.weight_distribution"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public ActivationRangePass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int hiddenSize = context.Architecture.Architecture.HiddenSize;
        if (hiddenSize <= 0)
        {
            Log.NoHiddenSize(_logger, context.Source.ModelId);
            return Task.CompletedTask;
        }
        double sqrtHidden = Math.Sqrt(hiddenSize);

        int tensorOrdinal = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorOrdinal++;

            // Activation ranges only make sense for weight-bearing matrices —
            // skip embeddings, norms, and 0-D buffers. Track 2 weights are the
            // intended consumers.
            if (t.Classification.Role.IsTrack1())
            {
                continue;
            }
            if (t.Info.Shape.Length == 0 || t.Info.ElementCount == 0)
            {
                continue;
            }

            double sumSq = 0;
            double linf = 0;
            SafetensorsReader.StreamDecode(t.Info, chunk =>
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    double v = chunk[i];
                    sumSq += v * v;
                    double abs = Math.Abs(v);
                    if (abs > linf)
                    {
                        linf = abs;
                    }
                }
            });
            double l2 = Math.Sqrt(sumSq);

            // fan_in = product of shape[1..] for typical 2-D weights;
            // for higher-rank tensors take the leading dim as fan_out and the rest as fan_in.
            long fanIn = 1;
            for (int i = 1; i < t.Info.Shape.Length; i++)
            {
                fanIn *= t.Info.Shape[i];
            }
            double sqrtFanIn = Math.Sqrt(fanIn);
            double estMax = linf * sqrtFanIn / sqrtHidden;
            double estMin = -estMax;

            byte[] hash = context.NewSignature("actr")
                .WriteHash(t.ContentHash)
                .WriteDouble(l2)
                .WriteDouble(linf)
                .WriteDouble(estMin)
                .WriteDouble(estMax)
                .Finalize();

            EntityHandle range = session.Batch.AddEntity(hash, "activation_range");
            session.Batch.AddEntityModelSource(range, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_activation_range", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(range, null, "target", 1),
            ]);

            Log.TensorRanged(_logger, tensorOrdinal, t.Info.Name, l2, linf, estMin, estMax);
        }
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[range {Idx}] {Name} L2={L2:F4} L∞={Linf:F4} est=[{Min:F3},{Max:F3}]")]
        public static partial void TensorRanged(ILogger logger, int idx, string name, double l2, double linf, double min, double max);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[range] {ModelId} has no hidden_size in config; skipping pass")]
        public static partial void NoHiddenSize(ILogger logger, string modelId);
    }
}
