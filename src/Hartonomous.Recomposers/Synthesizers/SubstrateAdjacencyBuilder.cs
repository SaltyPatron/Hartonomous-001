using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// CSR adjacency of the substrate's per-arena edge_significance matrix
/// over a finite vocab of entity hashes.
///
/// Builds the V×V sparse symmetric matrix
///   <c>W[i,j] = Σ_arena |mu(edge(vocab[i], vocab[j], arena)) − 1500| · w_arena</c>
/// where <c>w_arena</c> is <see cref="RecompositionOptions.ArenaWeights"/>
/// after L1 normalization. mu = 1500 means "neutral / no Glicko drift," so
/// the deviation magnitude carries the substrate's actual signal. Sign is
/// recovered downstream (attention synth — Q^T·K can be negative; FFN —
/// memory slot may suppress). Multi-arena blend follows the recipe.
///
/// Each row is symmetrized (<c>W = (W + W^T) / 2</c>) so the resulting
/// matrix is suitable for Laplacian eigenmap (<see cref="Hartonomous.Core.Compute.Ingestion.LaplacianEigenmap"/>)
/// and sparse symmetric eigendecomposition (<see cref="Hartonomous.Core.Compute.Ingestion.SparseSymEigs"/>).
/// The diagonal is left zero — the Laplacian construction adds self-loops
/// implicitly via D − W.
///
/// Substrate query: enumerate binary edges whose two participants are both
/// in vocab (role_position 0 + 1 self-join on substrate.edge_member),
/// JOIN substrate.edge_significance per arena.
/// </summary>
// SubstrateAdjacency class lives in SubstrateAdjacency.cs (one-type-per-file).
public static class SubstrateAdjacencyBuilder
{
    public static async Task<SubstrateAdjacency> BuildAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<VocabToken> vocab,
        RecompositionOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(options);

        int n = vocab.Count;
        byte[][] hashes = new byte[n][];
        Dictionary<string, int> hashHexToIndex = new(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            hashes[i] = vocab[i].EntityHash;
            hashHexToIndex[Convert.ToHexString(vocab[i].EntityHash)] = i;
        }

        Dictionary<(int i, int j), double> upperTriangular = new();

        Dictionary<string, double> arenaWeights = NormalizeArenaWeights(options.ArenaWeights);

