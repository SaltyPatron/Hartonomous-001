using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Substrate-derived position embeddings. Replaces conventional learned
/// or random-init positional embeddings with mean-pooled vocab-token
/// embeddings at each ordinal position across all content trajectories.
///
/// Construction:
///   1. Query <c>substrate.position_embedding_stats(max_position, top_n)</c>
///      → per-position (word_form_hash, occurrence_count) ranked rows.
///   2. For each position p, mean-pool the embedding rows of those
///      word_forms weighted by occurrence count.
///   3. Pack as <c>[max_position × hidden_dim]</c> tensor matching HF
///      position_embeddings.weight expectations.
///
/// Substrate-native + deterministic — same content trajectory state
/// produces byte-identical position embeddings.
/// </summary>
public static class PositionEmbeddingSynthesizer
{
    public static async Task<TensorData> SynthesizeAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<VocabToken> vocab,
        float[] embeddingF32,
        int hiddenDim,
        int maxPositionEmbeddings,
        RecompositionOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(embeddingF32);
        if (hiddenDim <= 0 || maxPositionEmbeddings <= 0)
        {
            throw new ArgumentException("hiddenDim and maxPositionEmbeddings must be positive");
        }
        if (embeddingF32.Length != vocab.Count * hiddenDim)
        {
            throw new ArgumentException(
                $"embedding shape mismatch: {embeddingF32.Length} != {vocab.Count}×{hiddenDim}");
        }

        Dictionary<string, int> hashHexToVocabIdx = new(StringComparer.Ordinal);
        for (int i = 0; i < vocab.Count; i++)
        {
            hashHexToVocabIdx[Convert.ToHexString(vocab[i].EntityHash)] = i;
        }

        float[] posEmbed = new float[(long)maxPositionEmbeddings * hiddenDim];
        double[] posWeightSum = new double[maxPositionEmbeddings];
        long rowsAggregated = 0;
        long rowsIgnoredOutOfVocab = 0;

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT ordinal, child_hash, occurrences "
            + "FROM substrate.position_embedding_stats(@max_pos, @top_n)",
            conn);
        cmd.CommandTimeout = 600;
        cmd.Parameters.AddWithValue("max_pos", maxPositionEmbeddings);
        cmd.Parameters.AddWithValue("top_n", 4096);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int position = reader.GetInt32(0);
            byte[] childHash = (byte[])reader.GetValue(1);
            long occurrences = reader.GetInt64(2);

            if (position < 0 || position >= maxPositionEmbeddings)
            {
                continue;
            }
            if (!hashHexToVocabIdx.TryGetValue(Convert.ToHexString(childHash), out int vocabIdx))
            {
                rowsIgnoredOutOfVocab++;
                continue;
            }

            double weight = Math.Log(1.0 + occurrences);
            posWeightSum[position] += weight;
            long embOff = (long)vocabIdx * hiddenDim;
            long posOff = (long)position * hiddenDim;
            for (int d = 0; d < hiddenDim; d++)
            {
                posEmbed[posOff + d] += (float)(weight * embeddingF32[embOff + d]);
            }
            rowsAggregated++;
        }

        for (int p = 0; p < maxPositionEmbeddings; p++)
        {
            double w = posWeightSum[p];
            if (w <= 0)
            {
                continue;
            }
            float scale = (float)(1.0 / w);
            long off = (long)p * hiddenDim;
            for (int d = 0; d < hiddenDim; d++)
            {
                posEmbed[off + d] *= scale;
            }
        }

        Console.Out.WriteLine(
            $"PositionEmbeddingSynthesizer: rows_aggregated={rowsAggregated} "
            + $"rows_ignored_out_of_vocab={rowsIgnoredOutOfVocab} "
            + $"positions_with_data={CountNonzero(posWeightSum)}/{maxPositionEmbeddings}");

        return TensorPacker.PackF32(posEmbed, new[] { maxPositionEmbeddings, hiddenDim }, options.OutputDtype);
    }

    private static int CountNonzero(double[] arr)
    {
        int n = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] != 0)
            {
                n++;
            }
        }
        return n;
    }
}
