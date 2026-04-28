using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Hash-as-PK ingestion pipeline. Every COPY writes
/// (entity_type_id, entity_hash) or (edge_type_id, edge_hash) directly into
/// substrate tables. There is no staging table for entity ID resolution —
/// the hash the decomposer computed IS the foreign key. The pipeline never
/// JOINs back to substrate.entity to discover a surrogate id; the surrogate
/// id does not exist.
///
/// Single transaction per batch. Order: entities, edges + edge_members,
/// junctions, physicality, sequence, entity_significance, entity_model_source.
/// Edge trajectories and edge_significance priming happen in dedicated phase
/// runners after the per-batch transactions commit.
/// </summary>
public sealed partial class NpgsqlIngestionPipeline : IIngestionPipeline
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly CodeResolver _codeResolver;
    private readonly ILogger<NpgsqlIngestionPipeline> _logger;

    private long _entitiesSubmitted;
    private long _edgesSubmitted;
    private long _junctionsSubmitted;
    private long _physicalitiesSubmitted;
    private long _significanceInitialized;
    private long _entityModelSourcesLinked;
    private long _batchesCommitted;
    private long _batchesFailed;
    private TimeSpan _totalCommitTime;

    public NpgsqlIngestionPipeline(
        string connectionString,
        IReferenceDataReader referenceDataReader,
        ILogger<NpgsqlIngestionPipeline> logger)
    {
        // IncludeErrorDetail surfaces the offending row in CHECK / FK / NOT NULL
        // violations. Hashes are not sensitive content.
        NpgsqlConnectionStringBuilder csb = new(connectionString) { IncludeErrorDetail = true };
        NpgsqlDataSourceBuilder builder = new(csb.ConnectionString);
        _dataSource = builder.Build();
        _codeResolver = new CodeResolver(referenceDataReader);
        _logger = logger;
    }

    public PipelineStats Stats => new()
    {
        EntitiesSubmitted        = _entitiesSubmitted,
        EdgesSubmitted           = _edgesSubmitted,
        JunctionsSubmitted       = _junctionsSubmitted,
        PhysicalitiesSubmitted   = _physicalitiesSubmitted,
        SignificanceInitialized  = _significanceInitialized,
        EntityModelSourcesLinked = _entityModelSourcesLinked,
        BatchesCommitted         = _batchesCommitted,
        BatchesFailed            = _batchesFailed,
        TotalCommitTime          = _totalCommitTime,
    };

    public IIngestionBatch CreateBatch() => new IngestionBatch();

    public async Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct)
    {
        if (batch is not IngestionBatch b)
        {
            throw new ArgumentException("Batch must be created by this pipeline.", nameof(batch));
        }

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

            await UpsertEntitiesAsync(conn, b, ct);
            await CreateEdgesAsync(conn, b, ct);
            await PopulateJunctionsAsync(conn, b, ct);
            await CreatePhysicalitiesAsync(conn, b, ct);
            await InitializeEntitySignificanceAsync(conn, b, ct);
            await LinkEntityModelSourcesAsync(conn, b, ct);

            await tx.CommitAsync(ct);

            Interlocked.Add(ref _entitiesSubmitted,        b.EntityCount);
            Interlocked.Add(ref _edgesSubmitted,           b.EdgeCount);
            Interlocked.Add(ref _junctionsSubmitted,       b.Junctions.Count);
            Interlocked.Add(ref _physicalitiesSubmitted,   b.Physicalities.Count);
            Interlocked.Add(ref _significanceInitialized,  b.Significances.Count);
            Interlocked.Add(ref _entityModelSourcesLinked, b.EntityModelSources.Count);
            Interlocked.Increment(ref _batchesCommitted);

            Log.BatchCommitted(_logger, b.EntityCount, b.EdgeCount, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _batchesFailed);
            Log.BatchFailed(_logger, b.EntityCount, ex);
            throw;
        }
        finally
        {
            _totalCommitTime += sw.Elapsed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    // ── UpsertEntitiesAsync ────────────────────────────────────────────────
    // Direct COPY into substrate.entity. ON CONFLICT (entity_type_id, hash) DO NOTHING.
    // No staging table, no resolve, no remap — the hash IS the FK.
    private async Task UpsertEntitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Entities.Count == 0)
        {
            return;
        }

        // Pre-resolve type codes once.
        int[] typeIds = new int[batch.Entities.Count];
        for (int i = 0; i < batch.Entities.Count; i++)
        {
            typeIds[i] = await _codeResolver.EntityTypeIdAsync(batch.Entities[i].EntityTypeCode, ct);
        }

        // PG won't COPY directly into a partitioned table with ON CONFLICT,
        // but it will if the conflict target is the table's PK / unique
        // constraint. Use INSERT-from-temp instead: COPY into a staging
        // table, then INSERT...SELECT...DISTINCT...ON CONFLICT DO NOTHING.
        // The staging table is small and ephemeral; it does NOT participate
        // in ID resolution — entities go straight to substrate.entity by hash.
        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_entity (" +
            "  entity_type_id INT NOT NULL, " +
            "  hash BYTEA NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_entity (entity_type_id, hash) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Entities.Count; i++)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(typeIds[i], NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(batch.Entities[i].Hash, NpgsqlDbType.Bytea, ct);
            }
            await writer.CompleteAsync(ct);
        }

        // Drain staging into substrate.entity via the named substrate
        // function (AP-2: no inline INSERT SQL in C#). The function loops
        // over distinct entity_type_ids and INSERTs one partition at a
        // time, sidestepping PG's multi-partition tuple-router corruption
        // under bulk load with the hartonomous extension.
        await using NpgsqlCommand flush = new(
            "SELECT substrate.flush_entities_from_staging()", conn);
        await flush.ExecuteNonQueryAsync(ct);
    }

    // ── CreateEdgesAsync ───────────────────────────────────────────────────
    // Compute edge hash from (edge_type_id, ordered participant hashes).
    // COPY edge rows + edge_member rows with composite hash FKs.
    private async Task CreateEdgesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Edges.Count == 0)
        {
            return;
        }

        // Pre-resolve type/role/provenance ids and build edge hashes.
        int[] edgeTypeIds = new int[batch.Edges.Count];
        int[] provenanceIds = new int[batch.Edges.Count];
        byte[][] edgeHashes = new byte[batch.Edges.Count][];
        int[][] memberRoleIds = new int[batch.Edges.Count][];

        for (int i = 0; i < batch.Edges.Count; i++)
        {
            EdgeEntry edge = batch.Edges[i];
            edgeTypeIds[i]   = await _codeResolver.EdgeTypeIdAsync(edge.EdgeTypeCode, ct);
            provenanceIds[i] = await _codeResolver.ProvenanceIdAsync(edge.ProvenanceCode, ct);

            // Sort members by Position so edge hash is deterministic regardless
            // of insertion order in the decomposer.
            EdgeMemberSpec[] sorted = (EdgeMemberSpec[])edge.Members.Clone();
            Array.Sort(sorted, (a, b) => a.Position.CompareTo(b.Position));

            byte[][] orderedHashes = new byte[sorted.Length][];
            int[] roleIds = new int[sorted.Length];
            for (int j = 0; j < sorted.Length; j++)
            {
                orderedHashes[j] = sorted[j].Entity.Hash;
                roleIds[j] = await _codeResolver.EdgeRoleIdAsync(sorted[j].RoleCode, ct);
            }
            edgeHashes[i] = ComputeEdgeHash(edgeTypeIds[i], orderedHashes);
            memberRoleIds[i] = roleIds;
        }

        // ── COPY substrate.edge ──────────────────────────────────────────
        await using (NpgsqlCommand createEdgeStaging = new(
            "CREATE TEMP TABLE staging_edge (" +
            "  edge_type_id INT NOT NULL, " +
            "  hash BYTEA NOT NULL, " +
            "  provenance_id INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createEdgeStaging.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter edgeWriter = await conn.BeginBinaryImportAsync(
            "COPY staging_edge (edge_type_id, hash, provenance_id) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Edges.Count; i++)
            {
                await edgeWriter.StartRowAsync(ct);
                await edgeWriter.WriteAsync(edgeTypeIds[i],   NpgsqlDbType.Integer, ct);
                await edgeWriter.WriteAsync(edgeHashes[i],    NpgsqlDbType.Bytea,   ct);
                await edgeWriter.WriteAsync(provenanceIds[i], NpgsqlDbType.Integer, ct);
            }
            await edgeWriter.CompleteAsync(ct);
        }

        // Drain staging_edge via named substrate function (AP-2).
        await using (NpgsqlCommand flushEdges = new(
            "SELECT substrate.flush_edges_from_staging()", conn))
        {
            await flushEdges.ExecuteNonQueryAsync(ct);
        }

        // ── COPY substrate.edge_member ───────────────────────────────────
        // edge_member is partitioned by edge_type_id and FKs both to edge
        // (composite) and entity (composite). Direct binary COPY.
        int totalMembers = 0;
        for (int i = 0; i < batch.Edges.Count; i++)
        {
            totalMembers += batch.Edges[i].Members.Length;
        }
        if (totalMembers == 0)
        {
            return;
        }

        await using (NpgsqlCommand createMemberStaging = new(
            "CREATE TEMP TABLE staging_edge_member (" +
            "  edge_type_id INT NOT NULL, " +
            "  edge_hash BYTEA NOT NULL, " +
            "  entity_type_id INT NOT NULL, " +
            "  entity_hash BYTEA NOT NULL, " +
            "  edge_role_id INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createMemberStaging.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter memberWriter = await conn.BeginBinaryImportAsync(
            "COPY staging_edge_member (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Edges.Count; i++)
            {
                EdgeEntry edge = batch.Edges[i];
                EdgeMemberSpec[] sorted = (EdgeMemberSpec[])edge.Members.Clone();
                Array.Sort(sorted, (a, b) => a.Position.CompareTo(b.Position));
                int[] roleIds = memberRoleIds[i];

                for (int j = 0; j < sorted.Length; j++)
                {
                    int entityTypeId = await _codeResolver.EntityTypeIdAsync(sorted[j].Entity.EntityTypeCode, ct);
                    await memberWriter.StartRowAsync(ct);
                    await memberWriter.WriteAsync(edgeTypeIds[i],     NpgsqlDbType.Integer, ct);
                    await memberWriter.WriteAsync(edgeHashes[i],      NpgsqlDbType.Bytea,   ct);
                    await memberWriter.WriteAsync(entityTypeId,       NpgsqlDbType.Integer, ct);
                    await memberWriter.WriteAsync(sorted[j].Entity.Hash, NpgsqlDbType.Bytea, ct);
                    await memberWriter.WriteAsync(roleIds[j],         NpgsqlDbType.Integer, ct);
                }
            }
            await memberWriter.CompleteAsync(ct);
        }

        // Drain staging_edge_member via named substrate function (AP-2).
        await using (NpgsqlCommand flushMembers = new(
            "SELECT substrate.flush_edge_members_from_staging()", conn))
        {
            await flushMembers.ExecuteNonQueryAsync(ct);
        }
    }

    // ── PopulateJunctionsAsync ─────────────────────────────────────────────
    // Group by table, COPY composite hash FK + reference id.
    private async Task PopulateJunctionsAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Junctions.Count == 0)
        {
            return;
        }

        Dictionary<string, List<JunctionEntry>> grouped = new(StringComparer.Ordinal);
        foreach (JunctionEntry j in batch.Junctions)
        {
            if (!grouped.TryGetValue(j.JunctionTable, out List<JunctionEntry>? list))
            {
                list = [];
                grouped[j.JunctionTable] = list;
            }
            list.Add(j);
        }

        foreach ((string table, List<JunctionEntry> entries) in grouped)
        {
            string refCol = GetJunctionRefColumn(table);
            bool hasMu = false;
            foreach (JunctionEntry e in entries)
            {
                if (e.Mu.HasValue) { hasMu = true; break; }
            }

            string stagingCols = hasMu
                ? "entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, ref_id INT NOT NULL, mu FLOAT8"
                : "entity_type_id INT NOT NULL, entity_hash BYTEA NOT NULL, ref_id INT NOT NULL";

            string stagingTable = $"staging_{table}";
            await using (NpgsqlCommand createTemp = new(
                $"CREATE TEMP TABLE {stagingTable} ({stagingCols}) ON COMMIT DROP", conn))
            {
                await createTemp.ExecuteNonQueryAsync(ct);
            }

            string copyCols = hasMu
                ? "entity_type_id, entity_hash, ref_id, mu"
                : "entity_type_id, entity_hash, ref_id";

            await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
                $"COPY {stagingTable} ({copyCols}) FROM STDIN (FORMAT binary)", ct))
            {
                foreach (JunctionEntry e in entries)
                {
                    int entityTypeId = await _codeResolver.EntityTypeIdAsync(e.Entity.EntityTypeCode, ct);
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(entityTypeId,   NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(e.Entity.Hash,  NpgsqlDbType.Bytea,   ct);
                    await writer.WriteAsync(e.ReferenceId,  NpgsqlDbType.Integer, ct);
                    if (hasMu)
                    {
                        if (e.Mu.HasValue)
                        {
                            await writer.WriteAsync(e.Mu.Value, NpgsqlDbType.Double, ct);
                        }
                        else
                        {
                            await writer.WriteNullAsync(ct);
                        }
                    }
                }
                await writer.CompleteAsync(ct);
            }

            string insertSql = hasMu
                ? $"INSERT INTO substrate.{table} (entity_type_id, entity_hash, {refCol}, mu) " +
                  $"SELECT DISTINCT entity_type_id, entity_hash, ref_id, COALESCE(mu, 1500.0) " +
                  $"FROM {stagingTable} ON CONFLICT DO NOTHING"
                : $"INSERT INTO substrate.{table} (entity_type_id, entity_hash, {refCol}) " +
                  $"SELECT DISTINCT entity_type_id, entity_hash, ref_id " +
                  $"FROM {stagingTable} ON CONFLICT DO NOTHING";

            await using NpgsqlCommand insertCmd = new(insertSql, conn);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── CreatePhysicalitiesAsync ───────────────────────────────────────────
    // COPY (physicality_type_id, entity_type_id, entity_hash, content_hash, geom)
    // content_hash = BLAKE3(WKB) deduplicates within (type, entity).
    private async Task CreatePhysicalitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Physicalities.Count == 0)
        {
            return;
        }

        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_physicality (" +
            "  physicality_type_id INT NOT NULL, " +
            "  entity_type_id INT NOT NULL, " +
            "  entity_hash BYTEA NOT NULL, " +
            "  content_hash BYTEA NOT NULL, " +
            "  wkb BYTEA NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_physicality (physicality_type_id, entity_type_id, entity_hash, content_hash, wkb) FROM STDIN (FORMAT binary)", ct))
        {
            foreach (PhysicalityEntry phys in batch.Physicalities)
            {
                int physTypeId    = await _codeResolver.PhysicalityTypeIdAsync(phys.PhysicalityTypeCode, ct);
                int entityTypeId  = await _codeResolver.EntityTypeIdAsync(phys.Entity.EntityTypeCode, ct);
                byte[] contentHash = Blake3.Hash(phys.Wkb);

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(physTypeId,        NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(entityTypeId,      NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(phys.Entity.Hash,  NpgsqlDbType.Bytea,   ct);
                await writer.WriteAsync(contentHash,       NpgsqlDbType.Bytea,   ct);
                await writer.WriteAsync(phys.Wkb,          NpgsqlDbType.Bytea,   ct);
            }
            await writer.CompleteAsync(ct);
        }

        // Drain staging_physicality via named substrate function (AP-2).
        await using NpgsqlCommand flushPhys = new(
            "SELECT substrate.flush_physicality_from_staging()", conn);
        await flushPhys.ExecuteNonQueryAsync(ct);
    }

    // ── InitializeEntitySignificanceAsync ──────────────────────────────────
    // Writes only entity_significance rows. Edge significance is primed in
    // bulk by SignificanceFieldRunner against substrate.edge_significance —
    // not per-batch.
    private async Task InitializeEntitySignificanceAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Significances.Count == 0)
        {
            return;
        }

        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_entity_significance (" +
            "  context_type_id INT NOT NULL, " +
            "  entity_type_id INT NOT NULL, " +
            "  entity_hash BYTEA NOT NULL, " +
            "  mu FLOAT8 NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_entity_significance (context_type_id, entity_type_id, entity_hash, mu) FROM STDIN (FORMAT binary)", ct))
        {
            foreach (SignificanceEntry sig in batch.Significances)
            {
                int contextId    = await _codeResolver.SignificanceContextIdAsync(sig.ContextTypeCode, ct);
                int entityTypeId = await _codeResolver.EntityTypeIdAsync(sig.Entity.EntityTypeCode, ct);

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(contextId,        NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(entityTypeId,     NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(sig.Entity.Hash,  NpgsqlDbType.Bytea,   ct);
                await writer.WriteAsync(sig.InitialMu,    NpgsqlDbType.Double,  ct);
            }
            await writer.CompleteAsync(ct);
        }

        // Drain staging_entity_significance via named substrate function (AP-2).
        await using NpgsqlCommand flushSig = new(
            "SELECT substrate.flush_entity_significance_from_staging()", conn);
        await flushSig.ExecuteNonQueryAsync(ct);
    }

    // ── LinkEntityModelSourcesAsync ────────────────────────────────────────
    private async Task LinkEntityModelSourcesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.EntityModelSources.Count == 0)
        {
            return;
        }

        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_entity_model_source (" +
            "  entity_type_id INT NOT NULL, " +
            "  entity_hash BYTEA NOT NULL, " +
            "  model_source_id INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_entity_model_source (entity_type_id, entity_hash, model_source_id) FROM STDIN (FORMAT binary)", ct))
        {
            foreach (EntityModelSourceEntry e in batch.EntityModelSources)
            {
                int entityTypeId = await _codeResolver.EntityTypeIdAsync(e.Entity.EntityTypeCode, ct);
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(entityTypeId,       NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(e.Entity.Hash,      NpgsqlDbType.Bytea,   ct);
                await writer.WriteAsync((int)e.ModelSourceId, NpgsqlDbType.Integer, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using NpgsqlCommand insertCmd = new(
            "INSERT INTO substrate.entity_model_source (entity_type_id, entity_hash, model_source_id) " +
            "SELECT DISTINCT entity_type_id, entity_hash, model_source_id " +
            "FROM staging_entity_model_source " +
            "ON CONFLICT DO NOTHING", conn);
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    // ── PopulateEdgeTrajectoriesAsync ──────────────────────────────────────
    // Calls a substrate function (defined alongside the schema in a later
    // migration) that processes WHERE geom IS NULL in LIMIT-bounded chunks
    // until none remain. No BIGINT id-range arithmetic — there is no id.
    public async Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        const int ChunkSize = 5_000;

        long totalUpdated = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
            await using (NpgsqlCommand setCmd = new(
                "SET max_parallel_workers_per_gather = 0; " +
                "SET max_parallel_maintenance_workers = 0; " +
                "SET work_mem = '256MB';", conn))
            {
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            await using NpgsqlCommand updateCmd = new(
                "SELECT substrate.populate_edge_trajectories($1)", conn);
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = ChunkSize });
            updateCmd.CommandTimeout = 600;

            object? raw = await updateCmd.ExecuteScalarAsync(ct);
            long updated = raw is long u ? u : (raw is int i ? i : 0L);
            if (updated <= 0)
            {
                break;
            }
            totalUpdated += updated;
        }

        if (totalUpdated > 0)
        {
            Log.EdgeTrajectoriesPopulated(_logger, totalUpdated);
        }
    }

    // ── ComputeEdgeHash ────────────────────────────────────────────────────
    // BLAKE3 over [edge_type_id (4 LE bytes) | hash1 (32 bytes) | hash2 ...].
    // Stable identity for a typed n-ary edge: same participants in the same
    // role-ordered sequence under the same edge type produce the same hash.
    private static byte[] ComputeEdgeHash(int edgeTypeId, byte[][] orderedMemberHashes)
    {
        int len = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            len += orderedMemberHashes[i].Length;
        }
        byte[] buffer = new byte[len];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), edgeTypeId);
        int offset = 4;
        for (int i = 0; i < orderedMemberHashes.Length; i++)
        {
            orderedMemberHashes[i].CopyTo(buffer.AsSpan(offset));
            offset += orderedMemberHashes[i].Length;
        }
        return Blake3.Hash(buffer);
    }

    // ── Junction allowlist + ref column mapping ────────────────────────────
    // Junction allowlist. entity_sense was REMOVED — sense is captured by
    // has_sense edges (lemma → synset) and rated via substrate.edge_significance,
    // not via a parallel junction. entity_lexname ADDED — bounded-vocabulary
    // lexname classification (45 lexicographer files), polymorphic on entity_type.
    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.Ordinal)
    {
        "entity_pos", "entity_lexname", "entity_language", "entity_morph_feature",
        "model_architecture_class", "tensor_tensor_role", "pattern_deprel",
    };

    private static string GetJunctionRefColumn(string table) => table switch
    {
        "entity_pos"               => "pos_id",
        "entity_lexname"           => "lexname_id",
        "entity_language"          => "language_id",
        "entity_morph_feature"     => "morph_feature_id",
        "model_architecture_class" => "architecture_class_id",
        "tensor_tensor_role"       => "tensor_role_id",
        "pattern_deprel"           => "deprel_id",
        _ => throw new ArgumentException($"Unknown junction table: '{table}'", nameof(table)),
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Batch committed: {EntityCount} entities, {EdgeCount} edges in {Elapsed}")]
        public static partial void BatchCommitted(ILogger logger, int entityCount, int edgeCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Information, Message = "Edge trajectories populated: {Count} edges updated")]
        public static partial void EdgeTrajectoriesPopulated(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Error, Message = "Batch failed: {EntityCount} entities")]
        public static partial void BatchFailed(ILogger logger, int entityCount, Exception ex);
    }
}
