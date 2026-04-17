using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-tensor sparsity profile: near-zero fraction + log-magnitude histogram.
/// Drives Track 2's functional sparsity filter — weight patterns above the
/// significance threshold become edges; values below are not stored (Law #11
/// "sparsity is honest recording, not approximation"). This pass reports the
/// pattern; downstream passes consume it.
///
/// Streamed via <see cref="SafetensorsReader.StreamDecode"/> so multi-GB FFN
/// matrices process without ever materializing the full tensor.
///
/// Entity: <c>sparsity_profile</c>. Signature: parent tensor hash + log-magnitude
/// bucket edges + bucket counts + near-zero fraction. Identical weights across
/// models dedupe to one entity, two <c>has_sparsity_profile</c> edges.
///
/// Per docs/specs/decomposers/analysis-passes.md § "SparsityAnalysisPass".
/// </summary>
internal sealed partial class SparsityAnalysisPass : IModelAnalysisPass
{
    public string PassId => "model.sparsity";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double NearZeroThreshold = 1e-6;
    private const int BucketCount = 16;
    // Log10 bucket edges from 1e-9 to 1e6 — covers the practical magnitude range
    // for normalized neural network weights. Underflow flows into bucket 0,
    // overflow into bucket BucketCount-1.
    private static readonly double[] BucketLog10Edges =
    [
        -9.0, -8.0, -7.0, -6.0, -5.0, -4.0, -3.0, -2.0,
        -1.0,  0.0,  1.0,  2.0,  3.0,  4.0,  5.0,  6.0,
    ];

    private readonly ILogger _logger;

    public SparsityAnalysisPass(ILogger logger)
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

            long elements = t.Info.ElementCount;
            if (elements == 0)
            {
                continue;
            }

            long nearZeroCount = 0;
            long[] buckets = new long[BucketCount];

            SafetensorsReader.StreamDecode(t.Info, chunk =>
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    double abs = Math.Abs(chunk[i]);
                    if (abs < NearZeroThreshold)
                    {
                        nearZeroCount++;
                    }
                    int bucket = ResolveBucket(abs);
                    buckets[bucket]++;
                }
            });

            double nearZeroFraction = (double)nearZeroCount / elements;
            byte[] profileHash = BuildSignature(context, t.ContentHash, buckets, nearZeroFraction);
            EntityHandle profile = session.Batch.AddEntity(profileHash, "sparsity_profile");
            session.Batch.AddEntityModelSource(profile, context.Source.ModelSourceId);

            session.Batch.AddEdge("has_sparsity_profile", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(profile, null, "target", 1),
            ]);

            Log.TensorProfiled(_logger, tensorOrdinal, t.Info.Name, elements, nearZeroFraction);
        }
        return Task.CompletedTask;
    }

    private static byte[] BuildSignature(
        ModelPassContext context, byte[] tensorHash, long[] buckets, double nearZeroFraction)
    {
        ICanonicalSignatureBuilder b = context.NewSignature("spar")
            .WriteHash(tensorHash)
            .WriteInt32LE(BucketCount)
            .WriteDouble(NearZeroThreshold)
            .WriteDouble(nearZeroFraction);
        for (int i = 0; i < BucketCount; i++)
        {
            b.WriteDouble(BucketLog10Edges[i]);
        }
        for (int i = 0; i < BucketCount; i++)
        {
            b.WriteInt64LE(buckets[i]);
        }
        return b.Finalize();
    }

    private static int ResolveBucket(double abs)
    {
        if (abs <= 0)
        {
            return 0;
        }
        double log10 = Math.Log10(abs);
        for (int i = BucketCount - 1; i >= 0; i--)
        {
            if (log10 >= BucketLog10Edges[i])
            {
                return i;
            }
        }
        return 0;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "[sparsity {Idx}] {Name} elements={Elements} near0={NearZeroFraction:F4}")]
        public static partial void TensorProfiled(ILogger logger, int idx, string name, long elements, double nearZeroFraction);
    }
}
