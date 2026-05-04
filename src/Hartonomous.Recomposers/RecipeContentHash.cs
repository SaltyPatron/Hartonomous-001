using System;
using System.Text;
using System.Text.Json;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// BLAKE3 of canonical recipe JSONB for __metadata__.hartonomous_recipe_id.
/// The serialization is canonical (sorted keys, no whitespace) so two
/// equivalent recipes hash to the same id, enabling recipe-level
/// content-addressing in the future recipe marketplace.
/// </summary>
public static class RecipeContentHash
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Compute(RecompositionOptions options, IComputeFacade compute)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(compute);

        var canonical = new
        {
            mode = options.Mode.ToString(),
            refinement_policy = options.RefinementPolicy.ToString(),
            quantization_policy = options.QuantizationPolicy.ToString(),
            requantize_target = options.RequantizeTarget,
            lora_policy = options.LoraPolicy.ToString(),
            max_shard_bytes = options.MaxShardBytes,
            provenance_filter = options.ProvenanceFilter,
            arena_codes = options.ArenaCodes,
            significance_threshold = options.SignificanceThreshold,
            arena_filter = options.ArenaFilter,
            noise_floor = options.NoiseFloor,
            target_arch_spec = options.TargetArchSpecJson,
            vocab_subset_token_hashes = options.VocabSubsetTokenHashes,
            hardware_profile = options.HardwareProfileJson,
            cherry_picked_sources_per_tensor = options.CherryPickedSourcesPerTensor,
        };

        string canonicalJson = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);

        Blake3Hasher hasher = compute.Common.CreateBlake3Hasher();
        Span<byte> tag = stackalloc byte[6];
        Encoding.ASCII.GetBytes("recipe", tag);
        hasher.Update(tag);
        hasher.Update(Encoding.UTF8.GetBytes(canonicalJson));
        byte[] hash = hasher.Finalize();
        return Convert.ToHexStringLower(hash);
    }
}
