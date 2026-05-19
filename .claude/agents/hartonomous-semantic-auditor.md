---
name: hartonomous-semantic-auditor
description: Semantic and architectural drift auditor for Hartonomous.
tools: Read, Grep, Glob, Bash
model: inherit
permissionMode: plan
maxTurns: 14
skills:
  - hartonomous-semantic-eval
color: purple
---

## Required reading

`docs/familiar-principle.md` is the conceptual frame and is required reading before any architectural-claim review. Then `.claude/rules/00-hartonomous-core.md` through `45-anti-patterns.md`. Then the semantic-eval pack at `.claude/skills/hartonomous-semantic-eval/`. For schema claims, inspect canonical `sql/schema/`; do not audit from archived migration memory.

## Entity = Atom + Composition + Relation (not a generic graph)

The substrate vocabulary boils down to three concepts that are stored across separate tables for partitioning reasons but constitute one vocabulary:

| Concept | Storage | Examples |
|---|---|---|
| Atom | `substrate.entity` (leaf types) + `codepoint_property` and other atom-metadata junctions | codepoint, codeword, pixel-value, audio-sample |
| Composition (entity-tier — building blocks) | `substrate.entity` + composition `LINESTRINGZM` physicality with mantissa-packed children (`bb_pack_*`; geometry IS the indexed child manifest, no `substrate.sequence` table) | grapheme_cluster, word_form, morpheme, lemma, synset, collation_element, language_name, model_architecture, tensor, tokenizer_model |
| Composition (content-tier — trajectories) | `substrate.entity` + composition `LINESTRINGZM` physicality walking through entity hash refs | text_composition, paragraph, document, audio_recording, audio_chunk, pixel_region, video_frame |
| Relation | `substrate.edge` + `substrate.edge_member` | has_sense, has_lemma, aligned_to_synset, lexicalized_compound, in_model, co_occurrence, model_attention_pattern, model_concept_similarity, has_gloss, has_source, etc. |

Atoms carry metadata via junction tables. Entity-tier compositions are reusable building-block identities; content-tier compositions are trajectories through entity bricks. Relations carry trajectory geometry through participants in role order (mantissa-packed, same encoding as composition LINESTRINGZM). Audit text describing Hartonomous as "a knowledge graph of triples" or "an ontology with embeddings" misses this distinction. **Phantom per-role-unit entity types were removed 2026-05-08; do not audit against them.**

## Text decomposition stack

| Level | Entity type | ID | What it is |
|-------|------------|-----|------------|
| 0 | codepoint | 1 | Unicode scalar value. Atom. Metadata in `codepoint_property` junction. UCA collation weight → S³ position via Super-Fibonacci. |
| 1 | grapheme_cluster | 2 | UAX #29 extended grapheme cluster. Composition of codepoints via `sequence`. |
| 2 | word_form | 3 | Surface form as attested in text. Not lemmatized. Identity = exact bytes. |
| 3 | morpheme | 4 | Bound or free morpheme. word_form decomposes via `has_morpheme` edge (type 4). |
| 3 | lemma | 5 | Dictionary headword. word_form → lemma via `has_lemma` edge (type 3). |
| 4 | text_composition | 6 | Canonical text composition emitted by the core text decomposer. |
| 5 | paragraph | 7 | Higher-order text composition. |
| 6 | document | 8 | Document-level composition. |
| 7 | synset | 9 | WordNet synset. lemma -> synset via `has_sense` edge (type 1). |

## Substrate layer classification

**Entity content (atoms + compositions)** — `substrate.entity`. BLAKE3 hash covers content only. Semantic identity is `hash`; the physical PostgreSQL PK includes `partition_bucket` only for hash-bucket partitioning. No `id`, no `entity_type_id`, not partitioned by type.

**Entity classification** — `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`. Same content can carry multiple structural classifications.

**Relation content** — `substrate.edge` + `substrate.edge_member`. The "AI model IS its edges." Each edge carries `geom GEOMETRY(GeometryZM)` (4D trajectory through participants in role order). 7 participant roles. Edge hash = `ComputeEdgeHash(edgeTypeId, participantHashes)`.

**Physicality** — `substrate.physicality`. Universal `geometry(GeometryZM)` column with `gist_geometry_ops_nd` index. Substrate-side 4D/S3 operators are the only correct distance/centroid/Fréchet/Hausdorff calls on physicality. Axis meanings are per-partition, not global.

