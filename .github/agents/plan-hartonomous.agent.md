---
name: plan-hartonomous
description: Plan Hartonomous work without flattening the invention.
handoffs:
  - label: Start Implementation
    agent: implement-hartonomous
    prompt: Implement the approved plan.
    send: false
---

## Substrate pillars

1. `substrate.entity` — atoms and compositions. PK `(id, entity_type_id)`. BLAKE3 hash of content only. 25 entity types, LIST-partitioned (migration `0006`).
2. `substrate.edge` + `substrate.edge_member` — n-ary typed relations with role-ordered participants. Trajectory geometry. 33 edge types: structural (1–13), cross_lingual (14–16), cross_modal (17–18), unicode (19–21), model_derived (22–33). 7 roles.
3. `substrate.physicality` — universal geometry. 13 types. POINTZM/LINESTRINGZM. GiST-indexed. `ST_FrechetDistance`.
4. Reference tables (migration `0004`) + junction tables (migration `0007`) — classification vocabularies and evidence junctions. Three junctions carry Glicko-2: `entity_pos`, `entity_sense`, `pattern_deprel`.

## Phase enum

`CoreAlgebra` → `UcdUca` → `Iso639` → `WordNetOmw` → `UniversalDeps` → `ModelDecomp` → `Wiktionary` → `Tatoeba` → `SignificanceField` → `InferenceEngine` → `Validation`

Defined in `src/Hartonomous.Core/Orchestration/Phase.cs`. `SequentialPhaseRunner` in `src/Hartonomous.Engine/Orchestration/`.

## Decomposer contracts

| Decomposer | Provenance | Phase | Entities (type IDs) | Edges (type IDs) | Junctions |
|------------|-----------|-------|---------------------|-------------------|-----------|
| UCD/UCA | `unicode_consortium` (μ=2000) | `UcdUca` | codepoint (1), collation_element (17) | maps_to_lowercase (19), case_folds_to (20), has_collation_weight (21) | codepoint_property |
| ISO 639 | `sil_international` (μ=2000) | `Iso639` | language_name (18) | — | entity_language |
| WordNet | `princeton_wordnet` (μ=1800) | `WordNetOmw` | lemma (5), synset (13), word_sense (14) | has_sense (1), has_gloss (5), has_example (6) | entity_pos, entity_sense |
| OMW | `omwn_consortium` (μ=1600) | `WordNetOmw` | lemma (5) | aligned_to_synset (14) | entity_language |
| UD | `universaldependencies` (μ=1600) | `UniversalDeps` | ud_sentence (6), ud_token (7), word_form (3), lemma (5) | has_lemma (3) | entity_pos, entity_morph_feature |
| Safetensors | `huggingface_model` (μ=1500) | `ModelDecomp` | tensor (23), model_architecture (24), bpe_token (12), attention_pattern (25) | in_model (22)–in_vocabulary (32) | tensor_tensor_role, model_architecture_class |
| Wiktionary | `wiktextract` (μ=1400) | `Wiktionary` | wikt_sense (15), inflected_form (16), word_form (3) | has_etymology (10)–has_wikidata (13), translation_of (15), inflection_of (9), has_form (2) | entity_pos, entity_sense |
| Tatoeba | `tatoeba` (μ=1200) | `Tatoeba` | tatoeba_sentence (8), audio_recording (20) | has_text (8), translation_link (16), recording_of (17), has_contributor (18) | entity_language |

## Identity hashing

`ComputeHash(ReadOnlySpan<byte>)` → `Blake3.Hash(content)`. Atom identity.
`ComputeHash(string)` → UTF-8 encode → `Blake3.Hash()`. String atom.
`ComputeMerkleHash(byte[][])` → concat child hashes → `Merkle.Hash()`. Composition identity.
`ComputeEdgeHash(int, byte[][])` → `[edgeTypeId | participant hashes]` → `ComputeHash()`. Edge identity.

Content only. Position, ordinal, filename, tensor name → `sequence`, edges, `provenance`.

## Ingestion vs inference

- **Ingestion** (`src/Hartonomous.Decomposers/`): deterministic. All candidates recorded. Same input + same version = same state (Law #6).
- **Inference** (`src/Hartonomous.Engine/`): traverses + reweights edges via Glicko-2. Session-scoped output compositions. No new knowledge edges.

## Compute facade

`IComputeFacade` → `ComputeFacade` → `NativeCompute` (`src/Hartonomous.Core/Compute/Internal/NativeCompute.cs`) → `libhartonomous` (`ext/libhartonomous/`).
P/Invoke: `Blake3Native`, `S3Native`, `SuperFibonacciNative`, `HilbertNative` in `src/Hartonomous.Core/Native/`.
No direct imports of MKL/Eigen/Spectra/ONNX from decomposers/engine.
