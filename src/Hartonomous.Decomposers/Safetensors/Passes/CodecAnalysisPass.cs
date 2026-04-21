using System.Buffers.Binary;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Neural codec analysis (Encodec, SNAC, DAC, Fish Speech codec). For each
/// VQ codebook tensor we compute:
///
///   • Codebook utilization prior — L2 norm of each codeword, normalized to a
///     probability distribution over the codebook. (Static approximation; true
///     utilization requires corpus forward pass which the substrate rejects as
///     approximation-by-inference — the weight norm is an honest weight-only
///     prior.)
///   • Entropy of that distribution.
///   • Dead-code count (near-zero L2).
///   • Pairwise cosine-similarity Frobenius norm: measures codebook diversity
///     (orthogonal codewords → low similarity → healthy codec).
///
/// Each codeword is also content-addressed as a <c>codec_codevector</c> entity
/// and attached to the parent <c>codec_codebook</c> entity. Code indices are
/// placement (edge position), not content.
///
/// Entity: <c>codec_codebook</c> per codebook tensor; <c>codec_codevector</c>
/// per codeword. Both content-addressed — same codebook across two models →
/// one entity, two `has_codebook` edges.
///
/// Per docs/specs/decomposers/analysis-passes.md § "CodecAnalysisPass".
/// </summary>
internal sealed partial class CodecAnalysisPass : IModelAnalysisPass
{
    public string PassId => "model.codec_analysis";
    public IReadOnlyList<string> Dependencies => [];

    // Neural codec families. Non-codec architectures ship no VqCodebook tensors
    // by classification, but architecture filtering keeps discovery tight.
    public IReadOnlyList<string> AppliesToArchitectures =>
    [
        "EncodecModel",
        "SnacModel",
        "DacModel",
        "FishSpeechCodec",
        "VqVae",
    ];

    private const int MaxCodebookSize = 4096;
    private const double DeadCodeThreshold = 1e-6;

    private readonly ILogger _logger;

