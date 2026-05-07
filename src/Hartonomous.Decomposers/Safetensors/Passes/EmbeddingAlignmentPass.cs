using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Phase C2 — cross-model embedding alignment via orthogonal Procrustes.
/// Per-model Laplacian eigenmaps produce firefly coordinates that are
/// arbitrary up to rotation+reflection. Without alignment, two models'
/// fireflies for the same shared bpe_token sit in independent eigenspaces
/// and never converge — Voronoi consensus over the shared bpe_token entity
/// is ill-defined.
///
/// This pass picks the first ingested model with sufficient vocab as the
/// canonical anchor (recorded in <c>substrate.embedding_alignment_anchor</c>,
/// migration 0061). Every subsequent model gets a 3×3 rotation R fit by
/// Kabsch/Procrustes against the anchor's fireflies on the shared vocab,
/// and that rotation is applied to all of this model's firefly POINTZM
/// physicalities (M = L2 magnitude is preserved). After alignment, two
/// models' fireflies for "king" point to nearby positions on the same S³
/// surface — Voronoi consensus and 4D-distance comparisons become
/// meaningful across models.
///
/// Depends on: EmbeddingFireflyPass (must have already attached fireflies
/// to bpe_token entities for this model).
/// </summary>
internal sealed partial class EmbeddingAlignmentPass : IModelAnalysisPass
{
    public string PassId => "model.embedding_alignment";
    public IReadOnlyList<string> Dependencies => ["model.embedding_fireflies"];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const int MinIntersectionTokens = 64;

    private readonly ILogger _logger;
    private readonly NpgsqlDataSource? _dataSource;

    public EmbeddingAlignmentPass(ILogger logger, NpgsqlDataSource? dataSource = null)
    {
        _logger = logger;
        _dataSource = dataSource;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        if (_dataSource is null)
        {
            Log.NoDataSource(_logger, context.Source.ModelId);
            return;
        }

        // Step 1: get the bpe_token entity hashes that have fireflies for THIS
        // model. Same query used twice — once here, once for the anchor —
        // because the intersection is what Procrustes fits over.
        byte[][] thisFireflyEntityHashes = await GetFireflyBpeTokenHashesAsync(
            context.Source.ModelSourceId, ct);
        if (thisFireflyEntityHashes.Length < MinIntersectionTokens)
        {
            Log.InsufficientFireflies(_logger, context.Source.ModelId, thisFireflyEntityHashes.Length, MinIntersectionTokens);
            return;
        }

        // Step 2: claim or get the canonical anchor. First-write-wins.
        // The function returns whichever model_source is currently the
        // anchor — could be us (we just claimed) or a prior ingestion.
        long anchorModelSourceId = await ClaimOrGetAnchorAsync(
            context.Source.ModelSourceId, thisFireflyEntityHashes.Length, ct);

        if (anchorModelSourceId == context.Source.ModelSourceId)
        {
            Log.AnchorClaimed(_logger, context.Source.ModelId, thisFireflyEntityHashes.Length);
            return;
        }

        // Step 3: fetch the intersection — bpe_token entities that have
        // fireflies in BOTH the anchor and this model. Hash32 wraps the
        // 32-byte BLAKE3 digest with O(1) equality / hashing for set
        // membership testing without per-comparison byte[] allocation.
        byte[][] anchorFireflyHashes = await GetFireflyBpeTokenHashesAsync(anchorModelSourceId, ct);
        HashSet<Hash32> anchorSet = new(anchorFireflyHashes.Length);
        foreach (byte[] h in anchorFireflyHashes)
        {
            anchorSet.Add(new Hash32(h));
        }
        List<byte[]> shared = new(thisFireflyEntityHashes.Length);
        foreach (byte[] h in thisFireflyEntityHashes)
        {
            if (anchorSet.Contains(new Hash32(h))) { shared.Add(h); }
        }
        if (shared.Count < MinIntersectionTokens)
        {
            Log.InsufficientIntersection(_logger, context.Source.ModelId, shared.Count, MinIntersectionTokens);
            return;
        }

        // Step 4: pull (entity_hash, x, y, z) for both models on the shared
        // vocab. Both are ordered by entity_hash ASC so columns line up.
        byte[][] sharedArr = [.. shared];
        Coords anchor = await GetCoordsAsync(sharedArr, anchorModelSourceId, ct);
        Coords self = await GetCoordsAsync(sharedArr, context.Source.ModelSourceId, ct);
        if (anchor.N != self.N || anchor.N < MinIntersectionTokens)
        {
            Log.CoordsMismatch(_logger, context.Source.ModelId, anchor.N, self.N);
            return;
        }

        // Step 5: pack into d×n column-major as ProcrustesAlign expects
        // (d=3, n=N). X is the moving frame (this model), Y is the target
        // (anchor). R*X ≈ Y, so the resulting R rotates this-model coords
        // into the anchor's frame.
        int n = anchor.N;
        double[] X = new double[3 * n];
        double[] Y = new double[3 * n];
        for (int i = 0; i < n; i++)
        {
            X[0 * n + i] = self.X[i];
            X[1 * n + i] = self.Y[i];
            X[2 * n + i] = self.Z[i];
            Y[0 * n + i] = anchor.X[i];
            Y[1 * n + i] = anchor.Y[i];
            Y[2 * n + i] = anchor.Z[i];
        }
        double[] rotation = new double[9];
        double residual = ProcrustesAlign.F64(3, n, X, Y, rotation);
        Log.RotationFit(_logger, context.Source.ModelId, n, residual);

        // Step 6: apply the rotation to every firefly POINTZM of this
        // model via the substrate function. M (L2 magnitude) is preserved.
        long updated = await ApplyRotationAsync(context.Source.ModelSourceId, rotation, ct);
        Log.RotationApplied(_logger, context.Source.ModelId, updated);
    }

