using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for RoPE frequency tables. Per A0: ROPE_FREQ
/// emits the whole tensor as ONE <c>rope_freq_table</c> entity hashed by
/// f64-canonical content of every element. Same RoPE configuration across
/// models → ONE entity → Glicko-2 corroboration on positional-encoding
/// frequency choices.
///
/// No sparsity filter (RoPE freq tables are small and structurally dense).
///
/// Per docs/specs/decomposers/analysis-passes.md A6 and
/// .claude/rules/35-inference-and-godel.md.
/// </summary>
internal sealed partial class RopeFreqPass : IModelAnalysisPass
{
    public string PassId => "model.rope_freqs";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int MaxLength = 1 << 22;
    private const int FlushThreshold = 1_000;

    private readonly ILogger _logger;

    public RopeFreqPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int processed = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            if (t.Classification.Role != TensorRole.RopeFreq)
            {
                continue;
            }

            long total = 1;
            for (int d = 0; d < t.Info.Shape.Length; d++)
            {
                total *= t.Info.Shape[d];
            }
            if (total <= 0 || total > MaxLength)
            {
                continue;
            }

            double[] values = SafetensorsReader.ReadTensorAsDouble(t.Info);
            int length = (int)total;
            if (values.Length != length)
            {
                continue;
            }

            CanonicalSignatureBuilder b = new(context.Compute.Common, "rofq");
            for (int i = 0; i < length; i++)
            {
                b.WriteDouble(values[i]);
            }
            byte[] hash = b.Finalize();

            EntityHandle freq = session.Batch.AddEntity(hash, "rope_freq_table");
            session.Batch.AddEntityModelSource(freq, context.Source.ModelSourceId);

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
            session.Batch.AddPhysicalityLineString4d(freq, "contour", verts.AsSpan());

            session.Batch.AddEdge("has_rope_freqs", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(freq, null, "target", 1),
            ]);

            processed++;
            await session.MaybeFlushAsync(FlushThreshold, ct);
        }

        Log.PassComplete(_logger, context.Source.ModelId, processed);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[rope-freq {ModelId}] complete — {Processed} rope_freq_table entities emitted")]
        public static partial void PassComplete(ILogger logger, string modelId, int processed);
    }
}
