using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Recipe-level tokenizer choice. Tokenizers in the substrate are
/// content-addressed `tokenizer_model` entities linked to `text_composition`
/// vocab entries via `has_vocab_entry` typed edges (vocab_index lives on
/// edge_member.role_position).
///
/// Marker-bearing tokens (" walk", "▁walk", "Ġwalk", "walk##", "walk@@",
/// "&lt;|endoftext|&gt;") are EACH their own text_composition entity
/// content-addressed by their full bytes. Their LINESTRINGZM trajectory
/// references the marker entity (codepoint U+0020 / U+2581 / U+0120 / etc.,
/// or a multi-codepoint marker text_composition) AND the bare-content
/// text_composition as children — the recursive Merkle DAG encodes the
/// underlying-lemma relationship structurally. Cross-tokenizer attention
/// attestation accumulation works automatically because all marker
/// variants share the bare-content child entity; substrate.edge_member
/// queries find all parents whose composition trajectory references the
/// bare lemma, regardless of which tokenizer's marker convention wraps it.
///
/// The substrate-side stays pure (text_composition + has_vocab_entry +
/// recursive trajectory); per-tokenizer-family marker insertion logic
/// (Llama BPE leading space, SentencePiece ▁ prefix, GPT-2 Ġ, WordPiece
/// ## continuation, Moses @@ suffix) lives in C-side family modules in
/// libhartonomous, invoked by the substrate.tokenize / substrate.detokenize
/// SRFs.
/// </summary>
public sealed class TokenizerSection
{
    /// <summary>
    /// How to build the exported model's tokenizer:
    ///
    ///   from_model — use the tokenizer of the named ingested model
    ///                (`from_model` field below). Output tokenizer.json
    ///                reproduces that model's vocab + IDs exactly. Required
    ///                for round-trip logit-correlation tests against the
    ///                source model.
    ///
    ///   substrate_derived — build a new tokenizer from the substrate's
    ///                most-attested text_composition entities. Vocab size
    ///                from `vocab_size`. Filtered by `arena_filter`,
    ///                `language_filter`, `modality_filter`. IDs assigned in
    ///                attestation-density order (most-attested = lowest ID).
    ///
    ///   merge — union of multiple ingested tokenizers' vocabs, deduped
    ///                via shared text_composition identity. `merge_models`
    ///                lists the tokenizer_model entity names to merge.
    ///                Final IDs assigned in merged order; per-model
    ///                provenance preserved via tokenizer.json metadata.
    ///
    ///   specialized — like `substrate_derived` but with explicit recipe-
    ///                level priorities for token categories (code identifiers,
    ///                natural-language word_forms, structured-data keys, etc).
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "from_model";

    /// <summary>
    /// When `mode = "from_model"`: the substrate `tokenizer_model` entity
    /// name to clone. Typically the source model's name
    /// (e.g. "qwen-2.5-coder-3b", "llama-4-maverick"). Resolved via
    /// substrate.get_tokenizer_by_name.
    /// </summary>
    [JsonPropertyName("from_model")]
    public string? FromModel { get; set; }

    /// <summary>
    /// When `mode = "merge"`: ordered list of tokenizer_model entity names
    /// to union. Earlier names get lower IDs for shared tokens.
    /// </summary>
    [JsonPropertyName("merge_models")]
    public List<string>? MergeModels { get; set; }

    /// <summary>
    /// When `mode = "substrate_derived"` / `"specialized"`: target vocab size.
    /// </summary>
    [JsonPropertyName("vocab_size")]
    public int? VocabSize { get; set; }

    /// <summary>
    /// Arena code to filter substrate text_compositions by attestation
    /// density. e.g. `"identifier_compositionality"` for code-specialized
    /// tokenizer; `"semantic_relevance"` for general-purpose.
    /// </summary>
    [JsonPropertyName("arena_filter")]
    public string? ArenaFilter { get; set; }

    /// <summary>
    /// Language codes (ISO 639-3) to restrict the substrate-derived vocab.
    /// e.g. `["eng"]` for English-only; `["eng", "cmn", "jpn"]` for multilingual.
    /// </summary>
    [JsonPropertyName("language_filter")]
    public List<string>? LanguageFilter { get; set; }

    /// <summary>
    /// Modality codes to restrict the substrate-derived vocab.
    /// Defaults to `["text"]`.
    /// </summary>
    [JsonPropertyName("modality_filter")]
    public List<string>? ModalityFilter { get; set; }

    /// <summary>
    /// Special tokens to prepend (added before substrate-derived vocab).
    /// e.g. `["<pad>", "<s>", "</s>", "<unk>", "<mask>"]`.
    /// </summary>
    [JsonPropertyName("special_tokens")]
    public List<string>? SpecialTokens { get; set; }

    /// <summary>
    /// When `mode = "specialized"`: per-category vocab quotas.
    /// e.g. `{"word_form": 30000, "text_composition": 15000, "code_identifier": 5000}`.
    /// </summary>
    [JsonPropertyName("category_quotas")]
    public Dictionary<string, int>? CategoryQuotas { get; set; }
}
