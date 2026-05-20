---
description: Text and semantics — the substrate viewed through linguistic content. Loads on text/decomposer/engine paths.
paths:
  - src/Hartonomous.Core/**
  - src/Hartonomous.Engine/**
  - src/Hartonomous.Decomposers/**
  - tests/**
  - docs/specs/decomposers/**
  - docs/specs/modalities/text.md
  - docs/specs/engine/**
---

## Text in the universal substrate

Text is one modality. Its Merkle DAG bottoms out at Unicode codepoints and tops out at documents, with structural classifications recorded at each tier. The same content from any text-bearing source — a UTF-8 byte stream from the user, a WordNet gloss, a Wiktionary citation, a UD sentence, a Tatoeba example, a safetensors `config.json` value, a tokenizer vocab byte string, an image caption, an audio transcript, a model output — routes through `Hartonomous.Core.Text.CanonicalTextDecomposer.Emit` and collapses to one identity by content hash. The substrate doesn't have a "WordNet sentence" and a "Tatoeba sentence" that happen to be the same; it has one `text_composition` entity that WordNet, Tatoeba, every safetensors model's tokenizer that contains it, and every user prompt that types it attest to. Trust priors and cross-source corroboration accumulate as separate `attestation_type`-distinguished Glicko events on the existing identity.

| Tier | Entity type | What it is in the Merkle DAG |
|------|-------------|------------------------------|
| 0 | `codepoint` | Unicode codepoint atom. POINTZM = Super-Fibonacci S³ projection by UCA collation rank + packed UCD bitmask in M. UCD properties on `codepoint_property` junction. |
| 1 | `grapheme_cluster` | UAX #29 grapheme cluster. LINESTRINGZM through codepoint centroids. |
| 2 | `word_form` | Attested surface form. LINESTRINGZM through grapheme centroids. Used everywhere — by every text decomposer AND by every safetensors decomposer's tokenizer pass to land model attestations on shared content. |
| 3 | `morpheme`, `lemma` | Morphological decomposition. LINESTRINGZM through children. |
| 4 | `text_composition`, `paragraph`, `document` | Higher-tier compositions. LINESTRINGZM through prior-tier centroids; MULTILINESTRINGZM for chapter-style branching. |
| 5 | `synset` | WordNet semantic units, attested via typed `has_sense` / `aligned_to_synset` / semantic-relation edges. |

Identity is content. A `word_form` like `minute` has ONE entity hash regardless of how many senses, languages, pronunciations, or models attest to it. The senses are typed edges (`has_sense` to `synset` entities); language is a junction row in `entity_language`; POS classifications are junction rows in `entity_pos` carrying their own Glicko-2 mu; morph features sit in `entity_morph_feature`. The substrate keeps polysemy because the same surface content really is one thing — disambiguation happens at inference time via arena-weighted edge selection.

Lexicalized wholes and their compositional decompositions coexist. `highrise` is an attested whole-form entity AND `high` + `rise` are separate entities; the `lexicalized_compound` edge connects them. The whole-form's centroid records the idiomatic meaning; the compositional trajectory through its parts records what the parts say. Geometric divergence between the two (Fréchet or centroid distance) is the substrate's idiomaticity signal — `scurvy_dog`'s whole-form centroid (pejorative) sits far from its compositional centroid (scurvy + dog), and the substrate detects that without anyone hand-labeling it.

## Why text is load-bearing for safetensors

Every safetensors model's tokenizer becomes / collapses-to `word_form` content entities via `HuggingFaceTokenizerDecomposer` → `SubstrateTextDecomposer.EmitStatic`. When `AttentionBlockTuplePass` processes Llama's layer 0 attention, it identifies the (token_a, token_b) pairs the heads attest between by content-hashing the tokenizer vocab strings. Two models that share vocabulary (or even just share tokens via byte-level overlap) attest on the **same** `model_attention_pattern(word_form:king, word_form:queen)` edge identity. Cross-architecture / cross-precision consensus accumulates by construction because the participant content entities are content-addressed.

This is what makes seed-uses-core non-negotiable. If a seed decomposer (WordNet, Wiktionary, Tatoeba) hashes a string directly via `ComputeHash(string)` instead of routing through `CanonicalTextDecomposer.Emit`, the resulting `text_composition` entity won't match what the canonical text path produces, and the same sentence in two sources will land on two entities. The substrate fragments. Same rule for safetensors metadata: `config.json` values, `tokenizer.json` byte strings, model card README content all go through the canonical text path.

## Decomposer contracts

Text decomposers extend `BaseDecomposer` (`src/Hartonomous.Core/Decomposition/BaseDecomposer.cs`):

- `ProvenanceCode` — row in `substrate.provenance` carrying the trust prior. Current priors include `unicode_consortium` (100,000), `wordnet` (95,000), `universal_dependencies` (92,000), `omw_curated` (90,000), `cldr` (70,000), `wiktionary` (68,000), `tatoeba` (50,000), `user_session` (20,000). Recompute from `sql/schema/seed/provenance.sql` when exact values matter.
- `Phases` — which `Phase` enum values it participates in (`UcdUca`, `Iso639`, `WordNetOmw`, `UniversalDeps`, `Wiktionary`, `Tatoeba`, plus model-decomposition phases that consume text via the tokenizer).
- `DecomposeCoreAsync()` — the producer body. Emits into `IRecordSink` via `IIngestionPipeline`.

Hashing is content-only: `ComputeHash(bytes)`, `ComputeMerkleHash(ordered child hashes)`, `ComputeEdgeHash(edge_type_id, role-ordered participant hashes)`. Decomposers MUST NOT hash user-visible text directly to produce `text_composition`-tier entities — they route through `CanonicalTextDecomposer.Emit` so the resulting hash matches what every other source produces for the same content. Per-row bulk hashing happens in-process before emission, so duplicate content within a chunk is suppressed via `IIngestionPipeline.GetExisting{EntityHashes,EntityClassifications,Edges,Physicalities,SequenceRows}Async` rather than relying on `ON CONFLICT` to clean up after blind emission (AP-19).

## Ingestion records, inference decides

Decomposers record all candidate senses, syntactic structures, classifications, and evidence edges without disambiguation. A token with three plausible POS tags emits three rows in `entity_pos` with provenance-derived initial mu; a verb with five candidate senses emits five `has_sense` edges with significance priors. Sense selection, role assignment, and meaning resolution happen at inference time via significance-weighted edge traversal in the requested arena (`lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, plus practitioner-defined arenas).

The substrate's text knowledge grows quantitatively. Each new source that attests `(word_form, pos)` fires a rating event on the existing `entity_pos` row, tightening sigma. Sources that disagree fire counter-events. The arena's leaderboard at query time IS the substrate's answer to "what POS is this word?"

## Codepoint cache discipline

`NpgsqlCodepointPropertiesCache.LoadAsync` loads all 303,808 codepoint property rows. That's appropriate for full-corpus seed phases (UCD/UCA seed, full-corpus ingestion). Inference paths (CLI `query`, `recall`, `complete`, `godel`, `SubstrateInferenceEngine`, `GodelEngine`) MUST use `LoadForCodepointsAsync(workingSet)` — subset by the codepoints actually present in the prompt or current document.

Per-codepoint S³ centroids do NOT come from this cache. They come from the embedded UCD blob via `hartonomous_ucd_cp_centroid` (UCA-collation-rank-ordered Super-Fibonacci, baked at blob build time). All C# paths that need a codepoint centroid go through `PhysicalityEmitter.CodepointS3Position`, which delegates to the blob. Computing `SuperFibonacciS3(cp, 0x110000)` on a raw codepoint integer is wrong and breaks Law #6 against the substrate-side `substrate.text_decompose`.

## Terse lexical probes

Terse user examples — `overload`, `highrise`, `minute`, `king : queen :: man : woman`, `scurvy_dog`, `rake`, `bank` — are semantic regression probes that test substrate behavior end-to-end (identity, classification, edges, geometry, arena selection). Answer the substrate-behavior path directly. The regression cases live in [`.claude/skills/hartonomous-semantic-eval/cases.md`](../skills/hartonomous-semantic-eval/cases.md).
