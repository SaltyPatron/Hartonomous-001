using System;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Npgsql;
using NpgsqlTypes;

namespace Hartonomous.Decomposers.Text;

/// <summary>
/// Substrate-side text decomposition entry point. Hands UTF-8 bytes to the
/// C-implemented <c>substrate.text_decompose</c> extension function which
/// performs the entire UAX #29 + BLAKE3 + 4D centroid pipeline in a single
/// SPI call and writes DIRECTLY into the substrate core tables. The C# side
/// receives only the root hash + root entity_type_id (and counts) — the
/// codepoint / grapheme_cluster / word_form / composition entities, their
/// physicalities, sequence rows, and significance rows are emitted by the
/// extension without round-tripping through the C# pipeline channels.
///
/// This replaces the old C#-side <see cref="CanonicalTextDecomposer.Emit"/>
/// path on the hot ingestion surface (TextIngestingDecomposer, prompt
/// ingestion, ModelTextArtifactsPass). Benefits:
///
///   * No <c>NpgsqlCodepointPropertiesCache</c> load — properties come from
///     the embedded UCD blob baked into the extension at build time.
///   * No 1330 LOC of C# segmentation — UAX #29 grapheme/word boundaries
///     run in C against the same generated tables.
///   * No per-codepoint <c>BLAKE3.Hash(byte[])</c> round-trip from C# to
///     native — the extension hashes natively in batch.
///   * No <c>NpgsqlBinaryImporter</c> for codepoint/grapheme/word records —
///     the extension SPI-INSERTs directly with ON CONFLICT DO NOTHING.
///
/// The C extension is the load-bearing path; this class is a thin Npgsql
/// wrapper. Determinism: same UTF-8 input → byte-identical substrate state
/// (Law #6). Cross-decomposer dedup is automatic — content IS the entity.
/// </summary>
public sealed class SubstrateTextDecomposer
{
    private readonly NpgsqlDataSource _dataSource;

    public SubstrateTextDecomposer(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Decompose UTF-8 bytes into the substrate. Single round-trip; on
    /// return, every codepoint/grapheme/word/composition entity + its
    /// physicality + its sequence rows + its significance row are already
    /// in substrate's core tables. The 32-byte root hash and the resolved
    /// root <c>entity_type_id</c> come back so callers can wire downstream
    /// edges (<c>has_text</c>, <c>has_gloss</c>, <c>has_example</c>, etc.)
    /// without re-hashing.
    /// </summary>
    /// <param name="utf8">Document content. Empty input returns an empty
    /// result (<c>RootHash</c> = empty array).</param>
    /// <param name="options">Provenance, top-level entity type, trust prior.</param>
    /// <param name="modelSourceId">Optional model_source link. When supplied,
    /// the extension also emits a <c>substrate.entity_model_source</c> row
    /// for the root composition.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TextDecomposeResult> EmitAsync(
        byte[] utf8,
        TextDecomposeOptions options,
        int? modelSourceId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(utf8);

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            @"SELECT entity_count, edge_count, edge_member_count,
                     physicality_count, sequence_count, significance_count,
                     classification_count, root_hash, root_entity_type_id
                FROM substrate.text_decompose($1, $2, $3, $4, $5)", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = utf8 });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = options.TopEntityType });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = options.TrustMu });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = options.ProvenanceCode });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = (object?)modelSourceId ?? DBNull.Value
        });

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("substrate.text_decompose returned no row");
        }

        long entityCount        = r.IsDBNull(0) ? 0 : r.GetInt64(0);
        // edge_count / edge_member_count are placeholders in the substrate
        // function (text decomposition emits no edges, only entities + members
        // up to the composition root). Discard.
        long physicalityCount   = r.IsDBNull(3) ? 0 : r.GetInt64(3);
        long sequenceCount      = r.IsDBNull(4) ? 0 : r.GetInt64(4);
        long significanceCount  = r.IsDBNull(5) ? 0 : r.GetInt64(5);
        // classification_count = r.GetInt64(6) — informational; not in TextDecomposeResult.
        byte[] rootHash = r.IsDBNull(7) ? Array.Empty<byte>() : (byte[])r.GetValue(7);

        EntityHandle rootHandle = new(options.TopEntityType, rootHash);

        // RootCentroid is left as the default (0,0,0,0). The substrate-side
        // pipeline writes the root composition's POINTZM physicality directly;
        // callers that need the centroid coords post-emission can read them
        // from substrate.physicality. The legacy CanonicalTextDecomposer
        // populated this in-process to save a round-trip; the extension path
        // doesn't need to — substrate IS the source of truth.
        return new TextDecomposeResult(
            RootHandle: rootHandle,
            RootHash: rootHash,
            EntitiesEmitted: entityCount,
            SequenceRowsEmitted: sequenceCount,
            PhysicalityRowsEmitted: physicalityCount,
            SignificanceRowsEmitted: significanceCount,
            RootCentroid: (0.0, 0.0, 0.0, 0.0));
    }
}
