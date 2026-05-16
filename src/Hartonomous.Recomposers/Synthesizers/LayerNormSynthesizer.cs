using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Substrate-derived LayerNorm γ (scale) and β (bias) per layer.
///
/// Conventional LN with γ=1 β=0 lets the activation variance compound
/// layer-to-layer; the softmax inside attention saturates after 2-3
/// layers → attention collapses to argmax → output degenerates to
/// repetition. A trained transformer learns per-layer γ/β to compensate
/// for the variance each layer's input distribution actually has.
///
/// Substrate-native derivation: for layer ℓ assigned arena Aℓ (per
/// RecipeConfig per_layer_arena_assignment), pull the mean and stddev
/// of entity_significance.mu in Aℓ via the named substrate function
/// per_arena_entity_significance_stats. Derive:
///   γ[d] = 1 / max(stddev_Aℓ, 1e-3)   (variance compensation)
///   β[d] = -mean_Aℓ × γ[d]            (recenter to ~0)
/// Both broadcast across hidden_dim (LayerNorm parameters are per-feature
/// scale/shift; substrate-derived value is constant across features at
/// this layer, which matches the conventional learned behavior where
/// γ ≈ const after training stabilizes).
/// </summary>
public static class LayerNormSynthesizer
{
    public static async Task<IReadOnlyDictionary<string, LayerNormStats>> LoadStatsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<string> arenaCodes,
        CancellationToken ct)
    {
        Dictionary<string, LayerNormStats> stats = new(StringComparer.Ordinal);

        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT arena_code, mean_mu, stddev_mu, row_count "
            + "FROM substrate.per_arena_entity_significance_stats(NULL) "
            + "WHERE arena_code = ANY(@arenas)",
            conn);
        cmd.CommandTimeout = 60;
        NpgsqlParameter arenasParam = new("arenas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = arenaCodes is string[] arr ? arr : System.Linq.Enumerable.ToArray(arenaCodes),
        };
        cmd.Parameters.Add(arenasParam);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string arena = reader.GetString(0);
            double mean = reader.GetDouble(1);
            double stddev = reader.GetDouble(2);
            long count = reader.GetInt64(3);
            double sd = Math.Max(stddev, 1e-3);
            double gamma = 1.0 / sd;
            double beta = -mean * gamma;
            stats[arena] = new LayerNormStats
            {
                Arena = arena,
                Gamma = gamma,
                Beta = beta,
                RowCount = count,
            };
        }
        return stats;
    }

    /// <summary>
    /// Returns gamma vector [hiddenDim] for the given arena from precomputed
    /// stats. Broadcasts the per-arena scalar to every feature dimension.
    /// </summary>
    public static float[] GammaFor(string arena, int hiddenDim, IReadOnlyDictionary<string, LayerNormStats> stats)
    {
        float[] gamma = new float[hiddenDim];
        float value = stats.TryGetValue(arena, out LayerNormStats? s) ? (float)s.Gamma : 1.0f;
        for (int i = 0; i < hiddenDim; i++)
        {
            gamma[i] = value;
        }
        return gamma;
    }

    public static float[] BetaFor(string arena, int hiddenDim, IReadOnlyDictionary<string, LayerNormStats> stats)
    {
        float[] beta = new float[hiddenDim];
        float value = stats.TryGetValue(arena, out LayerNormStats? s) ? (float)s.Beta : 0.0f;
        for (int i = 0; i < hiddenDim; i++)
        {
            beta[i] = value;
        }
        return beta;
    }
}
