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
/// The Build-a-bear recipe. A single structured JSON document defining
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

public enum PackageFormat
{
    Safetensors,
    SafetensorsSharded,
    Gguf,
}

public sealed class ArchitectureSection
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = "minilm";

    [JsonPropertyName("hf_architecture_name")]
    public string? HfArchitectureName { get; set; }

    [JsonPropertyName("vocab_size")]
    public int VocabSize { get; set; } = 30000;

    [JsonPropertyName("hidden_dim")]
    public int HiddenDim { get; set; } = 384;

    [JsonPropertyName("num_hidden_layers")]
    public int NumHiddenLayers { get; set; } = 6;

    [JsonPropertyName("num_attention_heads")]
    public int NumAttentionHeads { get; set; } = 12;

    [JsonPropertyName("num_key_value_heads")]
    public int NumKeyValueHeads { get; set; } = 12;

    [JsonPropertyName("head_dim")]
    public int HeadDim { get; set; } = 32;

    [JsonPropertyName("intermediate_size")]
    public int IntermediateSize { get; set; } = 1536;

    [JsonPropertyName("max_position_embeddings")]
    public int MaxPositionEmbeddings { get; set; } = 512;

    [JsonPropertyName("tie_word_embeddings")]
    public bool TieWordEmbeddings { get; set; } = true;

    [JsonPropertyName("activation")]
    public string Activation { get; set; } = "gelu";

    [JsonPropertyName("norm_type")]
    public string NormType { get; set; } = "layernorm";

    [JsonPropertyName("norm_eps")]
    public double NormEps { get; set; } = 1e-12;

    [JsonPropertyName("rope")]
    public RopeSection Rope { get; set; } = new();

    [JsonPropertyName("moe")]
    public MoeSection Moe { get; set; } = new();

    [JsonPropertyName("lora")]
    public LoraSection Lora { get; set; } = new();
}

public sealed class RopeSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("theta")]
    public double Theta { get; set; } = 10000.0;
}

public sealed class MoeSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("num_experts")]
    public int NumExperts { get; set; } = 0;

    [JsonPropertyName("experts_per_token")]
    public int ExpertsPerToken { get; set; } = 0;

    /// <summary>
    /// Per-expert arena weighting overrides. Each expert can pull from a
    /// different substrate slice. Empty = all experts share the top-level
    /// arena_weights.
    /// </summary>
    [JsonPropertyName("per_expert_arena_weights")]
    public List<Dictionary<string, double>>? PerExpertArenaWeights { get; set; }
}

public sealed class LoraSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("rank")]
    public int Rank { get; set; } = 0;

    [JsonPropertyName("alpha")]
    public double Alpha { get; set; } = 0;

    [JsonPropertyName("target_modules")]
    public List<string> TargetModules { get; set; } = new();

    /// <summary>
    /// LoRA adapter slices. Each slice picks a (provenance, arena) pair
    /// to specialize for. The substrate computes a per-slice low-rank
    /// approximation and packs as a separate adapter matrix pair.
    /// </summary>
    [JsonPropertyName("slices")]
    public List<LoraSlice>? Slices { get; set; }
}

public sealed class LoraSlice
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    [JsonPropertyName("provenance")]
    public string Provenance { get; set; } = "wordnet";

    [JsonPropertyName("arena")]
    public string Arena { get; set; } = "semantic_relevance";

    [JsonPropertyName("rank")]
    public int Rank { get; set; } = 4;
}

public sealed class VocabSelectionSection
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "cross_wf_connectivity";

    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = new() { "eng" };

    [JsonPropertyName("explicit_tokens")]
    public List<string>? ExplicitTokens { get; set; }

    [JsonPropertyName("min_cross_wf_edges")]
    public int MinCrossWfEdges { get; set; } = 2;
}

public sealed class SynthesisSection
{
    [JsonPropertyName("embedding")]
    public string Embedding { get; set; } = "laplacian_eigenmap";

    [JsonPropertyName("attention")]
    public string Attention { get; set; } = "spectra_ritz";

    [JsonPropertyName("ffn")]
    public string Ffn { get; set; } = "spectra_ritz";

    [JsonPropertyName("moe_router")]
    public string MoeRouter { get; set; } = "lexical_disambiguation_arena";

    [JsonPropertyName("layer_norm_init")]
    public string LayerNormInit { get; set; } = "ones";
}
