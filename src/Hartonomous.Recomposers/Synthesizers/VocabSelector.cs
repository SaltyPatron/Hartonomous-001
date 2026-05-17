using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Selects the target model's vocabulary by querying the substrate for the
/// top-N word_form entities ordered by cross-WF connectivity. Adds a
/// deterministic ordering so re-runs produce the same vocab indices.
/// </summary>
public static class VocabSelector
{
    // Per-entity-type vocab selection. For each entity_type in
    // entityTypeQuotas, select top-N entities ranked by total edge
    // participation count (universal connectivity, NOT cross-cohort-specific).
    // Union the results into a single ranked vocab list. Classification
    // cohorts (pos, morph_feature, deprel) typically include ALL entries
    // since they're bounded-cardinality anchors.
    //
    // The deeper architectural reframe: vocab is the substrate's entity
    // graph slice, not just word_forms. Including pos/morph/synset/lang_name
    // gives the model first-class classification anchors to attend to.
    private const string SelectVocabSql = @"
WITH params AS (
    SELECT unnest(@type_codes::text[]) AS entity_type_code,
           unnest(@type_quotas::int[]) AS quota
),
typed_entities AS (
    SELECT ec.entity_hash AS hash, et.code AS type_code
      FROM substrate.entity_classification ec
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
     WHERE et.code IN (SELECT entity_type_code FROM params)
),
edge_degree AS (
    SELECT em.entity_hash AS hash, count(*) AS deg
      FROM substrate.edge_member em
      JOIN typed_entities te ON te.hash = em.entity_hash
     GROUP BY em.entity_hash
),
ranked AS (
    SELECT te.hash,
           te.type_code,
           coalesce(ed.deg, 0)::bigint AS deg,
           ROW_NUMBER() OVER (PARTITION BY te.type_code ORDER BY coalesce(ed.deg, 0) DESC, te.hash) AS rk
      FROM typed_entities te
      LEFT JOIN edge_degree ed ON ed.hash = te.hash
)
SELECT r.hash, r.deg
  FROM ranked r
  JOIN params p ON p.entity_type_code = r.type_code
 WHERE r.rk <= p.quota
 ORDER BY r.deg DESC, r.hash";

    public static async Task<IReadOnlyList<VocabToken>> SelectAsync(
        NpgsqlDataSource dataSource,
        int vocabSize,
        CancellationToken ct,
        IReadOnlyDictionary<string, int>? entityTypeQuotas = null)
    {
        IReadOnlyDictionary<string, int> quotas =
            entityTypeQuotas ?? VocabSelectionSection.DefaultEntityTypeQuotas;

        // If caller passed an absolute vocab_size cap, scale quotas
        // proportionally to honor it. Otherwise use quotas as-is.
        int totalQuota = 0;
        foreach (int q in quotas.Values)
        {
            totalQuota += q;
        }
        IReadOnlyDictionary<string, int> effectiveQuotas;
        if (vocabSize > 0 && totalQuota > vocabSize)
        {
            double scale = (double)vocabSize / totalQuota;
            Dictionary<string, int> scaled = new(quotas.Count, StringComparer.Ordinal);
            foreach ((string k, int q) in quotas)
            {
                scaled[k] = Math.Max(1, (int)(q * scale));
            }
            effectiveQuotas = scaled;
        }
        else
        {
            effectiveQuotas = quotas;
        }

        string[] typeCodes = new string[effectiveQuotas.Count];
        int[] typeQuotas = new int[effectiveQuotas.Count];
        {
            int i = 0;
            foreach ((string k, int q) in effectiveQuotas)
            {
                typeCodes[i] = k;
                typeQuotas[i] = q;
                i++;
            }
        }

        List<VocabToken> rows = new(vocabSize > 0 ? vocabSize : totalQuota);

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(SelectVocabSql, conn);
        cmd.CommandTimeout = 1800; // edge_member self-join can be expensive on full substrate
        NpgsqlParameter typeCodesParam = new("type_codes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = typeCodes,
        };
        NpgsqlParameter typeQuotasParam = new("type_quotas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = typeQuotas,
        };
        cmd.Parameters.Add(typeCodesParam);
        cmd.Parameters.Add(typeQuotasParam);

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

    // Entity-role POINTZM physicality on word_form (or codepoint) entities
    // is the brick's representative real-coord centroid (codepoint
    // Super-Fibonacci S^3 by UCA rank; word_form aggregated centroid).
    // physicality_type='entity' + GeometryType=POINT selects only those
    // rows — composition LINESTRINGZMs live in the same partition but a
    // different shape.
    private const string LoadCentroidsSql = @"
SELECT p.entity_hash,
       ST_X(p.geom)::double precision AS x,
       ST_Y(p.geom)::double precision AS y,
       ST_Z(p.geom)::double precision AS z,
       ST_M(p.geom)::double precision AS m
  FROM substrate.physicality p
  JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
 WHERE pt.code = 'entity'
   AND GeometryType(p.geom) = 'POINT'
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
                // Word_form without entity-role POINTZM physicality — leave
                // centroid zero. EmbeddingSynthesizer falls back to
                // deterministic hash-derived pseudo-coords when centroid is
                // zero.
                attached.Add(t);
            }
        }

        return attached;
    }
}