        // Two-channel adjacency.
        //
        // (1) DIRECT — edges whose BOTH role-position participants are
        //     word_forms in vocab (synonym / antonym / translation_of /
        //     derived / inflection_of / etym_* / UD deprel patterns).
        //     "rake-synonym-tool" sits here.
        //
        // (2) INDIRECT — 2-hop path through a shared CLASSIFICATION
        //     intermediate (pos / synset / lemma / language /
        //     morph_feature / lexname). Two word_forms that both attest
        //     `has_pos(NOUN)` are co-noun; both attesting `has_sense(synset_X)`
        //     are co-synset (synonyms via a shared sense); both attesting
        //     `has_lemma(L)` are co-inflection (variants of one lemma).
        //     This IS the "rake-NOUN-dog" cosine the user described —
        //     transformer hidden-state similarity expressed in substrate.
        //
        // Restricted to classification-entity intermediates because:
        //   - text_composition intermediates (gloss / example / pronunciation /
        //     etymology) blow up the join (millions of glosses; one gloss
        //     is shared by hundreds of word_forms via has_gloss).
        //   - word_form intermediates would form 3-cycles in DIRECT.
        // The classification intermediate set is bounded-cardinality
        // (~thousands per type, not millions) so the join stays planar.
        const string Sql = @"
WITH vocab(hash) AS (
    SELECT DISTINCT unnest(@hashes)
),
classification_intermediates AS (
    SELECT entity_hash AS inter_hash
      FROM substrate.entity_classification
     WHERE entity_type_id IN (
         SELECT id FROM substrate.entity_type
          WHERE code IN ('pos', 'synset', 'lemma', 'language_name',
                         'morph_feature', 'lexname', 'sense', 'deprel')
     )
),
direct_edges AS (
    SELECT em0.entity_hash AS hash_a, em1.entity_hash AS hash_b,
           em0.edge_type_id, em0.edge_hash
      FROM substrate.edge_member em0
      JOIN substrate.edge_member em1
        ON em1.edge_type_id = em0.edge_type_id
       AND em1.edge_hash = em0.edge_hash
       AND em1.role_position > em0.role_position
      JOIN vocab va ON va.hash = em0.entity_hash
      JOIN vocab vb ON vb.hash = em1.entity_hash
),
-- For the indirect channel, first restrict to edges whose at least one
-- participant is in vocab AND whose other participant is a classification
-- entity. Then self-join via the classification intermediate.
vocab_to_classification AS (
    SELECT em_v.entity_hash AS vocab_hash,
           em_c.entity_hash AS inter_hash,
           em_v.edge_type_id, em_v.edge_hash
      FROM substrate.edge_member em_v
      JOIN substrate.edge_member em_c
        ON em_c.edge_type_id = em_v.edge_type_id
       AND em_c.edge_hash = em_v.edge_hash
       AND em_c.entity_hash <> em_v.entity_hash
      JOIN vocab v ON v.hash = em_v.entity_hash
      JOIN classification_intermediates ci ON ci.inter_hash = em_c.entity_hash
),
indirect_edges AS (
    SELECT vca.vocab_hash AS hash_a, vcb.vocab_hash AS hash_b,
           vca.edge_type_id AS edge_type_id_a, vca.edge_hash AS edge_hash_a,
           vcb.edge_type_id AS edge_type_id_b, vcb.edge_hash AS edge_hash_b
      FROM vocab_to_classification vca
      JOIN vocab_to_classification vcb
        ON vcb.inter_hash = vca.inter_hash
       AND vcb.vocab_hash > vca.vocab_hash
)
SELECT 'direct'::text AS channel, de.hash_a, de.hash_b, sc.code AS arena_code,
       es.mu AS mu_a, es.games AS games_a,
       0.0::double precision AS mu_b, 0 AS games_b
  FROM direct_edges de
  JOIN substrate.edge_significance es
    ON es.edge_type_id = de.edge_type_id
   AND es.edge_hash = de.edge_hash
  JOIN substrate.significance_context sc ON sc.id = es.context_type_id
 WHERE sc.code = ANY(@arenas)
   AND es.games > 0
UNION ALL
SELECT 'indirect'::text AS channel, ie.hash_a, ie.hash_b, sc_a.code AS arena_code,
       es_a.mu AS mu_a, es_a.games AS games_a,
       es_b.mu AS mu_b, es_b.games AS games_b
  FROM indirect_edges ie
  JOIN substrate.edge_significance es_a
    ON es_a.edge_type_id = ie.edge_type_id_a
   AND es_a.edge_hash = ie.edge_hash_a
  JOIN substrate.edge_significance es_b
    ON es_b.edge_type_id = ie.edge_type_id_b
   AND es_b.edge_hash = ie.edge_hash_b
   AND es_b.context_type_id = es_a.context_type_id
  JOIN substrate.significance_context sc_a ON sc_a.id = es_a.context_type_id
 WHERE sc_a.code = ANY(@arenas)
   AND es_a.games > 0
   AND es_b.games > 0
";