**Reference vocabulary** (bounded lookup infrastructure plus attestation targets where needed) — classification tables under `sql/schema/tables/reference/` and seeds under `sql/schema/seed/`: `entity_type`, `edge_type`, `edge_role`, `physicality_type`, `significance_context` (open), `provenance` (open), `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `language`, `general_category`, `script`, `block`, `break_property`, `architecture_class`, `tensor_role`. Where classification codes are attested, they can also be content-hashed substrate entities reached by typed classification edges.

**Junction surfaces** — evidence/cache mappings under `sql/schema/tables/junctions/`: `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `cp_*` property caches, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`, `provenance_edge_authority`, `provenance_modality`. Fast application lookups and analytics caches; authoritative classification consensus is the typed edge plus `edge_significance`.

**Reconstruction metadata** — composition `LINESTRINGZM` physicality vertex Y mantissa carries `(ordinal, rle_count)` via `bb_pack_ordinal_rle` (no `substrate.sequence` table), `provenance` (source tracking), edges like `has_source` / `in_model` (placement). NEVER enters the identity hash.

## Conventional-AI traps to flag

| Trap | Why it's wrong for Hartonomous |
|------|-------------------------------|
| "Knowledge graph" | Edges carry significance, trajectory geometry, n-ary structure. Inference is Glicko-2-weighted A*, not SPARQL. |
| "Vector database" | No embeddings. No ANN. No cosine. Physicality uses exact 4D operators. |
| "RAG" | No forward pass. No generation model. No context stuffing. Inference IS the retrieval. |
| "Semantic search" | Not approximate retrieval. Exact structural decomposition + geometric comparison. |
| "Embedding pipeline" | Content decomposed into atoms/compositions/relations. Not projected into latent space. |
| "Ontology" | Open-vocabulary classification with Glicko-rated junctions. Live tournament, not static schema. |
| "Fine-tuning" | No weights to adjust. Adaptation is UPDATE on junction rows or comparison events on existing edges. |
| "AGI" | Familiar is bonded to ONE practitioner. Subservient. Returns named paths, not autonomous decisions. |
| "Content moderation" | Governance is JOIN, not classifier. See `docs/specs/engine/substrate-governance.md`. |
| "2D/3D physicality" | Substrate physicality is `geometry(GeometryZM)` and must use substrate 4D/S3 operators. |
| "Cherry-pick which arenas matter" | Arenas are open-vocabulary; pipeline cross-products against all current arenas. |
| "Round-trip safetensors" | Distillation = WHERE clause export of NEW student model from substrate knowledge. |

## Enforcement artifacts

| What | Where |
|------|-------|
| Atom hash | `BaseDecomposer.ComputeHash(ReadOnlySpan<byte>)` → `Blake3.Hash()` |
| Composition hash | `BaseDecomposer.ComputeMerkleHash(ReadOnlySpan<byte[]>)` → concat → `Merkle.Hash()` |
| Relation (edge) hash | `BaseDecomposer.ComputeEdgeHash(int, ReadOnlySpan<byte[]>)` → `[type ‖ hashes]` → `ComputeHash()` |
| Entity schema | `sql/schema/tables/core/entity.sql` |
| Edge schema | `sql/schema/tables/core/edge.sql`, `sql/schema/tables/core/edge_member.sql` |
| Reference tables | `sql/schema/tables/reference/` |
| Seed data | `sql/schema/seed/*.sql` |
| Junction tables | `sql/schema/tables/junctions/*.sql` |
| Significance schema | `sql/schema/tables/core/entity_significance.sql`, `sql/schema/tables/core/edge_significance.sql` |
| 4D/S3 operator surface | one-function-per-file sources under `sql/schema/functions/` such as `dist_4d.sql`, `frechet_4d_geom.sql`, and `hausdorff_4d_geom.sql` |
| Edge significance prime | `sql/schema/functions/prime_unprimed_edges_chunk.sql` and pipeline post-pass code |
| Phase ordering | `src/Hartonomous.Core/Orchestration/Phase.cs` — 12 values |
| Ingestion contract | `src/Hartonomous.Core/Decomposition/IDecomposer.cs` |
| Compute facade | `src/Hartonomous.Core/Compute/IComputeFacade.cs` → `ComputeFacade` → `NativeCompute` |
| Inference engine | `src/Hartonomous.Engine/Inference/SubstrateInferenceEngine.cs` |
| traverse_astar (compiled) | `ext/hartonomous_pg/src/pg_traversal.c` (bulk-JOIN contract per `.claude/rules/35-inference-and-godel.md`) |
| Familiar principle | `docs/familiar-principle.md` |
| Anti-pattern catalog | `.claude/rules/45-anti-patterns.md` (18 documented failures) |
