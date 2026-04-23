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

        const int chunkSize = 50_000;
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        for (int offset = 0; offset < hashes.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, hashes.Count - offset);
            byte[][] chunk = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                chunk[i] = hashes[offset + i];
            }

            await using NpgsqlCommand cmd = new(
                "SELECT hash, id FROM substrate.entity WHERE hash = ANY($1)", conn);
            cmd.Parameters.AddWithValue(chunk);

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                byte[] hash = (byte[])reader[0];
                long id = reader.GetInt64(1);
                result[hash] = id;
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private async Task UpsertEntitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Entities.Count == 0)
        {
            return;
        }

        byte[][] hashes = new byte[batch.Entities.Count][];
        int[] typeIds = new int[batch.Entities.Count];
        for (int i = 0; i < batch.Entities.Count; i++)
        {
            EntityEntry entity = batch.Entities[i];
            hashes[i] = entity.Hash;
            typeIds[i] = await _codeResolver.EntityTypeIdAsync(entity.EntityTypeCode, ct);
        }

        await using (NpgsqlCommand insertCmd = new(
            "INSERT INTO substrate.entity (hash, entity_type_id) " +
            "SELECT * FROM unnest($1::bytea[], $2::int[]) " +
            "ON CONFLICT (hash, entity_type_id) DO NOTHING", conn))
        {
            insertCmd.Parameters.AddWithValue(hashes);
            insertCmd.Parameters.AddWithValue(typeIds);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await using NpgsqlCommand selectCmd = new(
            "SELECT e.id, t.ord FROM " +
            "unnest($1::bytea[], $2::int[]) WITH ORDINALITY AS t(hash, entity_type_id, ord) " +
            "JOIN substrate.entity e ON e.hash = t.hash AND e.entity_type_id = t.entity_type_id", conn);
        selectCmd.Parameters.AddWithValue(hashes);
        selectCmd.Parameters.AddWithValue(typeIds);

        await using NpgsqlDataReader reader = await selectCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long entityId = reader.GetInt64(0);
            int ordinal = (int)reader.GetInt64(1) - 1;
            batch.RemapHandle(ordinal, entityId);
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

        await using (NpgsqlCommand insertCmd = new(
            "INSERT INTO substrate.edge (hash, edge_type_id, provenance_id) " +
            "SELECT * FROM unnest($1::bytea[], $2::int[], $3::int[]) " +
            "ON CONFLICT (hash, edge_type_id) DO NOTHING", conn))
        {
            insertCmd.Parameters.AddWithValue(edgeHashes);
            insertCmd.Parameters.AddWithValue(edgeTypeIds);
            insertCmd.Parameters.AddWithValue(provenanceIds);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await using NpgsqlCommand selectCmd = new(
            "SELECT e.id, t.ord FROM " +
            "unnest($1::bytea[], $2::int[]) WITH ORDINALITY AS t(hash, edge_type_id, ord) " +
            "JOIN substrate.edge e ON e.hash = t.hash AND e.edge_type_id = t.edge_type_id", conn);
        selectCmd.Parameters.AddWithValue(edgeHashes);
        selectCmd.Parameters.AddWithValue(edgeTypeIds);

        long[] resolvedEdgeIds = new long[batch.Edges.Count];
        await using (NpgsqlDataReader reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                long edgeId = reader.GetInt64(0);
                int ordinal = (int)reader.GetInt64(1) - 1;
                resolvedEdgeIds[ordinal] = edgeId;
            }
        }

        List<long> memberEdgeIds = [];
        List<long> memberEntityIdList = [];
        List<int> memberRoleIdList = [];
        for (int i = 0; i < batch.Edges.Count; i++)
        {
            for (int j = 0; j < allMemberEntityIds[i].Length; j++)
            {
                memberEdgeIds.Add(resolvedEdgeIds[i]);
                memberEntityIdList.Add(allMemberEntityIds[i][j]);
                memberRoleIdList.Add(allMemberRoleIds[i][j]);
            }
        }

        if (memberEdgeIds.Count > 0)
        {
            await using NpgsqlCommand memberCmd = new(
                "INSERT INTO substrate.edge_member (edge_id, entity_id, edge_role_id) " +
                "SELECT * FROM unnest($1::bigint[], $2::bigint[], $3::int[]) " +
                "ON CONFLICT DO NOTHING", conn);
            memberCmd.Parameters.AddWithValue(memberEdgeIds.ToArray());
            memberCmd.Parameters.AddWithValue(memberEntityIdList.ToArray());
            memberCmd.Parameters.AddWithValue(memberRoleIdList.ToArray());
            await memberCmd.ExecuteNonQueryAsync(ct);
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

        // Resolve the type id for every entry once, then partition by coordinate
        // surface so each surface gets its own set-based INSERT against the
        // appropriate column on substrate.physicality. Per-partition CHECKs
        // (migrations 0036, 0037) anchor which column each physicality_type
        // partition allows; routing here to the wrong column would trip those.
        List<(long EntityId, int TypeId, byte[] Wkb, byte[] Hash)> postGis = [];
        List<(long EntityId, int TypeId, double[] Coords, byte[] Hash)> point4d = [];
        List<(long EntityId, int TypeId, double[] Coords, byte[] Hash)> lineString4d = [];

        for (int i = 0; i < batch.Physicalities.Count; i++)
        {
            PhysicalityEntry phys = batch.Physicalities[i];
            long entityId = batch.ResolveHandle(phys.Entity);
            int typeId = await _codeResolver.PhysicalityTypeIdAsync(phys.PhysicalityTypeCode, ct);

            switch (phys.Surface)
            {
                case PhysicalitySurface.PostGisGeom:
                    if (phys.PostGisWkb is null)
                    {
                        throw new InvalidOperationException(
                            $"PhysicalityEntry has Surface=PostGisGeom but PostGisWkb is null (type={phys.PhysicalityTypeCode}).");
                    }
                    postGis.Add((
                        entityId,
                        typeId,
                        phys.PostGisWkb,
                        Hartonomous.Core.Compute.Common.Blake3.Hash(phys.PostGisWkb)));
                    break;

                case PhysicalitySurface.Point4D:
                    if (phys.Point4DCoords is null || phys.Point4DCoords.Length != 4)
                    {
                        throw new InvalidOperationException(
                            $"PhysicalityEntry has Surface=Point4D but Point4DCoords is null or not length 4 (type={phys.PhysicalityTypeCode}).");
                    }
                    point4d.Add((
                        entityId,
                        typeId,
                        phys.Point4DCoords,
                        HashFloat8Array(phys.Point4DCoords)));
                    break;

                case PhysicalitySurface.LineString4D:
                    if (phys.LineString4DCoords is null
                        || phys.LineString4DCoords.Length < 4
                        || (phys.LineString4DCoords.Length % 4) != 0)
                    {
                        throw new InvalidOperationException(
                            $"PhysicalityEntry has Surface=LineString4D but LineString4DCoords is null or length is not a positive multiple of 4 (type={phys.PhysicalityTypeCode}).");
                    }
                    lineString4d.Add((
                        entityId,
                        typeId,
                        phys.LineString4DCoords,
                        HashFloat8Array(phys.LineString4DCoords)));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown PhysicalitySurface value {(int)phys.Surface} (type={phys.PhysicalityTypeCode}).");
            }
        }

        if (postGis.Count > 0)
        {
            long[] entityIds = new long[postGis.Count];
            int[] typeIds = new int[postGis.Count];
            byte[][] wkbs = new byte[postGis.Count][];
            byte[][] hashes = new byte[postGis.Count][];
            for (int i = 0; i < postGis.Count; i++)
            {
                entityIds[i] = postGis[i].EntityId;
                typeIds[i] = postGis[i].TypeId;
                wkbs[i] = postGis[i].Wkb;
                hashes[i] = postGis[i].Hash;
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.physicality (entity_id, physicality_type_id, geom, content_hash) "
              + "SELECT e, t, ST_GeomFromWKB(g, 4326), h "
              + "FROM unnest($1::bigint[], $2::int[], $3::bytea[], $4::bytea[]) AS u(e, t, g, h) "
              + "ON CONFLICT (entity_id, physicality_type_id, content_hash) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(entityIds);
            cmd.Parameters.AddWithValue(typeIds);
            cmd.Parameters.AddWithValue(wkbs);
            cmd.Parameters.AddWithValue(hashes);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (point4d.Count > 0)
        {
            long[] entityIds = new long[point4d.Count];
            int[] typeIds = new int[point4d.Count];
            double[] x1s = new double[point4d.Count];
            double[] x2s = new double[point4d.Count];
            double[] x3s = new double[point4d.Count];
            double[] x4s = new double[point4d.Count];
            byte[][] hashes = new byte[point4d.Count][];
            for (int i = 0; i < point4d.Count; i++)
            {
                entityIds[i] = point4d[i].EntityId;
                typeIds[i] = point4d[i].TypeId;
                x1s[i] = point4d[i].Coords[0];
                x2s[i] = point4d[i].Coords[1];
                x3s[i] = point4d[i].Coords[2];
                x4s[i] = point4d[i].Coords[3];
                hashes[i] = point4d[i].Hash;
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.physicality (entity_id, physicality_type_id, pt4d, content_hash) "
              + "SELECT e, t, public.point4d(a, b, c, d), h "
              + "FROM unnest($1::bigint[], $2::int[], $3::float8[], $4::float8[], $5::float8[], $6::float8[], $7::bytea[]) "
              + "AS u(e, t, a, b, c, d, h) "
              + "ON CONFLICT (entity_id, physicality_type_id, content_hash) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(entityIds);
            cmd.Parameters.AddWithValue(typeIds);
            cmd.Parameters.AddWithValue(x1s);
            cmd.Parameters.AddWithValue(x2s);
            cmd.Parameters.AddWithValue(x3s);
            cmd.Parameters.AddWithValue(x4s);
            cmd.Parameters.AddWithValue(hashes);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (lineString4d.Count > 0)
        {
            // Each row's coordinates are encoded as a self-describing bytea in
            // the linestring4d wire format: int32 npoints (network byte order)
            // followed by 4n float8 values (network byte order). Postgres
            // unnest($N::bytea[]) yields one bytea per row without any
            // flattening, so vertex counts may differ row-to-row in the same
            // INSERT. The substrate-side function public.bytea_to_linestring4d
            // decodes each row into a linestring4d. No parallel coordinate
            // arrays, no length-bucketing, no multidim flattening.
            long[]   entityIds = new long[lineString4d.Count];
            int[]    typeIds   = new int[lineString4d.Count];
            byte[][] payloads  = new byte[lineString4d.Count][];
            byte[][] hashes    = new byte[lineString4d.Count][];

            for (int i = 0; i < lineString4d.Count; i++)
            {
                (long entityId, int typeId, double[] coords, byte[] hash) = lineString4d[i];
                entityIds[i] = entityId;
                typeIds[i]   = typeId;
                hashes[i]    = hash;
                payloads[i]  = EncodeLineString4DWireFormat(coords);
            }

            await using NpgsqlCommand cmd = new(
                "INSERT INTO substrate.physicality (entity_id, physicality_type_id, ls4d, content_hash) "
              + "SELECT e, t, public.bytea_to_linestring4d(b), h "
              + "FROM unnest($1::bigint[], $2::int[], $3::bytea[], $4::bytea[]) AS u(e, t, b, h) "
              + "ON CONFLICT (entity_id, physicality_type_id, content_hash) DO NOTHING", conn);
            cmd.Parameters.AddWithValue(entityIds);
            cmd.Parameters.AddWithValue(typeIds);
            cmd.Parameters.AddWithValue(payloads);
            cmd.Parameters.AddWithValue(hashes);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Encode a flat <c>[x1,y1,z1,m1, x2,y2,z2,m2, ...]</c> coordinate buffer
    /// (length must be a positive multiple of 4) into the linestring4d wire
    /// format consumed by <c>public.bytea_to_linestring4d</c>: an int32 npoints
    /// header followed by <c>4 * npoints</c> float8 values, all in network
    /// (big-endian) byte order. Matches <c>pg_linestring4d_recv</c> byte for
    /// byte so the same decoder path is exercised by COPY BINARY round-trips.
    /// </summary>
    private static byte[] EncodeLineString4DWireFormat(double[] coords)
    {
        if (coords.Length == 0 || (coords.Length % 4) != 0)
        {
            throw new ArgumentException(
                $"linestring4d coordinate buffer length {coords.Length} must be a positive multiple of 4.",
                nameof(coords));
        }

        int npoints = coords.Length / 4;
        byte[] payload = new byte[4 + (coords.Length * 8)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), npoints);
        for (int i = 0; i < coords.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(
                payload.AsSpan(4 + (i * 8), 8), coords[i]);
        }
        return payload;
    }

    /// <summary>
    /// Hash a flat float8 sequence as 8 bytes per element, little-endian, in
    /// declaration order. Used as the content hash for both pt4d and ls4d
    /// physicality rows so the (entity_id, physicality_type_id, content_hash)
    /// uniqueness constraint dedupes within and across batches.
    /// </summary>
    private static byte[] HashFloat8Array(double[] coords)
    {
        byte[] bytes = new byte[coords.Length * 8];
        for (int i = 0; i < coords.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
                bytes.AsSpan(i * 8), coords[i]);
        }
        return Hartonomous.Core.Compute.Common.Blake3.Hash(bytes);
    }

    private static async Task CreateSequencesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        if (batch.Sequences.Count == 0)
        {
            return;
        }

        long[] parentIds = new long[batch.Sequences.Count];
        long[] childIds = new long[batch.Sequences.Count];
        int[] positions = new int[batch.Sequences.Count];
        int[] counts = new int[batch.Sequences.Count];

        for (int i = 0; i < batch.Sequences.Count; i++)
        {
            SequenceEntry seq = batch.Sequences[i];
            parentIds[i] = batch.ResolveHandle(seq.Parent);
            childIds[i] = batch.ResolveHandle(seq.Child);
            positions[i] = seq.Position;
            counts[i] = seq.Count;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.sequence (parent_id, child_id, ordinal_position, rle_count) " +
            "SELECT * FROM unnest($1::bigint[], $2::bigint[], $3::int[], $4::int[]) " +
            "ON CONFLICT (parent_id, ordinal_position) DO NOTHING", conn);
        cmd.Parameters.AddWithValue(parentIds);
        cmd.Parameters.AddWithValue(childIds);
        cmd.Parameters.AddWithValue(positions);
        cmd.Parameters.AddWithValue(counts);
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

        long[] entityIds = new long[batch.Significances.Count];
        int[] contextIds = new int[batch.Significances.Count];
        double[] mus = new double[batch.Significances.Count];

        for (int i = 0; i < batch.Significances.Count; i++)
        {
            SignificanceEntry sig = batch.Significances[i];
            entityIds[i] = batch.ResolveHandle(sig.Entity);
            contextIds[i] = await _codeResolver.SignificanceContextIdAsync(sig.ContextTypeCode, ct);
            mus[i] = sig.InitialMu;
        }

        await using NpgsqlCommand cmd = new(
            "INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games) " +
            "SELECT e, NULL, c, m, 350.0, 0.06, 0 " +
            "FROM unnest($1::bigint[], $2::int[], $3::float8[]) AS t(e, c, m) " +
            "ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue(entityIds);
        cmd.Parameters.AddWithValue(contextIds);
        cmd.Parameters.AddWithValue(mus);
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
        // which reads pt4d for s3_position and falls back to centroid_s3 over
        // contour ls4d vertices.
        //
        // Parallel workers disabled per-session: PostGIS geometry constructors
        // invoked from parallel worker backends have crashed the server
        // (signal 11) on freshly ingested phase data.
        const long ChunkSize = 250_000L;

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        await using (NpgsqlCommand setCmd = new(
            "SET max_parallel_workers_per_gather = 0; "
            + "SET max_parallel_maintenance_workers = 0; "
            + "SET work_mem = '1GB';",
            conn))
        {
            await setCmd.ExecuteNonQueryAsync(ct);
        }

        long maxId;
        await using (NpgsqlCommand maxCmd = new(
            "SELECT COALESCE(MAX(id), 0) FROM substrate.edge", conn))
        {
            maxCmd.CommandTimeout = 600;
            object? raw = await maxCmd.ExecuteScalarAsync(ct);
            maxId = raw is long l ? l : 0L;
        }

        if (maxId <= 0)
        {
            return;
        }

        long totalUpdated = 0;
        for (long low = 1; low <= maxId; low += ChunkSize)
        {
            long high = low + ChunkSize;
            await using NpgsqlCommand updateCmd = new(
                "SELECT substrate.populate_edge_trajectories($1, $2)", conn);
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = low });
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = high });
            updateCmd.CommandTimeout = 3600;
            object? result = await updateCmd.ExecuteScalarAsync(ct);
            long updated = result is long u ? u : 0L;
            totalUpdated += updated;
        }

        if (totalUpdated > 0)
        {
            Log.EdgeTrajectoriesPopulated(_logger, totalUpdated);
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Batch failed: {EntityCount} entities")]
        public static partial void BatchFailed(ILogger logger, int entityCount, Exception ex);
    }
}
