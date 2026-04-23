using System.Buffers.Binary;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Track 1 — embedding wholesale ingestion. For each Track-1 tensor (token /
/// position / token-type embedding, VQ codebook, object query):
///
///   1. Decode tensor bytes to f64 row-major.
///   2. Compute per-row L2 magnitude (the M coordinate of the firefly).
///   3. Project rows to the first 3 non-trivial eigenvectors of the normalized
///      Laplacian of the symmetric k-NN cosine graph (X, Y, Z).
///   4. Emit one <c>bpe_token</c> entity per row, content-addressed by the 4D
///      coordinates (X, Y, Z, M). Identical coordinates across runs/models
///      dedupe to one entity (Law #6).
///   5. Attach <c>embedding_firefly</c> physicality (POINTZM WKB) and a
///      <c>has_token_id</c> edge from the tensor entity to the firefly.
///
/// Per docs/specs/decomposers/analysis-passes.md § "EmbeddingFireflyPass"
/// and docs/specs/engine/embedding-physicality.md.
/// </summary>
internal sealed partial class EmbeddingFireflyPass : IModelAnalysisPass
{
    public string PassId => "model.embedding_fireflies";

    public IReadOnlyList<string> Dependencies => [];

    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FireflyBatchSize = 50_000;
    private const int MaxFireflyRows = 50_000;

    private readonly ILogger _logger;
    private readonly LaplacianEigenmapOptions _baseOptions;

    public EmbeddingFireflyPass(ILogger logger, LaplacianEigenmapOptions? options = null)
    {
        _logger = logger;
        _baseOptions = options ?? LaplacianEigenmapOptions.Default;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        ulong baseSeed = context.DeriveSeed(PassId);

        int tensorOrdinal = 0;
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorOrdinal++;

            if (!t.Classification.Role.IsTrack1())
            {
                continue;
            }
            if (t.Info.Shape.Length != 2)
            {
                Log.SkipNon2D(_logger, t.Info.Name, t.Info.Shape.Length);
                continue;
            }
            long rowsLong = t.Info.Shape[0];
            if (rowsLong < 4 || rowsLong > MaxFireflyRows)
            {
                Log.SkipOutOfRange(_logger, t.Info.Name, rowsLong);
                continue;
            }

            int rows = (int)rowsLong;
            int cols = (int)t.Info.Shape[1];

            Log.TensorStart(_logger, tensorOrdinal, t.Info.Name, rows, cols);
            double[] flat = SafetensorsReader.ReadTensorAsDouble(t.Info);
            double[] magnitude = ComputeRowMagnitudes(flat, rows, cols);

            // Per-tensor seed = baseSeed XOR low 64 bits of tensor content hash.
            // Same tensor content + same model + same pass → same seed.
            ulong tensorSeed = baseSeed ^ BitConverter.ToUInt64(t.ContentHash, 0);
            int seed = (int)(tensorSeed & 0x7FFFFFFF);
            LaplacianEigenmapOptions opts = _baseOptions with { Seed = seed };

            (double[] x, double[] y, double[] z) = LaplacianEigenmap.Project(
                flat, rows, cols, opts,
                onStage: msg => Log.Stage(_logger, t.Info.Name, msg));

            for (int i = 0; i < rows; i++)
            {
                ct.ThrowIfCancellationRequested();

                byte[] fireflyHash = new CanonicalSignatureBuilder(context.Compute.Common, "fire")
                    .WriteDouble(x[i])
                    .WriteDouble(y[i])
                    .WriteDouble(z[i])
                    .WriteDouble(magnitude[i])
                    .Finalize();

                EntityHandle firefly = session.Batch.AddEntity(fireflyHash, "bpe_token");
                session.Batch.AddPhysicalityPoint4d(firefly, "embedding_firefly", x[i], y[i], z[i], magnitude[i]);
                session.Batch.AddSignificance(firefly, "model_trust", ModelDerivedTrustMu);
                session.Batch.AddEntityModelSource(firefly, context.Source.ModelSourceId);

                session.Batch.AddEdge("has_token_id", context.ProvenanceCode,
                [
                    new EdgeMemberSpec(null, t.EntityId, "source", 0),
                    new EdgeMemberSpec(firefly, null, "target", 1),
                ]);

                await session.MaybeFlushAsync(FireflyBatchSize, ct);
            }
            Log.TensorComplete(_logger, t.Info.Name, rows);
        }
    }

    private static double[] ComputeRowMagnitudes(double[] flat, int rows, int cols)
    {
        double[] mag = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            long off = (long)i * cols;
            double sumSq = 0;
            for (int j = 0; j < cols; j++)
            {
                double v = flat[off + j];
                sumSq += v * v;
            }
            mag[i] = Math.Sqrt(sumSq);
        }
        return mag;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[firefly {Idx}] {Name} starting (rows={Rows}, cols={Cols})")]
        public static partial void TensorStart(ILogger logger, int idx, string name, int rows, int cols);

        [LoggerMessage(Level = LogLevel.Information, Message = "[firefly] {Name} complete ({Rows} fireflies emitted)")]
        public static partial void TensorComplete(ILogger logger, string name, int rows);

        [LoggerMessage(Level = LogLevel.Information, Message = "[firefly] {Name}: {Stage}")]
        public static partial void Stage(ILogger logger, string name, string stage);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[firefly] {Name} not 2-D (rank={Rank}); skipped")]
        public static partial void SkipNon2D(ILogger logger, string name, int rank);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[firefly] {Name} rows={Rows} out of supported range; skipped")]
        public static partial void SkipOutOfRange(ILogger logger, string name, long rows);
    }
}