    public CodecAnalysisPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        int stageOrdinal = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Classification.Role != TensorRole.VqCodebook)
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                Log.SkipNon2D(_logger, t.Info.Name, t.Info.Shape.Length);
                continue;
            }

            int codes = (int)t.Info.Shape[0];
            int dim = (int)t.Info.Shape[1];
            if (codes > MaxCodebookSize)
            {
                Log.SkipTooLarge(_logger, t.Info.Name, codes, MaxCodebookSize);
                continue;
            }
            stageOrdinal++;

            double[] codebook = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double[] norms = new double[codes];
            for (int i = 0; i < codes; i++)
            {
                long off = (long)i * dim;
                double sumSq = 0;
                for (int j = 0; j < dim; j++)
                {
                    double v = codebook[off + j];
                    sumSq += v * v;
                }
                norms[i] = Math.Sqrt(sumSq);
            }
            double totalNorm = 0;
            for (int i = 0; i < codes; i++)
            {
                totalNorm += norms[i];
            }
            double[] probs = new double[codes];
            int deadCodes = 0;
            double entropy = 0;
            if (totalNorm > 0)
            {
                for (int i = 0; i < codes; i++)
                {
                    probs[i] = norms[i] / totalNorm;
                    if (norms[i] < DeadCodeThreshold)
                    {
                        deadCodes++;
                    }
                    else if (probs[i] > 0)
                    {
                        entropy -= probs[i] * Math.Log(probs[i]);
                    }
                }
            }

            // Mean pairwise cosine similarity via normalized codebook Frobenius norm.
            double[] unit = new double[codes * dim];
            for (int i = 0; i < codes; i++)
            {
                long off = (long)i * dim;
                double inv = norms[i] > 0 ? 1.0 / norms[i] : 0;
                for (int j = 0; j < dim; j++)
                {
                    unit[off + j] = codebook[off + j] * inv;
                }
            }
            double frobeniusSq = 0;
            for (int i = 0; i < codes; i++)
            {
                long offI = (long)i * dim;
                for (int j = i + 1; j < codes; j++)
                {
                    long offJ = (long)j * dim;
                    double sim = 0;
                    for (int d = 0; d < dim; d++)
                    {
                        sim += unit[offI + d] * unit[offJ + d];
                    }
                    frobeniusSq += sim * sim;
                }
            }
            double meanPairSqSim = codes > 1 ? frobeniusSq / ((double)codes * (codes - 1) / 2) : 0;

            CanonicalSignatureBuilder b = new(context.Compute.Common, "cdbk");
            b.WriteHash(context.Architecture.ContentHash);
            b.WriteInt32LE(stageOrdinal);
            b.WriteInt32LE(codes);
            b.WriteInt32LE(dim);
            b.WriteDouble(entropy);
            b.WriteInt32LE(deadCodes);
            b.WriteDouble(meanPairSqSim);
            for (int i = 0; i < codes; i++)
            {
                b.WriteDouble(norms[i]);
            }
            byte[] codebookHash = b.Finalize();

            EntityHandle codebookEntity = session.Batch.AddEntity(codebookHash, "codec_codebook");
            session.Batch.AddEntityModelSource(codebookEntity, context.Source.ModelSourceId);
            session.Batch.AddEdge("has_codebook", context.ProvenanceCode,
            [
                new EdgeMemberSpec(null, t.EntityId, "source", 0),
                new EdgeMemberSpec(codebookEntity, null, "target", 1),
            ]);

            // Per-code vector entities.
            for (int i = 0; i < codes; i++)
            {
                CanonicalSignatureBuilder cb = new(context.Compute.Common, "cvec");
                cb.WriteHash(codebookHash);
                cb.WriteInt32LE(dim);
                long off = (long)i * dim;
                for (int d = 0; d < dim; d++)
                {
                    cb.WriteDouble(codebook[off + d]);
                }
                byte[] vecHash = cb.Finalize();

                EntityHandle codeEntity = session.Batch.AddEntity(vecHash, "codec_codevector");
                session.Batch.AddEdge("contains_codevector", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(codebookEntity, null, "codebook", 0),
                    new EdgeMemberSpec(codeEntity, null, "codevector", (short)i),
                ]);

                // S³ placement via Super-Fibonacci: index → deterministic S³
                // point, with the codeword's L2 norm on the M coordinate so the
                // codeword is still reachable by Hilbert index at inference time.
                byte[] wkb = BuildSuperFibonacciPointZm(i, codes, norms[i]);
                session.Batch.AddPhysicality(codeEntity, "codec_codevector_position", wkb);
            }

            Log.CodebookAnalyzed(_logger, t.Info.Name, codes, dim, deadCodes, entropy, meanPairSqSim);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deterministic placement on S³ via Super-Fibonacci quaternion sequence.
    /// Index = codeword position; scale on M = L2 norm. Codewords from identical
    /// codebooks therefore land on identical points — placement corroborates
    /// content.
    /// </summary>
    private static byte[] BuildSuperFibonacciPointZm(int idx, int total, double magnitude)
    {
        double[] parms = new double[2];
        parms[0] = idx;
        parms[1] = total;
        double[] quat = new double[4];
        SuperFibonacci.Project(parms, quat);

        byte[] wkb = new byte[37];
        wkb[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), 0xC0000001u); // POINTZM
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5), quat[0]);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13), quat[1]);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(21), quat[2]);
        BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(29), magnitude);
        return wkb;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[codec {Name}] codes={Codes} dim={Dim} dead={Dead} entropy={Entropy:F3} meanPairSim²={Sim:F4}")]
        public static partial void CodebookAnalyzed(ILogger logger, string name, int codes, int dim, int dead, double entropy, double sim);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[codec] {Name} codes={Codes} exceeds cap {Cap}; skipped")]
        public static partial void SkipTooLarge(ILogger logger, string name, int codes, int cap);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[codec] {Name} rank={Rank} not 2-D; skipped")]
        public static partial void SkipNon2D(ILogger logger, string name, int rank);
    }
}
