using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// FFN-as-substrate-edges construction. Each intermediate slot IS a
/// concrete substrate edge — inspectable, auditable, recipe-controllable.
///
/// Per layer ℓ with assigned arenas Aℓ:
///   1. Query <c>substrate.select_synth_edges_for_ffn(vocab, Aℓ, top_n=intermediate_dim)</c>
///   2. For each returned edge (source_hash, target_hash, mu, games):
///        - Resolve source_hash → vocab_idx → E[source] (hidden_dim row)
///        - Resolve target_hash → vocab_idx → E[target] (hidden_dim row)
///        - Slot k receives:
///            gate_proj[k, :] = E[source]   (key direction; SwiGLU only)
///            up_proj[k, :]   = E[source]   (key direction)
///            down_proj[:, k] = E[target] * signed_magnitude(mu)
///   3. Slots beyond returned-edge count stay exact zero (honest abstention
///      — sparse-by-construction).
///
/// Sign discrimination (AP-31): signed_magnitude(mu) = sqrt(|mu-1500|/100)
/// × sign(mu-1500). Negative-mu edges encode anti-correlation / suppression.
/// Magnitude scaled so typical mu deviation (~50-100) maps to ~1-1.5 weight;
/// extreme mu (~30000) maps to ~17 weight — preserves dynamic range without
/// dominating.
///
/// Inspectable: substrate edge (source_hash, target_hash) per slot is
/// queryable from the FFN slot index alone via the recipe + substrate.
/// </summary>
public static class FfnEdgeSlotSynthesizer
{
    public static async Task<FfnMatrices> SynthesizeAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<VocabToken> vocab,
        float[] embeddingF32,
        IReadOnlyList<string> arenaCodes,
        int hiddenDim,
        int intermediateDim,
        int layerIndex,
        bool useSwiGlu,
        RecompositionOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(embeddingF32);
        ArgumentNullException.ThrowIfNull(arenaCodes);
        if (hiddenDim <= 0 || intermediateDim <= 0)
        {
            throw new ArgumentException("hiddenDim and intermediateDim must be positive");
        }
        if (embeddingF32.Length != vocab.Count * hiddenDim)
        {
            throw new ArgumentException("embedding shape mismatch with vocab × hiddenDim");
        }

        Dictionary<string, int> hashHexToIdx = new(StringComparer.Ordinal);
        for (int i = 0; i < vocab.Count; i++)
        {
            hashHexToIdx[Convert.ToHexString(vocab[i].EntityHash)] = i;
        }

        byte[][] vocabHashes = new byte[vocab.Count][];
        for (int i = 0; i < vocab.Count; i++)
        {
            vocabHashes[i] = vocab[i].EntityHash;
        }

        float[]? gateProj = useSwiGlu ? new float[(long)intermediateDim * hiddenDim] : null;
        float[] upProj = new float[(long)intermediateDim * hiddenDim];
        float[] downProj = new float[(long)hiddenDim * intermediateDim];

        int slotsFilled = 0;
        long edgesScanned = 0;

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT source_hash, target_hash, mu, games, score "
            + "FROM substrate.select_synth_edges_for_ffn(@vocab, @arenas, @top_n)",
            conn);
        cmd.CommandTimeout = 1800;
        cmd.Parameters.Add(new NpgsqlParameter("vocab", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea) { Value = vocabHashes });
        cmd.Parameters.Add(new NpgsqlParameter("arenas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = ToArray(arenaCodes) });
        cmd.Parameters.AddWithValue("top_n", intermediateDim);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            edgesScanned++;
            if (slotsFilled >= intermediateDim)
            {
                break;
            }
            byte[] srcHash = (byte[])reader.GetValue(0);
            byte[] tgtHash = (byte[])reader.GetValue(1);
            double mu = reader.GetDouble(2);

            if (!hashHexToIdx.TryGetValue(Convert.ToHexString(srcHash), out int srcIdx))
            {
                continue;
            }
            if (!hashHexToIdx.TryGetValue(Convert.ToHexString(tgtHash), out int tgtIdx))
            {
                continue;
            }

            double muDev = mu - 1500.0;
            double signedMag = Math.Sqrt(Math.Abs(muDev) / 100.0) * Math.Sign(muDev);
            if (Math.Abs(signedMag) < 1e-9)
            {
                continue;
            }

            int slot = slotsFilled++;
            long srcOff = (long)srcIdx * hiddenDim;
            long tgtOff = (long)tgtIdx * hiddenDim;
            long gateRow = (long)slot * hiddenDim;

            for (int d = 0; d < hiddenDim; d++)
            {
                float keyVal = embeddingF32[srcOff + d];
                if (useSwiGlu && gateProj is not null)
                {
                    gateProj[gateRow + d] = keyVal;
                }
                upProj[gateRow + d] = keyVal;
                downProj[(long)d * intermediateDim + slot] = (float)(embeddingF32[tgtOff + d] * signedMag);
            }
        }

        Console.Out.WriteLine(
            $"  FfnEdgeSlot layer {layerIndex} arenas=[{string.Join(",", arenaCodes)}]: "
            + $"edges_scanned={edgesScanned} slots_filled={slotsFilled}/{intermediateDim} "
            + $"({(double)slotsFilled / intermediateDim * 100:F1}% density)");

        return new FfnMatrices
        {
            HiddenDim = hiddenDim,
            IntermediateDim = intermediateDim,
            GateProj = gateProj,
            UpProj = upProj,
            DownProj = downProj,
            UseSwiGlu = useSwiGlu,
            DerivedFromSubstrate = slotsFilled > 0,
            RitzSlotsUsed = slotsFilled,
        };
    }

    private static string[] ToArray(IReadOnlyList<string> list)
    {
        if (list is string[] arr)
        {
            return arr;
        }
        string[] r = new string[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            r[i] = list[i];
        }
        return r;
    }
}
