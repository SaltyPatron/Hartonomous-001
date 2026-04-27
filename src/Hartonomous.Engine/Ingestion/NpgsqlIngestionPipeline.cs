using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Hartonomous.Core;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

public sealed partial class NpgsqlIngestionPipeline : IIngestionPipeline
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly CodeResolver _codeResolver;
    private readonly ILogger<NpgsqlIngestionPipeline> _logger;

    private long _entitiesSubmitted;
    private long _edgesSubmitted;
    private long _junctionsSubmitted;
    private long _physicalitiesSubmitted;
    private long _sequencesSubmitted;
    private long _significanceInitialized;
    private long _entityModelSourcesLinked;
    private long _batchesCommitted;
    private long _batchesFailed;
    private TimeSpan _totalCommitTime;

    public NpgsqlIngestionPipeline(string connectionString, IReferenceDataReader referenceDataReader, ILogger<NpgsqlIngestionPipeline> logger)
    {
        NpgsqlDataSourceBuilder builder = new(connectionString);
        _dataSource = builder.Build();
        _codeResolver = new CodeResolver(referenceDataReader);
        _logger = logger;
    }

    public PipelineStats Stats => new()
    {
        EntitiesSubmitted = _entitiesSubmitted,
        EdgesSubmitted = _edgesSubmitted,
        JunctionsSubmitted = _junctionsSubmitted,
        PhysicalitiesSubmitted = _physicalitiesSubmitted,
        SequencesSubmitted = _sequencesSubmitted,
        SignificanceInitialized = _significanceInitialized,
        EntityModelSourcesLinked = _entityModelSourcesLinked,
        BatchesCommitted = _batchesCommitted,
        BatchesFailed = _batchesFailed,
        TotalCommitTime = _totalCommitTime,
    };

    public IIngestionBatch CreateBatch()
    {
        return new IngestionBatch();
    }

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
            await DetectCorroborationsAsync(conn, b, ct);
            await CreateEdgesAsync(conn, b, ct);
            await PopulateJunctionsAsync(conn, b, ct);
            await CreatePhysicalitiesAsync(conn, b, ct);
            await CreateSequencesAsync(conn, b, ct);
            await InitializeSignificanceAsync(conn, b, ct);
            await LinkEntityModelSourcesAsync(conn, b, ct);

            await tx.CommitAsync(ct);

            Interlocked.Add(ref _entitiesSubmitted, b.EntityCount);
            Interlocked.Add(ref _edgesSubmitted, b.EdgeCount);
            Interlocked.Add(ref _junctionsSubmitted, b.Junctions.Count);
            Interlocked.Add(ref _physicalitiesSubmitted, b.Physicalities.Count);
            Interlocked.Add(ref _sequencesSubmitted, b.Sequences.Count);
            Interlocked.Add(ref _significanceInitialized, b.Significances.Count);
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

    public async Task<IReadOnlyDictionary<byte[], long>> ResolveEntityIdsAsync(
        IReadOnlyList<byte[]> hashes, CancellationToken ct)
    {
        Dictionary<byte[], long> result = new(ByteArrayEqualityComparer.Instance);
        if (hashes.Count == 0)
        {
            return result;
        }

        // Replaces the prior SELECT ... WHERE hash = ANY($1) bytea[] pattern,
        // which OOM-killed postgres backends at WordNet scale (millions of
        // hashes against a LIST-partitioned table — array expansion + 16-way
        // partition scan exhausted backend memory). Pattern: binary COPY all
        // hashes into a TEMP staging table once, then JOIN against
        // substrate.entity by hash. The JOIN uses the unique (hash,
        // entity_type_id) index per partition, scaling linearly in result
        // count instead of quadratically in (input × partitions).
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        // CREATE TEMP TABLE ... ON COMMIT DROP must run inside an explicit
        // transaction or the implicit autocommit fires immediately and drops
        // the table before the COPY can populate it. Wrap the whole resolve
        // (create / copy / select) in one transaction so all three statements
        // see the same staging table.
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync(ct);

        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_resolve_hash (hash BYTEA NOT NULL) ON COMMIT DROP", conn, tx))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_resolve_hash (hash) FROM STDIN (FORMAT binary)", ct))
        {
            foreach (byte[] hash in hashes)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(hash, NpgsqlTypes.NpgsqlDbType.Bytea, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using NpgsqlCommand selectCmd = new(
            "SELECT e.hash, e.id FROM substrate.entity e " +
            "JOIN staging_resolve_hash s ON e.hash = s.hash", conn, tx);
        selectCmd.CommandTimeout = 600;

        await using (NpgsqlDataReader reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                byte[] hash = (byte[])reader[0];
                long id = reader.GetInt64(1);
                result[hash] = id;
            }
        }

        await tx.CommitAsync(ct);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    /// <summary>
    /// Fires Glicko-2 corroboration updates for entities in this batch that
    /// some OTHER model_source has previously contributed. Delegates entirely
    /// to substrate.detect_and_record_corroborations (migration 0050) — no
    /// inline SQL, set-based join against staging_entity, one round-trip per
    /// distinct contributing model_source_id.
    /// </summary>
    private static async Task DetectCorroborationsAsync(
        NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.EntityModelSources.Count == 0)
        {
            return;
        }

        HashSet<long> distinctModelSources = new();
        for (int i = 0; i < batch.EntityModelSources.Count; i++)
        {
            distinctModelSources.Add(batch.EntityModelSources[i].ModelSourceId);
        }

        foreach (long modelSourceId in distinctModelSources)
        {
            await using NpgsqlCommand cmd = new(
                "SELECT substrate.detect_and_record_corroborations($1)", conn);
            cmd.Parameters.AddWithValue(modelSourceId);
            await cmd.ExecuteScalarAsync(ct);
        }
    }

    private async Task UpsertEntitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Entities.Count == 0)
        {
            return;
        }

        // Pre-resolve entity_type ids to int once to avoid per-row dictionary lookups
        // inside the binary writer hot loop.
        int[] typeIds = new int[batch.Entities.Count];
        for (int i = 0; i < batch.Entities.Count; i++)
        {
            typeIds[i] = await _codeResolver.EntityTypeIdAsync(
                batch.Entities[i].EntityTypeCode, ct);
        }

        // Binary COPY into a transaction-local staging table, then dedup-INSERT
        // into substrate.entity. Replaces the prior INSERT...SELECT FROM unnest()
        // pattern which (a) hit a Postgres segfault on the LIST-partitioned
        // entity table at Wiktionary-scale volume, and (b) was 5–10× slower than
        // binary COPY for batches > ~5K rows. Staging table includes an `ord`
        // column so we can round-trip the BatchIndex back to the caller for
        // EntityHandle remapping without an array-of-bytea round trip.
        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_entity (" +
            "  ord INT NOT NULL, " +
            "  hash BYTEA NOT NULL, " +
            "  entity_type_id INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_entity (ord, hash, entity_type_id) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Entities.Count; i++)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(i, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(batch.Entities[i].Hash, NpgsqlTypes.NpgsqlDbType.Bytea, ct);
                await writer.WriteAsync(typeIds[i], NpgsqlTypes.NpgsqlDbType.Integer, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using (NpgsqlCommand insertCmd = new(
            "INSERT INTO substrate.entity (hash, entity_type_id) " +
            "SELECT DISTINCT hash, entity_type_id FROM staging_entity " +
            "ON CONFLICT (hash, entity_type_id) DO NOTHING", conn))
        {
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        // Resolve all (hash, type_id) → entity_id. Pure set-based join, no
        // correlated subqueries. The previous EXISTS-on-entity_model_source
        // path crashed Postgres backends under UCD-scale load (SIGSEGV during
        // query execution; reproducible across multiple runs). Corroboration
        // evidence accumulation needs to be rebuilt as a separate, batched
        // pass that runs after entity resolution — not inline in the ID
        // resolution query.
        await using (NpgsqlCommand selectCmd = new(@"
            SELECT s.ord, e.id
              FROM staging_entity s
              JOIN substrate.entity e
                ON e.hash = s.hash AND e.entity_type_id = s.entity_type_id", conn))
        {
            await using NpgsqlDataReader reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                int ordinal = reader.GetInt32(0);
                long entityId = reader.GetInt64(1);
                batch.RemapHandle(ordinal, entityId);
            }
        }
    }

    private async Task CreateEdgesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Edges.Count == 0)
        {
            return;
        }

        byte[][] edgeHashes = new byte[batch.Edges.Count][];
        int[] edgeTypeIds = new int[batch.Edges.Count];
        int[] provenanceIds = new int[batch.Edges.Count];
        long[][] allMemberEntityIds = new long[batch.Edges.Count][];
        int[][] allMemberRoleIds = new int[batch.Edges.Count][];

        for (int i = 0; i < batch.Edges.Count; i++)
        {
            EdgeEntry edge = batch.Edges[i];
            edgeTypeIds[i] = await _codeResolver.EdgeTypeIdAsync(edge.EdgeTypeCode, ct);
            provenanceIds[i] = await _codeResolver.ProvenanceIdAsync(edge.ProvenanceCode, ct);

            long[] memberEntityIds = new long[edge.Members.Length];
            int[] memberRoleIds = new int[edge.Members.Length];
            for (int j = 0; j < edge.Members.Length; j++)
            {
                memberEntityIds[j] = batch.ResolveHandleOrExisting(
                    edge.Members[j].Handle, edge.Members[j].ExistingEntityId);
                memberRoleIds[j] = await _codeResolver.EdgeRoleIdAsync(edge.Members[j].RoleCode, ct);
            }

            allMemberEntityIds[i] = memberEntityIds;
            allMemberRoleIds[i] = memberRoleIds;
            edgeHashes[i] = ComputeEdgeHash(edgeTypeIds[i], memberEntityIds);
        }

        // Binary COPY into staging — same rationale as UpsertEntitiesAsync. Avoids
        // the partitioned-table-with-array-unnest segfault and is 5-10× faster
        // for batches above a few thousand rows.
        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_edge (" +
            "  ord INT NOT NULL, " +
            "  hash BYTEA NOT NULL, " +
            "  edge_type_id INT NOT NULL, " +
            "  provenance_id INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_edge (ord, hash, edge_type_id, provenance_id) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Edges.Count; i++)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(i, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(edgeHashes[i], NpgsqlTypes.NpgsqlDbType.Bytea, ct);
                await writer.WriteAsync(edgeTypeIds[i], NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(provenanceIds[i], NpgsqlTypes.NpgsqlDbType.Integer, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using (NpgsqlCommand insertCmd = new(
            "INSERT INTO substrate.edge (hash, edge_type_id, provenance_id) " +
            "SELECT DISTINCT ON (hash, edge_type_id) hash, edge_type_id, provenance_id " +
            "FROM staging_edge " +
            "ON CONFLICT (hash, edge_type_id) DO NOTHING", conn))
        {
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        long[] resolvedEdgeIds = new long[batch.Edges.Count];
        await using (NpgsqlCommand selectCmd = new(
            "SELECT s.ord, e.id FROM staging_edge s " +
            "JOIN substrate.edge e ON e.hash = s.hash AND e.edge_type_id = s.edge_type_id", conn))
        await using (NpgsqlDataReader reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                int ordinal = reader.GetInt32(0);
                long edgeId = reader.GetInt64(1);
                resolvedEdgeIds[ordinal] = edgeId;
            }
        }

        // ── edge_member binary COPY ──────────────────────────────────────
        int totalMembers = 0;
        for (int i = 0; i < batch.Edges.Count; i++)
        {
            totalMembers += allMemberEntityIds[i].Length;
        }
        if (totalMembers == 0)
        {
            return;
        }

        await using (NpgsqlCommand createMemberTemp = new(
            "CREATE TEMP TABLE staging_edge_member (" +
            "  edge_id BIGINT NOT NULL, " +
            "  entity_id BIGINT NOT NULL, " +
            "  edge_role_id INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createMemberTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter memberWriter = await conn.BeginBinaryImportAsync(
            "COPY staging_edge_member (edge_id, entity_id, edge_role_id) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Edges.Count; i++)
            {
                long edgeId = resolvedEdgeIds[i];
                long[] memberEntityIds = allMemberEntityIds[i];
                int[] memberRoleIds = allMemberRoleIds[i];
                for (int j = 0; j < memberEntityIds.Length; j++)
                {
                    await memberWriter.StartRowAsync(ct);
                    await memberWriter.WriteAsync(edgeId, NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                    await memberWriter.WriteAsync(memberEntityIds[j], NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                    await memberWriter.WriteAsync(memberRoleIds[j], NpgsqlTypes.NpgsqlDbType.Integer, ct);
                }
            }
            await memberWriter.CompleteAsync(ct);
        }

        await using (NpgsqlCommand memberInsert = new(
            "INSERT INTO substrate.edge_member (edge_id, entity_id, edge_role_id) " +
            "SELECT DISTINCT edge_id, entity_id, edge_role_id FROM staging_edge_member " +
            "ON CONFLICT DO NOTHING", conn))
        {
            await memberInsert.ExecuteNonQueryAsync(ct);
        }

        // Prime edge significance from provenance trust prior across every
        // arena currently in substrate.significance_context. Delegated to
        // substrate.prime_edge_significance_for_staging (migration 0052) so
        // C# does not construct SQL — per AP-2 in .claude/rules/45-anti-patterns.md.
        // Open-vocabulary: new arenas added later get backfilled into existing
        // edges via substrate.backfill_edge_significance_for_arena(code).
        await using (NpgsqlCommand primeSig = new(
            "SELECT substrate.prime_edge_significance_for_staging()", conn))
        {
            await primeSig.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task PopulateJunctionsAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Junctions.Count == 0)
        {
            return;
        }

        Dictionary<string, List<JunctionEntry>> grouped = new(StringComparer.Ordinal);
        foreach (JunctionEntry junction in batch.Junctions)
        {
            if (!grouped.TryGetValue(junction.JunctionTable, out List<JunctionEntry>? list))
            {
                list = [];
                grouped[junction.JunctionTable] = list;
            }
            list.Add(junction);
        }

        foreach (KeyValuePair<string, List<JunctionEntry>> kv in grouped)
        {
            string table = kv.Key;
            string refCol = GetJunctionRefColumn(table);
            List<JunctionEntry> entries = kv.Value;

            long[] entityIds = new long[entries.Count];
            int[] refIds = new int[entries.Count];
            double?[] mus = new double?[entries.Count];
            bool hasMu = false;

            for (int i = 0; i < entries.Count; i++)
            {
                entityIds[i] = batch.ResolveHandle(entries[i].Entity);
                refIds[i] = entries[i].ReferenceId;
                mus[i] = entries[i].Mu;
                hasMu |= entries[i].Mu.HasValue;
            }

            if (hasMu)
            {
                await using NpgsqlCommand cmd = new(
                    $"INSERT INTO substrate.{table} (entity_id, {refCol}, mu) " +
                    $"SELECT * FROM unnest($1::bigint[], $2::int[], $3::float8[]) " +
                    $"ON CONFLICT DO NOTHING", conn);
                cmd.Parameters.AddWithValue(entityIds);
                cmd.Parameters.AddWithValue(refIds);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double, mus);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using NpgsqlCommand cmd = new(
                    $"INSERT INTO substrate.{table} (entity_id, {refCol}) " +
                    $"SELECT * FROM unnest($1::bigint[], $2::int[]) " +
                    $"ON CONFLICT DO NOTHING", conn);
                cmd.Parameters.AddWithValue(entityIds);
                cmd.Parameters.AddWithValue(refIds);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private async Task CreatePhysicalitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Physicalities.Count == 0)
        {
            return;
        }

        // Single coordinate surface: every PhysicalityEntry carries WKB bytes
        // for substrate.physicality.geom. PostGIS's ST_GeomFromWKB accepts
        // POINT/POINTZ/POINTZM/LINESTRING/LINESTRINGZ/LINESTRINGZM uniformly;
        // per-partition CHECKs (migration 0048) reject geometries whose
        // dimensionality doesn't match the partition's declared type.
        if (batch.Physicalities.Count == 0)
        {
            return;
        }

        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_physicality (" +
            "  entity_id BIGINT NOT NULL, " +
            "  physicality_type_id INT NOT NULL, " +
            "  wkb BYTEA NOT NULL, " +
            "  content_hash BYTEA NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_physicality (entity_id, physicality_type_id, wkb, content_hash) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Physicalities.Count; i++)
            {
                PhysicalityEntry phys = batch.Physicalities[i];
                long entityId = batch.ResolveHandle(phys.Entity);
                int typeId = await _codeResolver.PhysicalityTypeIdAsync(phys.PhysicalityTypeCode, ct);
                byte[] hash = Hartonomous.Core.Compute.Common.Blake3.Hash(phys.Wkb);

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(entityId, NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(typeId, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(phys.Wkb, NpgsqlTypes.NpgsqlDbType.Bytea, ct);
                await writer.WriteAsync(hash, NpgsqlTypes.NpgsqlDbType.Bytea, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using NpgsqlCommand insertCmd = new(
            "INSERT INTO substrate.physicality (entity_id, physicality_type_id, geom, content_hash) " +
            "SELECT entity_id, physicality_type_id, ST_GeomFromWKB(wkb), content_hash " +
            "FROM staging_physicality " +
            "ON CONFLICT (entity_id, physicality_type_id, content_hash) DO NOTHING", conn);
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CreateSequencesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Sequences.Count == 0)
        {
            return;
        }

        // Binary COPY into staging — sequence rows can grow into the millions for
        // a single Moby-Dick-class document (21K text_compositions × ~30 children
        // = 600K+ rows). The unnest() pattern serializes the entire array into a
        // single command parameter; binary COPY streams row-by-row.
        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_sequence (" +
            "  parent_id BIGINT NOT NULL, " +
            "  child_id BIGINT NOT NULL, " +
            "  ordinal_position INT NOT NULL, " +
            "  rle_count INT NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_sequence (parent_id, child_id, ordinal_position, rle_count) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Sequences.Count; i++)
            {
                SequenceEntry seq = batch.Sequences[i];
                long parentId = seq.ParentEntityId ?? batch.ResolveHandle(seq.ParentHandle!.Value);
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(parentId, NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(batch.ResolveHandle(seq.Child), NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(seq.Position, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(seq.Count, NpgsqlTypes.NpgsqlDbType.Integer, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.sequence (parent_id, child_id, ordinal_position, rle_count) " +
            "SELECT DISTINCT ON (parent_id, ordinal_position) parent_id, child_id, ordinal_position, rle_count " +
            "FROM staging_sequence " +
            "ON CONFLICT (parent_id, ordinal_position) DO NOTHING", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task LinkEntityModelSourcesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.EntityModelSources.Count == 0)
        {
            return;
        }

        long[] entityIds = new long[batch.EntityModelSources.Count];
        int[] entityTypeIds = new int[batch.EntityModelSources.Count];
        long[] sourceIds = new long[batch.EntityModelSources.Count];
        for (int i = 0; i < batch.EntityModelSources.Count; i++)
        {
            EntityModelSourceEntry link = batch.EntityModelSources[i];
            entityIds[i] = batch.ResolveHandle(link.Entity);
            string typeCode = batch.Entities[link.Entity.BatchIndex].EntityTypeCode;
            entityTypeIds[i] = await _codeResolver.EntityTypeIdAsync(typeCode, ct);
            sourceIds[i] = link.ModelSourceId;
        }

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.link_entity_model_sources($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(entityIds);
        cmd.Parameters.AddWithValue(entityTypeIds);
        cmd.Parameters.AddWithValue(sourceIds);
        await cmd.ExecuteScalarAsync(ct);
    }

    private async Task InitializeSignificanceAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Significances.Count == 0)
        {
            return;
        }

        // Pre-resolve context_type ids before the binary writer hot loop.
        int[] contextIds = new int[batch.Significances.Count];
        for (int i = 0; i < batch.Significances.Count; i++)
        {
            contextIds[i] = await _codeResolver.SignificanceContextIdAsync(
                batch.Significances[i].ContextTypeCode, ct);
        }

        await using (NpgsqlCommand createTemp = new(
            "CREATE TEMP TABLE staging_significance (" +
            "  entity_id BIGINT NOT NULL, " +
            "  context_type_id INT NOT NULL, " +
            "  mu FLOAT8 NOT NULL" +
            ") ON COMMIT DROP", conn))
        {
            await createTemp.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY staging_significance (entity_id, context_type_id, mu) FROM STDIN (FORMAT binary)", ct))
        {
            for (int i = 0; i < batch.Significances.Count; i++)
            {
                SignificanceEntry sig = batch.Significances[i];
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(batch.ResolveHandle(sig.Entity), NpgsqlTypes.NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(contextIds[i], NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(sig.InitialMu, NpgsqlTypes.NpgsqlDbType.Double, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games) " +
            "SELECT entity_id, NULL, context_type_id, mu, 350.0, 0.06, 0 " +
            "FROM staging_significance " +
            "ON CONFLICT DO NOTHING", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static byte[][] HashesToByteArrays(IReadOnlyList<byte[]> hashes)
    {
        byte[][] result = new byte[hashes.Count][];
        for (int i = 0; i < hashes.Count; i++)
        {
            result[i] = hashes[i];
        }
        return result;
    }

    public async Task PopulateEdgeTrajectoriesAsync(CancellationToken ct)
    {
        // substrate.populate_edge_trajectories(p_id_low, p_id_high) (migration
        // 0038) does a single bounded UPDATE over edge ids in [low, high)
        // whose geom IS NULL. The caller drives id-range iteration so each
        // invocation is one bounded transaction — chunk size caps planner
        // memory and a crash inside one chunk only loses that chunk's work.
        //
        // Endpoints are resolved per-edge through substrate.entity_s3_point,
        // which reads the GeometryZM POINTZM for s3_position and falls back
        // to centroid_s3 over contour LINESTRINGZM vertices (post-0048).
        //
        // Parallel workers disabled per-session: PostGIS geometry constructors
        // invoked from parallel worker backends have crashed the server
        // (signal 11) on freshly ingested phase data.
        //
        // Chunk size dropped from 50K to 5K — populate_edge_trajectories has
        // a history of OOM-killing the postgres backend (signal 9) on
        // freshly-ingested model phase data because per-edge centroid resolution
        // pulls 4D physicality geometry into the planner's working set. At 50K
        // chunks across 30K+ ingested edges per model the cumulative pressure
        // still tips PG over. 5K keeps the working set firmly bounded.
        const long ChunkSize = 5_000L;

        long maxId;
        await using (NpgsqlConnection probeConn = await _dataSource.OpenConnectionAsync(ct))
        await using (NpgsqlCommand maxCmd = new(
            "SELECT COALESCE(MAX(id), 0) FROM substrate.edge", probeConn))
        {
            maxCmd.CommandTimeout = 600;
            object? raw = await maxCmd.ExecuteScalarAsync(ct);
            maxId = raw is long l ? l : 0L;
        }

        if (maxId <= 0)
        {
            return;
        }

        // Each chunk runs on its OWN connection — if PG dies inside one
        // chunk's UPDATE (OOM, signal 11), the multiplexed reader for that
        // connection is dead but the next chunk gets a fresh one. Earlier
        // single-connection design propagated one chunk's crash into a
        // "Timeout during reading attempt" multiplexing failure that
        // prevented every subsequent chunk from running.
        long totalUpdated = 0;
        long failedChunks = 0;
        for (long low = 1; low <= maxId; low += ChunkSize)
        {
            long high = low + ChunkSize;
            try
            {
                await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
                await using (NpgsqlCommand setCmd = new(
                    "SET max_parallel_workers_per_gather = 0; "
                    + "SET max_parallel_maintenance_workers = 0; "
                    + "SET work_mem = '256MB';",
                    conn))
                {
                    await setCmd.ExecuteNonQueryAsync(ct);
                }

                await using NpgsqlCommand updateCmd = new(
                    "SELECT substrate.populate_edge_trajectories($1, $2)", conn);
                updateCmd.Parameters.Add(new NpgsqlParameter { Value = low });
                updateCmd.Parameters.Add(new NpgsqlParameter { Value = high });
                updateCmd.CommandTimeout = 600;
                object? result = await updateCmd.ExecuteScalarAsync(ct);
                long updated = result is long u ? u : 0L;
                totalUpdated += updated;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) // BOUNDARY: per-chunk isolation — one bad chunk doesn't block remaining chunks; reported aggregate failure count at end.
            {
                failedChunks++;
                Log.EdgeTrajectoryChunkFailed(_logger, low, high, ex.Message);
            }
        }

        if (totalUpdated > 0)
        {
            Log.EdgeTrajectoriesPopulated(_logger, totalUpdated);
        }
        if (failedChunks > 0)
        {
            Log.EdgeTrajectoryChunksFailed(_logger, failedChunks);
        }
    }

    private static byte[] ComputeEdgeHash(int edgeTypeId, long[] memberEntityIds)
    {
        byte[] buffer = new byte[4 + (memberEntityIds.Length * 8)];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), edgeTypeId);
        for (int i = 0; i < memberEntityIds.Length; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(4 + (i * 8), 8), memberEntityIds[i]);
        }
        return Hartonomous.Core.Compute.Common.Blake3.Hash(buffer);
    }

    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "entity_pos", "entity_sense", "entity_language", "entity_morph_feature",
        "model_architecture_class", "tensor_tensor_role", "pattern_deprel"
    };

    private static string GetJunctionRefColumn(string table)
    {
        if (!AllowedJunctionTables.Contains(table))
        {
            throw new ArgumentException($"Unknown junction table: '{table}'");
        }

        return table switch
        {
            "entity_pos" => "pos_id",
            "entity_sense" => "sense_id",
            "entity_language" => "language_id",
            "entity_morph_feature" => "morph_feature_id",
            "model_architecture_class" => "architecture_class_id",
            "tensor_tensor_role" => "tensor_role_id",
            "pattern_deprel" => "deprel_id",
            _ => throw new ArgumentException($"Unknown junction table: '{table}'")
        };
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Batch committed: {EntityCount} entities, {EdgeCount} edges in {Elapsed}")]
        public static partial void BatchCommitted(ILogger logger, int entityCount, int edgeCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Information, Message = "Edge trajectories populated: {Count} edges updated")]
        public static partial void EdgeTrajectoriesPopulated(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Edge trajectory chunk [{Low},{High}) failed: {Reason} — skipped, continuing with remaining chunks")]
        public static partial void EdgeTrajectoryChunkFailed(ILogger logger, long low, long high, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Edge trajectory population finished with {ChunkCount} failed chunks (re-runnable via `phases run --phase ModelDecomp --force` or `ops trajectories`)")]
        public static partial void EdgeTrajectoryChunksFailed(ILogger logger, long chunkCount);

        [LoggerMessage(Level = LogLevel.Error, Message = "Batch failed: {EntityCount} entities")]
        public static partial void BatchFailed(ILogger logger, int entityCount, Exception ex);
    }
}