    private async Task<byte[][]> GetFireflyBpeTokenHashesAsync(long modelSourceId, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
                await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                        conn,
                        SubstrateFunctionNames.EmbeddingFireflyTokenHashes,
                        [(int)modelSourceId]);
        List<byte[]> hashes = [];
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { hashes.Add((byte[])r.GetValue(0)); }
        return [.. hashes];
    }

    private async Task<long> ClaimOrGetAnchorAsync(long modelSourceId, int intersectionCount, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ClaimOrGetEmbeddingAnchor,
            [(int)modelSourceId, intersectionCount]);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is int i ? i : (result is long l ? l : modelSourceId);
    }

    private async Task<Coords> GetCoordsAsync(byte[][] sharedHashes, long modelSourceId, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.GetFireflyCoords,
            [
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = sharedHashes },
                new NpgsqlParameter { Value = (int)modelSourceId }
            ]);
        List<double> xs = [], ys = [], zs = [];
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // entity_hash at position 0 is consumed only by ORDER BY on the
            // SQL side; we don't need it in C# because the rows arrive in
            // hash-sorted order matching anchor-side rows pulled the same way.
            xs.Add(r.GetDouble(1));
            ys.Add(r.GetDouble(2));
            zs.Add(r.GetDouble(3));
        }
        return new Coords(xs.Count, [.. xs], [.. ys], [.. zs]);
    }

    private async Task<long> ApplyRotationAsync(long modelSourceId, double[] r, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        object?[] parameterValues = new object?[10];
        parameterValues[0] = (int)modelSourceId;
        for (int index = 0; index < 9; index++)
        {
            parameterValues[index + 1] = r[index];
        }

        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ApplyFireflyRotation,
            parameterValues);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }

    private sealed record Coords(int N, double[] X, double[] Y, double[] Z);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[embedding-alignment {ModelId}] no NpgsqlDataSource injected — alignment skipped (composition root must wire it).")]
        public static partial void NoDataSource(ILogger logger, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-alignment {ModelId}] insufficient fireflies ({Count} < {Min}); skipped")]
        public static partial void InsufficientFireflies(ILogger logger, string modelId, int count, int min);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-alignment {ModelId}] became canonical anchor ({TokenCount} tokens)")]
        public static partial void AnchorClaimed(ILogger logger, string modelId, int tokenCount);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-alignment {ModelId}] insufficient intersection with anchor ({Shared} < {Min}); skipped")]
        public static partial void InsufficientIntersection(ILogger logger, string modelId, int shared, int min);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[embedding-alignment {ModelId}] coords mismatch (anchor={AnchorN}, self={SelfN}); skipped")]
        public static partial void CoordsMismatch(ILogger logger, string modelId, int anchorN, int selfN);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-alignment {ModelId}] Procrustes fit on {N} shared tokens; residual={Residual:F4}")]
        public static partial void RotationFit(ILogger logger, string modelId, int n, double residual);

        [LoggerMessage(Level = LogLevel.Information, Message = "[embedding-alignment {ModelId}] applied rotation to {Updated} fireflies")]
        public static partial void RotationApplied(ILogger logger, string modelId, long updated);
    }
}