        string[] arenaCodes = new string[arenaWeights.Count];
        {
            int k = 0;
            foreach (string a in arenaWeights.Keys)
            {
                arenaCodes[k++] = a;
            }
        }

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(Sql, conn);
        cmd.CommandTimeout = 1800; // 2-hop self-join across edge_member can be expensive on full corpus
        NpgsqlParameter hashesParam = new("hashes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea)
        {
            Value = hashes,
        };
        NpgsqlParameter arenasParam = new("arenas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = arenaCodes,
        };
        cmd.Parameters.Add(hashesParam);
        cmd.Parameters.Add(arenasParam);

        // Scoring channels (log-magnitude scaled):
        //   DIRECT:   contribution = w_arena * log1p(|mu_a - 1500|)
        //   INDIRECT: contribution = w_arena * log1p(|mu_a - 1500|) * log1p(|mu_b - 1500|) / log1p(1500)
        //
        // Why log1p: per-arena mu deviations span 8+ orders of magnitude in
        // the substrate today. Source_authority arena carries trust priors
        // initialized at ~100,000 (unicode_consortium) down to 20,000
        // (user_session). Evidence-drift arenas like lexical_disambiguation /
        // semantic_relevance / syntactic_role_fitness are typically 10..50
        // mu-units off the 1500 baseline per Glicko event. Linear blending
        // means source_authority's 100k drowns every other arena's 50.
        // log1p(100000) = 11.5, log1p(50) = 3.93 — same ordering, same sign,
        // but the trust-prior arena no longer dominates the spectrum.
        //
        // Deterministic (Law #6) — log1p is bitwise-identical across runs.
        const double LogNorm = 7.314; // log1p(1500) — keeps INDIRECT comparable to DIRECT

        long rowsSeen = 0;
        long rowsRejectedHashLookup = 0;
        long rowsRejectedArena = 0;
        long rowsRejectedMagnitude = 0;
        long rowsAcceptedDirect = 0;
        long rowsAcceptedIndirect = 0;
        Dictionary<string, long> arenaCounts = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rowsSeen++;
            string channel = reader.GetString(0);
            byte[] ha = (byte[])reader.GetValue(1);
            byte[] hb = (byte[])reader.GetValue(2);
            string arenaCode = reader.GetString(3);
            double muA = reader.GetDouble(4);
            double muB = reader.GetDouble(6);

            arenaCounts.TryGetValue(arenaCode, out long arenaCount);
            arenaCounts[arenaCode] = arenaCount + 1;

            if (!hashHexToIndex.TryGetValue(Convert.ToHexString(ha), out int i)
                || !hashHexToIndex.TryGetValue(Convert.ToHexString(hb), out int j))
            {
                rowsRejectedHashLookup++;
                continue;
            }
            if (i == j)
            {
                continue;
            }
            if (!arenaWeights.TryGetValue(arenaCode, out double w))
            {
                rowsRejectedArena++;
                continue;
            }

            double devA = Math.Abs(muA - 1500.0);
            double contribution;
            if (channel == "direct")
            {
                if (devA < 1e-9)
                {
                    rowsRejectedMagnitude++;
                    continue;
                }
                contribution = w * Math.Log(1.0 + devA);
                rowsAcceptedDirect++;
            }
            else
            {
                double devB = Math.Abs(muB - 1500.0);
                if (devA < 1e-9 || devB < 1e-9)
                {
                    rowsRejectedMagnitude++;
                    continue;
                }
                contribution = w * Math.Log(1.0 + devA) * Math.Log(1.0 + devB) / LogNorm;
                rowsAcceptedIndirect++;
            }

            int lo = Math.Min(i, j);
            int hi = Math.Max(i, j);
            (int, int) key = (lo, hi);
            upperTriangular.TryGetValue(key, out double cur);
            upperTriangular[key] = cur + contribution;
        }

        Console.Out.WriteLine(
            $"SubstrateAdjacency build telemetry: rowsSeen={rowsSeen} "
            + $"rejectedHash={rowsRejectedHashLookup} rejectedArena={rowsRejectedArena} "
            + $"rejectedMag={rowsRejectedMagnitude} "
            + $"acceptedDirect={rowsAcceptedDirect} acceptedIndirect={rowsAcceptedIndirect}");
        if (arenaCounts.Count > 0)
        {
            Console.Out.WriteLine("  arena breakdown:");
            foreach ((string ac, long count) in arenaCounts)
            {
                bool whitelisted = arenaWeights.ContainsKey(ac);
                Console.Out.WriteLine($"    {ac,-30} {count,10} (in recipe: {whitelisted})");
            }
        }

