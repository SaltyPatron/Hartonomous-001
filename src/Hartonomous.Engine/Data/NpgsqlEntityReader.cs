using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;
using Npgsql;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Hash-as-PK implementation of <see cref="IEntityReader"/> and
/// <see cref="ITextRecompositionReader"/>.
/// </summary>
public sealed class NpgsqlEntityReader : IEntityReader, ITextRecompositionReader
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlEntityReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<EntityHandle>> ResolveEntityHandlesAsync(
        IReadOnlyList<Hash32> hashes,
        IReadOnlyList<string> entityTypeCodes,
        CancellationToken ct)
    {
        if (hashes.Count == 0 || entityTypeCodes.Count == 0)
        {
            return [];
        }

        byte[][] hashArray = new byte[hashes.Count][];
        for (int index = 0; index < hashes.Count; index++)
        {
            hashArray[index] = hashes[index].ToByteArray();
        }

        string[] typeArray = new string[entityTypeCodes.Count];
        for (int index = 0; index < entityTypeCodes.Count; index++)
        {
            typeArray[index] = entityTypeCodes[index];
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.ResolveEntityHandles,
            new object?[] { hashArray, typeArray });

        List<EntityHandle> result = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            result.Add(new EntityHandle(hash, typeCode));
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GetEntityInfoAsync(
        IReadOnlyList<EntityHandle> entityHandles, CancellationToken ct)
    {
        if (entityHandles.Count == 0)
        {
            return new Dictionary<EntityHandle, EntityInfo>();
        }

        string[] typeCodes = new string[entityHandles.Count];
        byte[][] hashes = new byte[entityHandles.Count][];
        for (int index = 0; index < entityHandles.Count; index++)
        {
            typeCodes[index] = entityHandles[index].EntityTypeCode;
            hashes[index] = entityHandles[index].Hash.ToByteArray();
        }

        Dictionary<EntityHandle, EntityInfo> result = new(entityHandles.Count);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.GetEntityInfoByHandles,
            new object?[] { typeCodes, hashes });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            EntityHandle handle = new(hash, typeCode);
            result[handle] = new EntityInfo
            {
                Handle = handle,
                ContentLabel = null,
            };
        }

        return result;
    }

    public async Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
        EntityHandle parent, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.CompositionRange,
            new object?[] { parent.Hash, 1, int.MaxValue });

        List<(EntityHandle Child, int Position)> children = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            int position = reader.GetInt32(2);
            children.Add((new EntityHandle(hash, typeCode), position));
        }

        return children;
    }

    public async Task<IReadOnlyDictionary<EdgeHandle, EdgeInfo>> GetEdgeInfoAsync(
        IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct)
    {
        if (edgeHandles.Count == 0)
        {
            return new Dictionary<EdgeHandle, EdgeInfo>();
        }

        string[] typeCodes = new string[edgeHandles.Count];
        byte[][] hashes = new byte[edgeHandles.Count][];
        for (int index = 0; index < edgeHandles.Count; index++)
        {
            typeCodes[index] = edgeHandles[index].EdgeTypeCode;
            hashes[index] = edgeHandles[index].Hash.ToByteArray();
        }

        Dictionary<EdgeHandle, EdgeInfo> result = new(edgeHandles.Count);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.GetEdgeInfoByHandles,
            new object?[] { typeCodes, hashes });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string edgeTypeCode = reader.GetString(0).Trim();
            byte[] edgeHash = (byte[])reader.GetValue(1);
            EdgeHandle handle = new(edgeHash, edgeTypeCode);

            EntityHandle? source = null;
            if (!reader.IsDBNull(2) && !reader.IsDBNull(3))
            {
                source = new EntityHandle((byte[])reader.GetValue(3), reader.GetString(2).Trim());
            }

            EntityHandle? target = null;
            if (!reader.IsDBNull(4) && !reader.IsDBNull(5))
            {
                target = new EntityHandle((byte[])reader.GetValue(5), reader.GetString(4).Trim());
            }

            result[handle] = new EdgeInfo
            {
                Handle = handle,
                Source = source,
                Target = target,
            };
        }

        return result;
    }

    public async Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
        string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
    {
        IReadOnlyList<Hash32> candidateHashes = EntityContentHashResolver.GetCandidateHashes(
            content, entityTypeCodes);
        if (candidateHashes.Count == 0)
        {
            return [];
        }

        return await ResolveEntityHandlesAsync(candidateHashes, entityTypeCodes, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
        EntityHandle source, string edgeTypeCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.GetOutboundEdgeTargets,
            new object?[] { source.Hash, edgeTypeCode });

        List<EntityHandle> targets = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            targets.Add(new EntityHandle(hash, typeCode));
        }

        return targets;
    }

    public async Task<string?> RecomposeTextAsync(EntityHandle root, int maxDepth, CancellationToken ct)
    {
        // Delegate to the C# bulk-tier walker in Core (Gate 1 item #36). The
        // walker handles its own connection lifetime — but we own the
        // NpgsqlDataSource here, so a fresh connection per call is the
        // cleanest contract.
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        // maxDepth=0 is a contract violation in the bulk walker; the
        // ITextRecompositionReader caller may pass 0 to mean "default" so
        // clamp upward.
        int depth = maxDepth > 0 ? maxDepth : 16;
        byte[] bytes = await BulkTierContentWalk.RecomposeAsync(conn, root.Hash, depth, ct);
        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }
}
