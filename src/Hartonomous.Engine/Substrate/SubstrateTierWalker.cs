using System;
using System.Buffers;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Substrate;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Engine.Substrate;

/// <summary>
/// Server-side tier walker over <c>substrate.physicality</c>
/// LINESTRINGZM trajectories. Per tier the walker calls
/// <c>substrate.get_composition_children(parent_hash)</c> for each parent
/// in the previous tier — the function reads the parent's LINESTRINGZM
/// geom, unpacks mantissa-packed vertices via <c>bb_unpack_*</c>, and
/// joins against the <c>(hash_bits_0_51, hash_bits_52_103)</c> composite
/// btree to recover child hashes. Yields one <see cref="TierFrame"/> per
/// depth; depth 0 is the root tier.
///
/// <para>
/// One Npgsql round trip per tier per parent — for compositions with
/// fanout F at each of D depths, that's D × F round trips total. The
/// native fast path (PG SRF <c>pg_expand_trajectory</c> wrapping
/// <c>hartonomous_trajectory_unpack</c> with batched SPI JOIN) collapses
/// this to one round trip per tier regardless of fanout; this SQL-driven
/// implementation is the correct-behavior version that the SRF will
/// replace for hot paths.
/// </para>
///
/// <para>
/// <see cref="ReconstructTextAsync"/> delegates to
/// <c>substrate.recompose_text(root, max_depth)</c> — the substrate-side
/// recursive walk that concatenates leaf codepoint bytes. One round
/// trip for the whole reconstruction.
/// </para>
/// </summary>
public sealed class SubstrateTierWalker : ITierWalker
{
    private readonly NpgsqlDataSource _dataSource;

    public SubstrateTierWalker(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TierFrame> WalkAsync(
        EntityHandle root,
        int maxDepth,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Tier 0 = the root itself.
        List<EntityHandle> currentTier = new(capacity: 1) { root };
        yield return new TierFrame(0, currentTier);

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();

            List<EntityHandle> nextTier = new();
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

            foreach (EntityHandle parent in currentTier)
            {
                ct.ThrowIfCancellationRequested();

                await using NpgsqlCommand cmd = new(
                    "SELECT child_hash FROM substrate.get_composition_children($1) ORDER BY ordinal",
                    conn);
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Bytea,
                    Value = parent.Hash.ToByteArray()
                });

                await using NpgsqlDataReader reader =
                    await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
                while (await reader.ReadAsync(ct))
                {
                    byte[] childHashBytes = (byte[])reader.GetValue(0);
                    Hash32 childHash = new(childHashBytes);
                    // EntityHandle requires a type code; the walker doesn't
                    // know it without a per-child lookup. For the read-side
                    // walker we pass an empty marker — callers that need
                    // the type code resolve via entity_classification.
                    nextTier.Add(new EntityHandle(childHash, "_"));
                }
            }

            if (nextTier.Count == 0)
            {
                yield break;
            }

            yield return new TierFrame(depth, nextTier);
            currentTier = nextTier;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> ReconstructTextAsync(
        EntityHandle root,
        CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.recompose_text($1, 100000)",
            conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bytea,
            Value = root.Hash.ToByteArray()
        });

        object? result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull)
        {
            return null;
        }
        return (string)result;
    }
}
