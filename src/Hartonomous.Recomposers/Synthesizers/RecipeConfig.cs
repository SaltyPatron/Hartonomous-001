using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// The Substrate Synthesis recipe. A single structured JSON document defining
/// every choice that goes into a substrate-derived model export:
///
///   • Container format (safetensors single / safetensors sharded / GGUF).
///   • Architecture (family + dimensions — Llama / BERT / MiniLM / Qwen
///     / Mistral / custom).
///   • Vocab selection strategy (cross-WF connectivity / explicit list /
///     edge-count / per-language filter).
///   • Per-arena weights (which substrate domains contribute, in what
///     proportion).
///   • Per-provenance weights (which sources count more — wordnet / wiktionary
///     / model-attestations / user-curated).
///   • Synthesis algorithm per layer-type (Laplacian eigenmap / Spectra Ritz /
///     deterministic init).
///   • MoE / LoRA / RoPE knobs.
///   • Output dtype + honest-abstention.
///
/// Loadable from JSON file via <see cref="LoadAsync"/>; writable via
/// <see cref="SaveAsync"/>. Templates for known architectures live in
/// <see cref="RecipeTemplates"/>. The exporter (<see cref="SubstrateModelExporter"/>)
/// consumes the resolved config and writes whichever package format
/// the recipe requested.
/// </summary>
public sealed class RecipeConfig
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "untitled-bear";

    [JsonPropertyName("package_format")]
    public PackageFormat PackageFormat { get; set; } = PackageFormat.Safetensors;

    [JsonPropertyName("architecture")]
    public ArchitectureSection Architecture { get; set; } = new();

    [JsonPropertyName("vocab_selection")]
    public VocabSelectionSection VocabSelection { get; set; } = new();

    [JsonPropertyName("synthesis")]
    public SynthesisSection Synthesis { get; set; } = new();

    [JsonPropertyName("arena_weights")]
    public Dictionary<string, double> ArenaWeights { get; set; } = new()
    {
        ["lexical_disambiguation"] = 1.0,
        ["semantic_relevance"] = 1.0,
        ["syntactic_role_fitness"] = 1.0,
        ["translation_quality"] = 1.0,
        ["frequency_significance"] = 1.0,
        ["corroboration_strength"] = 1.0,
        ["source_authority"] = 1.0,
        ["model_trust"] = 0.5,
        ["morphological_productivity"] = 1.0,
        ["attention_pattern_confidence"] = 0.5,
    };

    [JsonPropertyName("provenance_weights")]
    public Dictionary<string, double> ProvenanceWeights { get; set; } = new()
    {
        ["wordnet"] = 1.0,
        ["wiktionary"] = 0.8,
        ["universal_dependencies"] = 0.9,
        ["omw_curated"] = 0.9,
    };

    [JsonPropertyName("output_dtype")]
    public QuantizationTarget OutputDtype { get; set; } = QuantizationTarget.F32;

    [JsonPropertyName("honest_abstention")]
    public bool HonestAbstention { get; set; } = true;

    [JsonPropertyName("deterministic_seed")]
    public int DeterministicSeed { get; set; } = 0;

    /// <summary>
    /// Translates this recipe into the exporter's internal types.
    /// </summary>
    public TargetArchitectureSpec ToArchitectureSpec()
    {
        ArchitectureSection a = Architecture;
        return new TargetArchitectureSpec(
            Architecture: a.HfArchitectureName ?? DeriveHfName(a.Family),
            VocabSize: a.VocabSize,
            HiddenDim: a.HiddenDim,
            NumHiddenLayers: a.NumHiddenLayers,
            NumAttentionHeads: a.NumAttentionHeads,
            IntermediateSize: a.IntermediateSize,
            MaxPositionEmbeddings: a.MaxPositionEmbeddings,
            TieWordEmbeddings: a.TieWordEmbeddings,
            ActivationFunction: a.Activation,
            LayerNormEps: a.NormEps,
            InitializerRange: 0.02,
            HiddenAct: a.Activation,
            UseCache: true,
            HeadDim: a.HeadDim,
            NumKeyValueHeads: a.NumKeyValueHeads,
            AdditionalTensorNames: ImmutableArray<string>.Empty);
    }

    public RecompositionOptions ToRecompositionOptions()
    {
        return new RecompositionOptions(
            ArenaWeights: ArenaWeights.ToImmutableDictionary(),
            ProvenanceWeights: ProvenanceWeights.ToImmutableDictionary(),
            ProvenanceAllowlist: ImmutableArray<string>.Empty,
            ProvenanceBlocklist: ImmutableArray<string>.Empty,
            OutputDtype: OutputDtype,
            LayerAssignmentSeed: DeterministicSeed,
            SignificanceFloor: 1e-9,
            EgonetHops: 2,
            HonestAbstention: HonestAbstention);
    }

    private static string DeriveHfName(string family) => family.ToLowerInvariant() switch
    {
        "llama" => "LlamaForCausalLM",
        "bert" => "BertForMaskedLM",
        "minilm" => "BertForMaskedLM",
        "qwen" or "qwen2" => "Qwen2ForCausalLM",
        "mistral" => "MistralForCausalLM",
        "custom" => "HartonomousCustomForCausalLM",
        _ => "HartonomousModel",
    };

    public static async Task<RecipeConfig> LoadAsync(string path, CancellationToken ct = default)
    {
        await using FileStream fs = File.OpenRead(path);
        RecipeConfig? cfg = await JsonSerializer.DeserializeAsync<RecipeConfig>(
            fs, JsonOpts, ct).ConfigureAwait(false);
        return cfg ?? throw new InvalidDataException($"Recipe file at {path} parsed to null.");
    }

    /// <summary>
    /// Load a recipe by its substrate-content name (registered via
    /// substrate.recipe_name). The substrate is the source of truth for
    /// recipes per the three-tier data model — app-starter rows are
    /// seeded at db-bootstrap, ingest-derived rows are emitted by the
    /// SafetensorsDecomposer end-pass, and practitioner forks are user-
    /// tier. The CLI's --recipe-name option flows through this method
    /// instead of reading from a file.
    /// </summary>
    public static async Task<RecipeConfig> LoadFromSubstrateAsync(
        Npgsql.NpgsqlDataSource dataSource, string recipeName, CancellationToken ct = default)
    {
        await using Npgsql.NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using Npgsql.NpgsqlCommand cmd = new(
            "SELECT substrate.get_recipe_by_name($1)", conn);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = recipeName });
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null || result == DBNull.Value)
        {
            throw new InvalidOperationException(
                $"Recipe '{recipeName}' not found in substrate.recipe_name. "
              + "Available recipes: SELECT code FROM substrate.recipe_name. "
              + "App-starter recipes are seeded at db-bootstrap (minilm-base, "
              + "bert-base, llama-1b, llama-3b, mistral-7b, qwen-7b, qwen-2.5-coder-3b). "
              + "Ingest-derived recipes appear after running 'hart seed ModelDecomp'.");
        }
        byte[] canonicalJson = (byte[])result;
        RecipeConfig? cfg = JsonSerializer.Deserialize<RecipeConfig>(canonicalJson, JsonOpts);
        return cfg ?? throw new InvalidDataException(
            $"Recipe '{recipeName}' canonical_json parsed to null.");
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        await using FileStream fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, this, JsonOpts, ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
