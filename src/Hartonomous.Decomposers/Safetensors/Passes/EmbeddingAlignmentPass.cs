using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;

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

        // Step 1: get the bpe_token entity ids that have fireflies for THIS
        // model. Same query used twice — once here, once for the anchor —
        // because the intersection is what Procrustes fits over.
        long[] thisFireflyEntityIds = await GetFireflyBpeTokenIdsAsync(
            context.Source.ModelSourceId, ct);
        if (thisFireflyEntityIds.Length < MinIntersectionTokens)
        {
            Log.InsufficientFireflies(_logger, context.Source.ModelId, thisFireflyEntityIds.Length, MinIntersectionTokens);
            return;
        }

        // Step 2: claim or get the canonical anchor. First-write-wins.
        // The function returns whichever model_source is currently the
        // anchor — could be us (we just claimed) or a prior ingestion.
        long anchorModelSourceId = await ClaimOrGetAnchorAsync(
            context.Source.ModelSourceId, thisFireflyEntityIds.Length, ct);

        if (anchorModelSourceId == context.Source.ModelSourceId)
        {
            Log.AnchorClaimed(_logger, context.Source.ModelId, thisFireflyEntityIds.Length);
            return;
        }

        // Step 3: fetch the intersection — bpe_token entities that have
        // fireflies in BOTH the anchor and this model. SQL IN clause via
        // ANY($1) is the smallest cross-model query.
        long[] anchorFireflyIds = await GetFireflyBpeTokenIdsAsync(anchorModelSourceId, ct);
        HashSet<long> anchorSet = new(anchorFireflyIds);
        List<long> shared = new(thisFireflyEntityIds.Length);
        foreach (long id in thisFireflyEntityIds)
        {
            if (anchorSet.Contains(id)) { shared.Add(id); }
        }
        if (shared.Count < MinIntersectionTokens)
        {
            Log.InsufficientIntersection(_logger, context.Source.ModelId, shared.Count, MinIntersectionTokens);
            return;
        }

        // Step 4: pull (entity_id, x, y, z) for both models on the shared
        // vocab. Both are ordered by entity_id ASC so columns line up.
        long[] sharedArr = [.. shared];
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

    private async Task<long[]> GetFireflyBpeTokenIdsAsync(long modelSourceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT p.entity_id
              FROM substrate.physicality p
              JOIN substrate.entity_model_source ems ON ems.entity_id = p.entity_id
              JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
             WHERE ems.model_source_id = $1
               AND pt.code = 'embedding_firefly'
             ORDER BY p.entity_id ASC";

        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = modelSourceId });
        List<long> ids = [];
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { ids.Add(r.GetInt64(0)); }
        return [.. ids];
    }

    private async Task<long> ClaimOrGetAnchorAsync(long modelSourceId, int intersectionCount, CancellationToken ct)
    {
        const string sql = "SELECT substrate.claim_or_get_embedding_anchor($1, $2)";
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = modelSourceId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = intersectionCount });
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : modelSourceId;
    }

    private async Task<Coords> GetCoordsAsync(long[] sharedIds, long modelSourceId, CancellationToken ct)
    {
        const string sql = "SELECT entity_id, x, y, z FROM substrate.get_firefly_coords($1, $2)";
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = sharedIds });
        cmd.Parameters.Add(new NpgsqlParameter { Value = modelSourceId });
        List<long> ids = [];
        List<double> xs = [], ys = [], zs = [];
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            ids.Add(r.GetInt64(0));
            xs.Add(r.GetDouble(1));
            ys.Add(r.GetDouble(2));
            zs.Add(r.GetDouble(3));
        }
        return new Coords(ids.Count, [.. xs], [.. ys], [.. zs]);
    }

    private async Task<long> ApplyRotationAsync(long modelSourceId, double[] r, CancellationToken ct)
    {
        const string sql = @"
            SELECT substrate.apply_firefly_rotation(
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10)";
        await using NpgsqlConnection conn = await _dataSource!.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = modelSourceId });
        for (int i = 0; i < 9; i++)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = r[i] });
        }
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
