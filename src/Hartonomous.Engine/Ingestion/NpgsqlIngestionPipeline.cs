using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
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
    private long _batchesCommitted;
    private long _batchesFailed;
    private TimeSpan _totalCommitTime;

    public NpgsqlIngestionPipeline(string connectionString, ILogger<NpgsqlIngestionPipeline> logger)
    {
        NpgsqlDataSourceBuilder builder = new(connectionString);
        _dataSource = builder.Build();
        _codeResolver = new CodeResolver(_dataSource);
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

            await tx.CommitAsync(ct);

            Interlocked.Add(ref _entitiesSubmitted, b.EntityCount);
            Interlocked.Add(ref _edgesSubmitted, b.EdgeCount);
            Interlocked.Add(ref _junctionsSubmitted, b.Junctions.Count);
            Interlocked.Add(ref _physicalitiesSubmitted, b.Physicalities.Count);
            Interlocked.Add(ref _sequencesSubmitted, b.Sequences.Count);
            Interlocked.Add(ref _significanceInitialized, b.Significances.Count);
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
        Dictionary<byte[], long> result = new(ByteArrayComparer.Instance);
        if (hashes.Count == 0)
        {
            return result;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT hash, id FROM substrate.entity WHERE hash = ANY($1)", conn);
        cmd.Parameters.AddWithValue(HashesToByteArrays(hashes));

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byte[] hash = (byte[])reader[0];
            long id = reader.GetInt64(1);
            result[hash] = id;
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private async Task UpsertEntitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        for (int i = 0; i < batch.Entities.Count; i++)
        {
            IngestionBatch.EntityEntry entity = batch.Entities[i];
            int typeId = await _codeResolver.EntityTypeIdAsync(entity.EntityTypeCode, ct);

            await using NpgsqlCommand cmd = new(
                "CALL substrate.upsert_entity($1, $2, NULL, NULL)", conn);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, entity.Hash);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, typeId);

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                long entityId = reader.GetInt64(0);
                batch.RemapHandle(i, entityId);
            }
        }
    }

    private async Task CreateEdgesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        foreach (IngestionBatch.EdgeEntry edge in batch.Edges)
        {
            int edgeTypeId = await _codeResolver.EdgeTypeIdAsync(edge.EdgeTypeCode, ct);
            int provenanceId = await _codeResolver.ProvenanceIdAsync(edge.ProvenanceCode, ct);

            long[] memberEntityIds = new long[edge.Members.Length];
            int[] memberRoleIds = new int[edge.Members.Length];

            for (int i = 0; i < edge.Members.Length; i++)
            {
                memberEntityIds[i] = batch.ResolveHandleOrExisting(
                    edge.Members[i].Handle, edge.Members[i].ExistingEntityId);
                memberRoleIds[i] = await _codeResolver.EdgeRoleIdAsync(edge.Members[i].RoleCode, ct);
            }

            byte[] edgeHash = ComputeEdgeHash(edgeTypeId, memberEntityIds);

            await using NpgsqlCommand cmd = new(
                "CALL substrate.create_edge(NULL, NULL, $1, $2, $3, NULL, $4, $5)", conn);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, edgeHash);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, edgeTypeId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, provenanceId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint, memberEntityIds);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer, memberRoleIds);

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task PopulateJunctionsAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        foreach (IngestionBatch.JunctionEntry junction in batch.Junctions)
        {
            long entityId = batch.ResolveHandle(junction.Entity);
            string table = junction.JunctionTable;

            if (junction.Mu.HasValue)
            {
                await using NpgsqlCommand cmd = new(
                    $"INSERT INTO substrate.{table} (entity_id, {GetJunctionRefColumn(table)}, mu) " +
                    $"VALUES ($1, $2, $3) ON CONFLICT DO NOTHING", conn);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, entityId);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, junction.ReferenceId);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Double, junction.Mu.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using NpgsqlCommand cmd = new(
                    $"INSERT INTO substrate.{table} (entity_id, {GetJunctionRefColumn(table)}) " +
                    $"VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, entityId);
                cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, junction.ReferenceId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private async Task CreatePhysicalitiesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        foreach (IngestionBatch.PhysicalityEntry phys in batch.Physicalities)
        {
            long entityId = batch.ResolveHandle(phys.Entity);
            int typeId = await _codeResolver.PhysicalityTypeIdAsync(phys.PhysicalityTypeCode, ct);

            await using NpgsqlCommand cmd = new(
                "CALL substrate.create_physicality($1, $2, ST_GeomFromWKB($3, 4326), NULL)", conn);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, entityId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, typeId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, phys.GeomWkb);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task CreateSequencesAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        foreach (IngestionBatch.SequenceEntry seq in batch.Sequences)
        {
            long parentId = batch.ResolveHandle(seq.Parent);
            long childId = batch.ResolveHandle(seq.Child);

            await using NpgsqlCommand cmd = new(
                "CALL substrate.create_sequence($1, $2, $3, $4)", conn);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, parentId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, childId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, seq.Position);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, seq.Count);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task InitializeSignificanceAsync(NpgsqlConnection conn, IngestionBatch batch, CancellationToken ct)
    {
        foreach (IngestionBatch.SignificanceEntry sig in batch.Significances)
        {
            long entityId = batch.ResolveHandle(sig.Entity);
            int contextId = await _codeResolver.SignificanceContextIdAsync(sig.ContextTypeCode, ct);

            await using NpgsqlCommand cmd = new(
                "CALL substrate.initialize_significance($1, NULL, $2, $3)", conn);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, entityId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, contextId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Double, sig.InitialMu);
            await cmd.ExecuteNonQueryAsync(ct);
        }
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

    private static byte[] ComputeEdgeHash(int edgeTypeId, long[] memberEntityIds)
    {
        byte[] buffer = new byte[4 + (memberEntityIds.Length * 8)];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), edgeTypeId);
        for (int i = 0; i < memberEntityIds.Length; i++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(4 + (i * 8), 8), memberEntityIds[i]);
        }
        byte[] hash = new byte[32];
        Hartonomous.Core.Native.Blake3Native.Blake3(buffer, (nuint)buffer.Length, hash);
        return hash;
    }

    private static readonly HashSet<string> AllowedJunctionTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "entity_pos", "entity_sense", "entity_language", "entity_morph_feature",
        "codepoint_property", "model_architecture_class", "tensor_tensor_role", "pattern_deprel"
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
            "codepoint_property" => "property_id",
            "model_architecture_class" => "architecture_class_id",
            "tensor_tensor_role" => "tensor_role_id",
            "pattern_deprel" => "deprel_id",
            _ => throw new ArgumentException($"Unknown junction table: '{table}'")
        };
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null)
            {
                return false;
            }
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            unchecked
            {
                int hash = 17;
                int len = Math.Min(obj.Length, 8);
                for (int i = 0; i < len; i++)
                {
                    hash = (hash * 31) + obj[i];
                }
                return hash;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Batch committed: {EntityCount} entities, {EdgeCount} edges in {Elapsed}")]
        public static partial void BatchCommitted(ILogger logger, int entityCount, int edgeCount, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error, Message = "Batch failed: {EntityCount} entities")]
        public static partial void BatchFailed(ILogger logger, int entityCount, Exception ex);
    }
}