        SortedDictionary<int, SortedDictionary<int, double>> byRow = new();
        foreach (((int i, int j) edge, double w) in upperTriangular)
        {
            AddToRow(byRow, edge.i, edge.j, w);
            AddToRow(byRow, edge.j, edge.i, w);
        }

        long nnz = 0;
        foreach (KeyValuePair<int, SortedDictionary<int, double>> kv in byRow)
        {
            nnz += kv.Value.Count;
        }

        long[] rowPtr = new long[n + 1];
        long[] colIdx = new long[nnz];
        double[] values = new double[nnz];
        double[] rowL1 = new double[n];
        long isoCount = 0;

        long cursor = 0;
        for (int i = 0; i < n; i++)
        {
            rowPtr[i] = cursor;
            if (!byRow.TryGetValue(i, out SortedDictionary<int, double>? cols))
            {
                continue;
            }
            double rowSum = 0;
            foreach (KeyValuePair<int, double> col in cols)
            {
                colIdx[cursor] = col.Key;
                values[cursor] = col.Value;
                rowSum += col.Value;
                cursor++;
            }
            rowL1[i] = rowSum;
            if (rowSum > 0)
            {
                isoCount++;
            }
        }
        rowPtr[n] = cursor;

        return new SubstrateAdjacency
        {
            N = n,
            Nnz = nnz,
            RowPtr = rowPtr,
            ColIdx = colIdx,
            Values = values,
            RowL1 = rowL1,
            NonIsolatedNodes = isoCount,
        };
    }

    private static void AddToRow(
        SortedDictionary<int, SortedDictionary<int, double>> byRow,
        int row, int col, double w)
    {
        if (!byRow.TryGetValue(row, out SortedDictionary<int, double>? cols))
        {
            cols = new SortedDictionary<int, double>();
            byRow[row] = cols;
        }
        cols.TryGetValue(col, out double cur);
        cols[col] = cur + w;
    }

    private static Dictionary<string, double> NormalizeArenaWeights(
        System.Collections.Immutable.ImmutableDictionary<string, double> arenaWeights)
    {
        if (arenaWeights.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["lexical_disambiguation"] = 1.0,
                ["semantic_relevance"] = 1.0,
                ["syntactic_role_fitness"] = 1.0,
                ["translation_quality"] = 1.0,
            };
        }
        double sum = 0;
        foreach (double w in arenaWeights.Values)
        {
            if (w < 0)
            {
                throw new ArgumentException("arena weight must be non-negative");
            }
            sum += w;
        }
        if (sum <= 0)
        {
            throw new ArgumentException("at least one arena weight must be positive");
        }
        Dictionary<string, double> normalized = new(arenaWeights.Count, StringComparer.Ordinal);
        foreach ((string k, double w) in arenaWeights)
        {
            normalized[k] = w / sum;
        }
        return normalized;
    }

    public static string DebugCsrSummary(SubstrateAdjacency adj)
    {
        StringBuilder sb = new();
        CultureInfo ci = CultureInfo.InvariantCulture;
        sb.AppendLine(ci, $"SubstrateAdjacency: n={adj.N}, nnz={adj.Nnz}, non_isolated={adj.NonIsolatedNodes}");
        double minV = double.PositiveInfinity, maxV = double.NegativeInfinity, sumV = 0;
        for (long k = 0; k < adj.Nnz; k++)
        {
            double v = adj.Values[k];
            if (v < minV)
            {
                minV = v;
            }
            if (v > maxV)
            {
                maxV = v;
            }
            sumV += v;
        }
        if (adj.Nnz > 0)
        {
            sb.AppendLine(ci, $"  value range: [{minV.ToString("G6", ci)}, {maxV.ToString("G6", ci)}], mean={(sumV / adj.Nnz).ToString("G6", ci)}");
        }
        return sb.ToString();
    }
}
