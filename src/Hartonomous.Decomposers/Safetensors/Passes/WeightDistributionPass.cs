using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-tensor moment statistics: count, min, max, mean, variance, skew,
/// kurtosis. Streamed via <see cref="SafetensorsReader.StreamDecode"/> using
/// Welford-style online updates so the values are stable even on giant
/// tensors (no two-pass mean+variance, no naive sum-of-squares overflow).
///
/// Entity: <c>weight_distribution</c>. Signature: parent tensor hash + each
/// statistic in canonical order. Two tensors with identical weights produce the
/// same distribution entity (Law #6).
///
/// Per docs/specs/decomposers/analysis-passes.md § "WeightDistributionPass".
/// </summary>
internal sealed partial class WeightDistributionPass : IModelAnalysisPass
{
    public string PassId => "model.weight_distribution";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public WeightDistributionPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int tensorOrdinal = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorOrdinal++;

            if (t.Info.ElementCount == 0)
            {
                continue;
            }

            long count = 0;
            double mean = 0, m2 = 0, m3 = 0, m4 = 0;
            double min = double.PositiveInfinity, max = double.NegativeInfinity;

            SafetensorsReader.StreamDecode(t.Info, chunk =>
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    double x = chunk[i];
                    if (x < min)
                    {
                        min = x;
                    }
                    if (x > max)
                    {
                        max = x;
                    }

                    count++;
                    double delta = x - mean;
                    double delta_n = delta / count;
                    double delta_n2 = delta_n * delta_n;
                    double term1 = delta * delta_n * (count - 1);
                    mean += delta_n;
                    m4 += term1 * delta_n2 * (count * count - 3 * count + 3)
                        + 6 * delta_n2 * m2
                        - 4 * delta_n * m3;
                    m3 += term1 * delta_n * (count - 2) - 3 * delta_n * m2;
                    m2 += term1;
                }
            });

            double variance = count > 1 ? m2 / (count - 1) : 0;
            double std = Math.Sqrt(variance);
            double skew = (count > 0 && m2 > 0) ? Math.Sqrt((double)count) * m3 / Math.Pow(m2, 1.5) : 0;
            double kurtosis = (count > 0 && m2 > 0) ? (count * m4) / (m2 * m2) - 3.0 : 0;

            byte[] distHash = context.NewSignature("dist")
                .WriteHash(t.ContentHash)
                .WriteInt64LE(count)
                .WriteDouble(min)
                .WriteDouble(max)
                .WriteDouble(mean)
                .WriteDouble(variance)
                .WriteDouble(std)
                .WriteDouble(skew)
                .WriteDouble(kurtosis)
                .Finalize();

            EntityHandle dist = session.Batch.AddEntity(distHash, "weight_distribution");
            session.Batch.AddEntityModelSource(dist, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_weight_distribution", context.ProvenanceCode,
            [
                new EdgeMemberSpec(t.Entity, "source", 0),
                new EdgeMemberSpec(dist, "target", 1),
            ]);

            Log.TensorDistributed(_logger, tensorOrdinal, t.Info.Name, mean, std, skew, kurtosis);
        }
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[dist {Idx}] {Name} mean={Mean:F4} std={Std:F4} skew={Skew:F3} kurt={Kurt:F3}")]
        public static partial void TensorDistributed(ILogger logger, int idx, string name, double mean, double std, double skew, double kurt);
    }
}
