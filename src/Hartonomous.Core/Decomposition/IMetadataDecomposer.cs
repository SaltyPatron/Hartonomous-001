using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Per-format metadata decomposer for model package files (config.json,
/// model_index.json, tokenizer_config.json, README.md, modeling_*.py, etc.).
/// Produces text_composition entities + structural metadata edges binding the
/// model_architecture / tokenizer_model / tensor entities to their declared
/// parameters.
///
/// Implementations per spec §V.4-V.6:
/// <list type="bullet">
/// <item><c>ModelConfigDecomposer</c> (config.json + generation_config.json →
/// has_hidden_size / has_num_layers / has_num_attention_heads / has_vocab_size
/// edges + text_composition for full JSON content).</item>
/// <item><c>ModelIndexDecomposer</c> (model_index.json for multi-component
/// packages: Flux text_encoder + transformer + vae; SDXL similar; Diffusers-format).
/// Produces model_architecture entities with sub-component children.</item>
/// <item><c>TokenizerConfigDecomposer</c> (tokenizer_config.json,
/// special_tokens_map.json → tokenizer-level metadata + special-token edges).</item>
/// <item><c>ModelCardDecomposer</c> (README.md, MODEL_CARD.md, citation files →
/// text_composition documentation entities).</item>
/// <item><c>PythonCodeDecomposer</c> (modeling_*.py, configuration_*.py when shipped
/// → text_composition with code-aware boundaries).</item>
/// </list>
///
/// Metadata decomposers are DIFFERENT from <see cref="IContentDecomposer"/>:
/// content decomposers handle the raw modality content (text/audio/image/video);
/// metadata decomposers handle model package metadata files (config and code).
/// Both ultimately route through <see cref="Hartonomous.Core.Text.SubstrateTextDecomposer"/>
/// for text-bearing content (seed-uses-core; per spec §II.1).
///
/// Per spec §V.4-V.6.
/// </summary>
public interface IMetadataDecomposer
{
    /// <summary>
    /// File patterns this decomposer accepts (filename patterns or extensions).
    /// </summary>
    /// <example>
    /// ModelConfigDecomposer: ["config.json", "generation_config.json"].
    /// ModelIndexDecomposer: ["model_index.json"].
    /// TokenizerConfigDecomposer: ["tokenizer_config.json", "special_tokens_map.json"].
    /// ModelCardDecomposer: ["README.md", "MODEL_CARD.md", "*.bib", "CITATION.cff"].
    /// PythonCodeDecomposer: ["modeling_*.py", "configuration_*.py"].
    /// HuggingFaceTokenizerDecomposer: ["tokenizer.json"] (special: produces word_form
    ///     entities via SubstrateTextDecomposer per spec §V.5).
    /// </example>
    IReadOnlyList<string> AcceptedFilePatterns { get; }

    /// <summary>
    /// Decompose a metadata file into substrate text_composition entities and
    /// the appropriate metadata edges binding them to the host entity (e.g.
    /// the model_architecture entity that this config.json belongs to). The
    /// host entity handle is provided by the caller (the safetensors container
    /// decomposer that's orchestrating this metadata decomposition).
    /// </summary>
    Task DecomposeAsync(
        Stream metadataFile,
        EntityHandle hostEntity,
        MetadataDecomposeContext context,
        IIngestionBatch batch,
        CancellationToken ct);
}

/// <summary>
/// Context for metadata decomposition. Carries provenance and trust prior for
/// the emitted text_composition entities + edges.
/// </summary>
public sealed record MetadataDecomposeContext(
    string ProvenanceCode,
    double TrustMu,
    string SourceFilePath);
