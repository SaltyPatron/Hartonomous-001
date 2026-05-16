using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// One row in the target model's vocabulary, sourced from the substrate.
/// <see cref="EntityHash"/> is the BLAKE3 of the word_form's content bytes
/// (canonical text decomposer output); <see cref="TokenText"/> is the
/// surface form for tokenizer.json; <see cref="EdgeCount"/> is the
/// substrate-measured prominence (used to rank for vocab selection); the
/// 4D centroid (<see cref="CentroidX"/>..<see cref="CentroidM"/>) is the
/// word_form's representative 4D position read out of the s3_position
/// physicality partition.
/// </summary>
public sealed record VocabToken(
    int Index,
    byte[] EntityHash,
    string TokenText,
    long EdgeCount,
    double CentroidX,
    double CentroidY,
    double CentroidZ,
    double CentroidM);

/// <summary>
/// Selects the target model's vocabulary by querying the substrate for the
/// top-N word_form entities ordered by outgoing edge count (a substrate-
/// computed prominence proxy). Adds a deterministic ordering so re-runs
/// produce the same vocab indices.
/// </summary>
public static class VocabSelector
{
    // Cross-word_form connectivity ranking. The substrate's universal-graph
    // edges fall into two classes for a word_form: (a) word_form ↔ word_form
    // (synonym, antonym, translation_of, derived, inflection_of, etym_*, UD
    // deprel patterns, model attention patterns); (b) word_form ↔ other-entity
    // (has_pos → pos, has_sense → synset, has_lemma → lemma, has_gloss →
    // text_composition, has_pronunciation → text_composition).
    //
    // Vocab selection MUST rank by cross-word_form connectivity — class (a)
    // — because that's what populates the V×V adjacency matrix that
    // Laplacian-eigenmap / Ritz-pair synthesis consumes. Ranking by total
    // edge_count picks the/of/and (heavy class (b) — many glosses /
    // pronunciations / examples) which have very few edges to OTHER
    // word_forms.
    //
    // The query below counts cross-word_form edge participations: for each
    // word_form, how many edges does it participate in whose OTHER
    // participant(s) are also word_form? Ordered descending; deterministic
    // tie-break by hash.
    private const string SelectVocabSql = @"
WITH wf_entity AS (
    SELECT ec.entity_hash AS hash
      FROM substrate.entity_classification ec
     WHERE ec.entity_type_id = (SELECT id FROM substrate.entity_type WHERE code = 'word_form')
),
cross_wf_degree AS (
    SELECT em0.entity_hash AS hash, count(*) AS cross_wf_count
      FROM substrate.edge_member em0
      JOIN substrate.edge_member em1
        ON em1.edge_type_id = em0.edge_type_id
       AND em1.edge_hash = em0.edge_hash
       AND em1.entity_hash <> em0.entity_hash
      JOIN wf_entity wf0 ON wf0.hash = em0.entity_hash
      JOIN wf_entity wf1 ON wf1.hash = em1.entity_hash
     GROUP BY em0.entity_hash
)
SELECT hash,
       cross_wf_count
  FROM cross_wf_degree
 ORDER BY cross_wf_count DESC, hash
 LIMIT @vocab_size";

    public static async Task<IReadOnlyList<VocabToken>> SelectAsync(
        NpgsqlDataSource dataSource,
        int vocabSize,
        CancellationToken ct)
    {
        List<VocabToken> rows = new(vocabSize);

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(SelectVocabSql, conn);
        cmd.CommandTimeout = 1800; // cross-WF degree query is a triple self-join over edge_member; allow up to 30 min
        cmd.Parameters.AddWithValue("vocab_size", vocabSize);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int idx = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] hash = (byte[])reader.GetValue(0);
            long edgeCount = reader.GetInt64(1);
            // TokenText: hex of hash for now — TokenizerExporter will resolve to
            // the actual surface form via substrate.recompose_text() in a
            // follow-up; for the first export, hex hash is a placeholder that
            // makes the tokenizer.json deterministic + content-addressed.
            string tokenText = $"<wf_{System.Convert.ToHexString(hash).ToLowerInvariant().AsSpan(0, 16)}>";
            rows.Add(new VocabToken(
                Index: idx++,
                EntityHash: hash,
                TokenText: tokenText,
                EdgeCount: edgeCount,
                CentroidX: 0, CentroidY: 0, CentroidZ: 0, CentroidM: 0));
        }

        return rows;
    }

    private const string LoadCentroidsSql = @"
SELECT p.entity_hash,
       ST_X(p.geom)::double precision AS x,
       ST_Y(p.geom)::double precision AS y,
       ST_Z(p.geom)::double precision AS z,
       ST_M(p.geom)::double precision AS m
  FROM substrate.physicality p
  JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
 WHERE pt.code = 's3_position'
   AND p.entity_hash = ANY(@hashes)";

    public static async Task<IReadOnlyList<VocabToken>> AttachCentroidsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<VocabToken> tokens,
        CancellationToken ct)
    {
        byte[][] hashes = new byte[tokens.Count][];
        for (int i = 0; i < tokens.Count; i++)
        {
            hashes[i] = tokens[i].EntityHash;
        }

        Dictionary<string, (double X, double Y, double Z, double M)> byHashHex =
            new(StringComparer.Ordinal);

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(LoadCentroidsSql, conn);
        cmd.CommandTimeout = 600;
        NpgsqlParameter hashesParam = new("hashes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea)
        {
            Value = hashes,
        };
        cmd.Parameters.Add(hashesParam);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] hash = (byte[])reader.GetValue(0);
            byHashHex[System.Convert.ToHexString(hash)] =
                (reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4));
        }

        List<VocabToken> attached = new(tokens.Count);
        foreach (VocabToken t in tokens)
        {
            if (byHashHex.TryGetValue(System.Convert.ToHexString(t.EntityHash),
                out (double X, double Y, double Z, double M) c))
            {
                attached.Add(t with
                {
                    CentroidX = c.X,
                    CentroidY = c.Y,
                    CentroidZ = c.Z,
                    CentroidM = c.M
                });
            }
            else
            {
                // Word_form without s3_position physicality — leave centroid zero.
                // EmbeddingSynthesizer falls back to deterministic hash-derived
                // pseudo-coords when centroid is zero.
                attached.Add(t);
            }
        }

        return attached;
    }
}
