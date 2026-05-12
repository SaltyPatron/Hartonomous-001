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
| **Sequence / reconstruction** | `substrate.sequence(parent_hash, ordinal, child_hash, rle_count)` | Ordering metadata for compositions; never enters identity hash. |

Identity is content. Hash IS the foreign key. There are no surrogate `id BIGSERIAL` columns on the entity surface. Same content from any decomposer collapses to one entity row via `ON CONFLICT (hash) DO NOTHING`. Multiple structural classifications of the same content (e.g. `dog` is both `word_form` and `lemma`) materialize as multiple rows in `substrate.entity_classification` against the same `entity_hash`. Atoms carry metadata via junction tables (`codepoint_property` is the canonical example). Compositions emerge through LINESTRINGZM physicality (vertices ARE child centroids) plus typed adjacency edges; the recursion is unbounded — a tier-T composition's LINESTRINGZM has vertices that are tier-(T−1) centroids, each of which is itself the aggregate of a tier-(T−2) LINESTRINGZM.

Placement metadata — position, ordinal, filename, tensor name, source offset, line number, model id — NEVER enters the hash. It lives on `substrate.sequence`, on typed edges (`has_source`, `in_model`), on model-source tables, or on provenance. Same content in two places is one entity with two edges.

## Two strict layers — substrate vs infrastructure

**Substrate content** (content-addressed, irreducible, deterministic):
- `substrate.entity` (PK `hash` only — NOT composite with `entity_type_id`)
- `substrate.entity_classification` — the structural classification(s)
- `substrate.edge` + `substrate.edge_member`
- `substrate.physicality`
- `substrate.entity_significance` + `substrate.edge_significance`
- `substrate.sequence`

**App-layer infrastructure** (bounded cardinality, microsecond JOIN, rebuildable from seeds):
- Reference vocabularies: `entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `attestation_type`, `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `semantic_relation_type`, `general_category`, `script`, `block`, `break_property`, `language`, `tensor_role`, `architecture_class`.
- Junctions: `entity_classification`, `entity_pos` (Glicko-2), `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel` (Glicko-2), `provenance_edge_authority`.

Pushing classification (POS, sense, language, structural-kind) into `substrate.entity` is the most common drift. It belongs in the reference + junction layer. Macrolanguage / supersession / has_alternate_name are also NOT substrate.edge content — they're metadata between language CODES (rows in `substrate.language` reference table) and live in reference-layer junctions.

## Glicko-2 on four surfaces — what each rates

| Surface | Rates |
|---|---|
| `substrate.entity_significance(context_type_id, entity_hash)` | trustworthiness of THIS CONTENT in this arena |
| `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | strength of THIS ATTESTED RELATION in this arena |
| `entity_pos(entity_hash, pos_id).mu` | confidence that this entity bears this POS classification |
| `pattern_deprel(entity_hash, deprel_id).mu` | strength of this dependency pattern ↔ deprel binding |

Substrate significance rates *what is there*. Junction Glicko rates *what we say about what is there*. Do not merge relation trust, entity trust, and classification confidence.

## Arenas — open vocabulary, no hardcoded list

`substrate.significance_context` ships with starter codes in `sql/schema/seed/significance_context.sql`. Practitioners add their own at runtime. The pipeline's edge-significance priming cross-products against whatever arenas exist at insert time — no `WHERE context_type_id IN (...)` filter. New arenas added later auto-backfill into existing edges via substrate functions. Code that hardcodes a subset is wrong (AP-1).

## Seed-uses-core

Every text-bearing content from any seed (Wiktionary citations, WordNet glosses, UD sentences, Tatoeba sentences, safetensors config JSON values, image captions, audio transcripts) routes through the core text decomposer (`Hartonomous.Core.Text.CanonicalTextDecomposer.Emit`). Same content collapses to one `text_composition` regardless of source. Seed decomposers MUST NOT call `ComputeHash(string)` or `ComputeAtomicStringHash(string)` on user-visible text to produce text_composition-tier entities — that fragments the substrate.

## Per-role units of Track 2 tensors = attestation EDGES, not phantom entities

Every per-role unit of every Track 2 transformation tensor (each FFN row, each attention head's QK pattern, each MoE expert neuron, each LoRA rank component, each layer norm scale) **manifests as a typed attestation EDGE between existing content entities** — typically two `word_form` tokens the unit binds, resolved through the model's tokenizer to existing content. The `edge_type_id` encodes the relationship; the `attestation_type` encodes the evidence kind; the edge's `LINESTRINGZM` trajectory is the unit's spectral fingerprint; the Glicko mu derives from the tensor math. Sign is preserved via Glicko `score`.

Cross-model corroboration fires separate `attestation_type`-distinguished rating events on the same edge identity. Phantom per-role-unit entity types (`attention_head`, `ffn_neuron`, `embedding_position`, `attention_pattern`, `moe_expert_neuron`, `lora_component`, etc.) are deprecated by the 2026-05-08 architectural correction (`sql/schema/seed/entity_type.sql:59-98`) because they fragment cross-model consensus into per-source debris. Working template: `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.

## Ingestion vs inference (Law #8)

- **Ingestion** (`src/Hartonomous.Decomposers/`) — deterministic; records ALL candidate senses/structures/evidence without disambiguation. Same input + same decomposer version = byte-identical state. Decomposers are pure producers; the single `StreamingIngestionPipeline` owns channels, COPY-into-temp-inflight-then-INSERT-SELECT drain, inline edge trajectory build (with end-of-phase backfill fallback), and end-of-phase significance priming across whatever arenas exist.
- **Inference** (`src/Hartonomous.Engine/`) — traverses existing edges, reweights via Glicko-2. May create session-scoped output composition entities (the answer itself, with `user_session` provenance, plus the explanation trace as substrate content). Does NOT create new structural knowledge edges. Glicko-2 updates from outcomes are not "new edges."

Cross-references:
- [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) — authoritative substrate spec
- [`docs/familiar-principle.md`](../../docs/familiar-principle.md) — conceptual frame
- [`docs/specs/sql/infrastructure-vs-substrate.md`](../../docs/specs/sql/infrastructure-vs-substrate.md) — full layer-discipline probe study
- [`docs/specs/engine/arenas-and-significance.md`](../../docs/specs/engine/arenas-and-significance.md) — Glicko-2 mechanics
- [`.claude/rules/25-physicality-4d.md`](25-physicality-4d.md) — the geometry layer
- [`.claude/rules/35-inference-and-godel.md`](35-inference-and-godel.md) — traversal mechanics
- [`.claude/rules/45-anti-patterns.md`](45-anti-patterns.md) — drift catalog
