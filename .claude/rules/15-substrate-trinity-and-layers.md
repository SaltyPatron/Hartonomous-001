---
description: The four pillars expressed as schema. Atom + composition + relation + geometry as one vocabulary; reference + junction as separate infrastructure layer. Loads on substrate-touching code.
paths:
  - sql/**
  - src/Hartonomous.Core/**
  - src/Hartonomous.Decomposers/**
  - src/Hartonomous.Engine/**
  - src/Hartonomous.Recomposers/**
  - ext/**
  - docs/specs/sql/**
  - docs/specs/decomposers/**
  - docs/specs/engine/**
---

## The substrate is one vocabulary, partitioned across tables for indexing

Atoms, compositions, and relations are one substrate vocabulary. The schema splits them across tables for partitioning and index reasons, but they are facets of the same content-addressed Merkle DAG — each row is a node or edge whose identity is BLAKE3 of content.

| Concept | Storage | Content of the identity |
|---|---|---|
| **Atom** | `substrate.entity` (PK `hash`), classified for structural kind in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)` | BLAKE3 over content bytes alone (codepoint integer, audio sample, pixel intensity, etc.) |
| **Composition** | `substrate.entity` (PK `hash`), classified the same way for higher-tier types | Merkle hash over ordered child hashes. Geometry: LINESTRINGZM through child centroids in `substrate.physicality` |
| **Relation** | `substrate.edge(edge_type_id, hash)` + `substrate.edge_member(edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)` | `ComputeEdgeHash(edge_type_id, role-ordered participant hashes)`. Edge `geom` = LINESTRINGZM through participants' centroids in role order |
| **Geometry** | `substrate.physicality(physicality_type_id, entity_hash, content_hash, geom geometry(GeometryZM))` | Per-tier-of-modality 4D realization. POINTZM for atoms, LINESTRINGZM / MULTILINESTRINGZM / POLYGONZM / etc. for compositions. Memoized per-entity; centroids are write-once and referenced by hash from every parent. |
| **Per-arena Glicko-2 ratings** | `substrate.entity_significance(context_type_id, entity_hash)` + `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | What this content / relation is worth in this arena. Cross-source corroboration fires separate `attestation_type`-distinguished rating events on the same row. |
| **Sequence / reconstruction** | Composition `LINESTRINGZM` physicality vertex stream — `(X = bb_pack_hash_lo(child.hash_bits_0_51), Y = bb_pack_ordinal_rle(ordinal, rle_count), Z = bb_pack_hash_hi(child.hash_bits_52_103), M = bb_pack_metadata(0))` | Ordering metadata lives in the Y mantissa of the geometry. There is no separate `substrate.sequence` table — the geometry IS the indexed child manifest. Reverse-resolve via `substrate.entity_by_hash_prefix` composite btree. |

Identity is content. Hash IS the foreign key. There are no surrogate `id BIGSERIAL` columns on the entity surface. Same content from any decomposer collapses to one entity row via `ON CONFLICT (hash) DO NOTHING`. Multiple structural classifications of the same content (e.g. `dog` is both `word_form` and `lemma`) materialize as multiple rows in `substrate.entity_classification` against the same `entity_hash`. Atoms carry metadata via junction tables (`codepoint_property` is the canonical example). Compositions emerge through LINESTRINGZM physicality (vertices are mantissa-packed identity-POINTZMs of children — `(X, Z)` carry the child's BLAKE3 hash prefix, `Y` carries `bb_pack_ordinal_rle(ordinal, rle_count)`, `M` is reserved) plus typed adjacency edges; the recursion is unbounded — a tier-T composition's LINESTRINGZM has vertices that are tier-(T−1) entity hash refs, each of which is itself the root of its own LINESTRINGZM through tier-(T−2) refs, bottoming out at the modality's atom POINTZM (codepoint S³, audio sample, pixel intensity, etc.).

Placement metadata — position, ordinal, filename, tensor name, source offset, line number, model id — NEVER enters the hash. It lives in the composition LINESTRINGZM's Y mantissa (`bb_pack_ordinal_rle`), on typed edges (`has_source`, `in_model`, role_position on edge_member), on model-source tables, or on provenance. Same content in two places is one entity referenced from two trajectories.

## Two trees, one vocabulary — entities are bricks, content is the walk

**Entity tier — the building blocks.** Reusable identities the substrate stores once and references from many trajectories. Entity types: `codepoint`, `grapheme_cluster`, `word_form`, `morpheme`, `lemma`, `synset`, `collation_element`, `language_name`, `model_architecture`, `tensor`, `tokenizer_model`. Entity-tier physicality is the brick's own internal structure — an atom POINTZM with real content-derived coords (codepoint S³ by UCA rank, audio sample value, pixel intensity) or a composition LINESTRINGZM through tier-below entity hash refs (`word_form("cat")` = 3-vertex LINESTRING packing c/a/t codepoint hashes; this is *the brick's structure*, not a content trajectory).

**Content tier — the trajectories through entities.** A specific walk through bricks. Content types: `text_composition`, `paragraph`, `document`, `audio_recording`, `audio_chunk`, `pixel_region`, `video_frame`. Content-tier physicality is the walk — `text_composition("the cat sat on the mat")` = 6-vertex LINESTRING through 6 word_form entity hash refs; `whale` appears ~1500 times in Moby Dick's document trajectory but is one word_form entity referenced 1500 times. The trajectory IS the content's identity.

Both kinds of rows live in `substrate.entity` keyed by BLAKE3 hash; both can carry physicality and edges. The distinction is *what role the row plays in the Merkle DAG*:

| Axis | Entity tier (brick) | Content tier (trajectory) |
|---|---|---|
| Reuse | Many trajectories reference one entity | Each trajectory is its own identity (Merkle over child hashes in order) |
| `physicality.content_hash` | Degenerate (one row per `(type, entity)`; same decomposer version → same geometry → same content_hash) | Load-bearing (N ingestion paths through the same content can yield N different segmentations → N physicality rows on one entity, anchored by `has_source` to N provenance rows) |
| Cross-source accumulation surface | Cross-model attestation EDGES between entities (Glicko-2 events tighten sigma) | Cross-source physicality realizations + `has_source` attestation events on the content's provenance |
| AI-model attestations | Models attest entity↔entity edges (`model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor`, `model_cross_modal_pattern`) — granularity = per-(token_a, token_b) pair, NOT model trajectories | Models do NOT contribute content trajectories; the substrate's content tier comes from corpora + user prompts + other content-tier ingest |
| `has_source` edges | Rare (entity identity is content-only) | Required (every content trajectory anchors to its origin via `has_source` with provenance) |

Edges divide along the same axis: entity↔entity (within the building-block tree — `has_form`, `has_lemma`, `has_morpheme`, `has_sense`, case mappings, semantic synset↔synset, cross-lingual lemma↔lemma, every model_* attestation surface), entity↔content (bridges — `has_gloss` synset→text_composition, `has_etymology` lemma→text_composition, `has_pronunciation`, `has_canonical_decomposition` codepoint→text_composition, model artifact bindings model_architecture→text_composition), content↔content (Unicode named/emoji/ZWJ sequences text_composition→text_composition, `translation_link`, `recording_of`), and the universal `has_source` from content trajectories to provenance.

Why the order of seed work follows from this: Unicode + WordNet/OMW/Wiktionary/UD/Tatoeba seed BOTH tiers (the entity-tier vocabulary AND content-tier trajectories of glosses / examples / sentences / definitions). AI models then attest on the entity-tier edge surface that the corpora have already populated. Without the corpora floor the first model's tokenizer is the only word_form source, every word_form provenance points at that model, and cross-source consensus has no comparison floor.

## Two strict layers — substrate vs infrastructure

**Substrate content** (content-addressed, irreducible, deterministic):
- `substrate.entity` (PK `hash` only — NOT composite with `entity_type_id`; carries `hash_bits_0_51` + `hash_bits_52_103` GENERATED columns for composition vertex reverse-resolve)
- `substrate.entity_classification` — the structural classification(s)
- `substrate.edge` + `substrate.edge_member`
- `substrate.physicality` — atom POINTZM + composition LINESTRINGZM, the latter mantissa-packed and serving as the indexed child manifest (no separate `substrate.sequence` table)
- `substrate.entity_significance` + `substrate.edge_significance`
- `substrate.entity_model_source`

**App-layer infrastructure** (bounded cardinality, microsecond JOIN, rebuildable from seeds):
- Reference vocabularies: `entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `attestation_type`, `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `semantic_relation_type`, `general_category`, `script`, `block`, `break_property`, `language`, `tensor_role`, `architecture_class`.
- Junctions: `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `cp_*` property caches, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`, `provenance_edge_authority`, `provenance_modality`.

Classification consensus is not an `entity` row attribute. Reference vocabulary codes have bounded lookup rows and, where they are attestation targets, content-hashed entity rows reached by typed edges (`has_classification`, transitional `has_pos`, `has_language`, `has_morph_feature`, `has_deprel_pattern`, etc.). The authoritative cross-source consensus is the typed edge plus `substrate.edge_significance` per provenance and arena. Junction tables are analytics caches for lookup locality, not the truth surface.

## Glicko-2 on substrate surfaces

| Surface | Rates |
|---|---|
| `substrate.entity_significance(context_type_id, entity_hash)` | trustworthiness of THIS CONTENT in this arena |
| `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | strength of THIS ATTESTED RELATION in this arena, including classification claims |

The older four-surface framing is deprecated by AP-8. Classification evidence competes on the unified edge-significance surface; junction scores, where present, are denormalized analytics caches.

## Arenas — open vocabulary, no hardcoded list

`substrate.significance_context` ships with starter codes in `sql/schema/seed/significance_context.sql`. Practitioners add their own at runtime. The pipeline's edge-significance priming cross-products against whatever arenas exist at insert time — no `WHERE context_type_id IN (...)` filter. New arenas added later auto-backfill into existing edges via substrate functions. Code that hardcodes a subset is wrong (AP-1).

## Seed-uses-core

Every text-bearing content from any seed (Wiktionary citations, WordNet glosses, UD sentences, Tatoeba sentences, safetensors config JSON values, image captions, audio transcripts) routes through the core text decomposer (`Hartonomous.Core.Text.CanonicalTextDecomposer.Emit`). Same content collapses to one `text_composition` regardless of source. Seed decomposers MUST NOT call `ComputeHash(string)` or `ComputeAtomicStringHash(string)` on user-visible text to produce text_composition-tier entities — that fragments the substrate.

## Per-role units of Track 2 tensors = attestation EDGES, not phantom entities

Every per-role unit of every Track 2 transformation tensor (each FFN row, each attention head's QK pattern, each MoE expert neuron, each LoRA rank component, each layer norm scale) **manifests as a typed attestation EDGE between existing content entities** — typically two `word_form` tokens the unit binds, resolved through the model's tokenizer to existing content. The `edge_type_id` encodes the relationship; provenance + arena + rating attribution encode source/mechanism/domain; `attestation_type` is only the sign-bearing discriminator (`positive_evidence`, `negative_evidence`, `neutral_evidence`) while the column is on the deprecation path. The edge's `LINESTRINGZM` trajectory is the unit's spectral fingerprint; the Glicko mu derives from the tensor math. Sign is preserved via Glicko `score`.

Cross-model corroboration fires separate rating events on the same edge identity. Phantom per-role-unit entity types (`attention_head`, `ffn_neuron`, `embedding_position`, `attention_pattern`, `moe_expert_neuron`, `lora_component`, etc.) are absent from `sql/schema/seed/entity_type.sql`; current seed count is 34 including reference-vocabulary and UCD-property entity targets. The phantom decomposer passes have been replaced by layer-type tuple/primitive passes. Working template: `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.

## Ingestion vs inference (Law #8)

- **Ingestion** (`src/Hartonomous.Decomposers/`) — deterministic; records ALL candidate senses/structures/evidence without disambiguation. Same input + same decomposer version = byte-identical state. Decomposers are pure producers; the single `StreamingIngestionPipeline` owns channels and COPY-into-temp-inflight-then-INSERT-SELECT drains. Edge trajectory population and significance priming are drain-completion work inside `DrainPendingAsync`, independent of orchestration phases.
- **Inference** (`src/Hartonomous.Engine/`) — traverses existing edges, reweights via Glicko-2. May create session-scoped output composition entities (the answer itself, with `user_session` provenance, plus the explanation trace as substrate content). Does NOT create new structural knowledge edges. Glicko-2 updates from outcomes are not "new edges."

Cross-references:
- [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) — authoritative substrate spec
- [`docs/familiar-principle.md`](../../docs/familiar-principle.md) — conceptual frame
- [`docs/specs/sql/infrastructure-vs-substrate.md`](../../docs/specs/sql/infrastructure-vs-substrate.md) — full layer-discipline probe study
- [`docs/specs/engine/arenas-and-significance.md`](../../docs/specs/engine/arenas-and-significance.md) — Glicko-2 mechanics
- [`.claude/rules/25-physicality-4d.md`](25-physicality-4d.md) — the geometry layer
- [`.claude/rules/35-inference-and-godel.md`](35-inference-and-godel.md) — traversal mechanics
- [`.claude/rules/45-anti-patterns.md`](45-anti-patterns.md) — drift catalog
