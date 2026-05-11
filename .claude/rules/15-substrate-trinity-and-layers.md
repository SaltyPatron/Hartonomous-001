## Entity is the trinity: Atom + Composition + Relation

Every architectural decision must respect that the substrate's vocabulary boils down to three concepts. The schema separates them across tables for partitioning and performance, but they are one vocabulary.

Canonical specification: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §II for the four-pillar substrate model and §III for per-role units as attestation edges (NOT phantom entities).

| Concept | Storage | Examples |
|---|---|---|
| **Atom** | `substrate.entity` (PK = `hash`), classified by `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)` for leaf entity types | `codepoint` (UCD properties on `codepoint_property` junction), `audio_chunk` sample, `pixel_region` atom |
| **Composition** | `substrate.entity` (PK = `hash`), classified by `substrate.entity_classification` for higher-tier types; hash = Merkle of ordered child hashes; geometry = LINESTRINGZM through child centroids in `substrate.physicality` | `grapheme_cluster`, `word_form`, `lemma`, `text_composition`, `paragraph`, `document`, `synset`, `audio_recording`, plus model-side structural artifacts: `tensor`, `model_architecture`, `tokenizer_model` |
| **Relation** | `substrate.edge` keyed `(edge_type_id, hash)` + `substrate.edge_member` with `entity_hash` single-column FK to entity and composite `(edge_type_id, edge_hash)` FK to edge | `has_sense`, `has_lemma`, `aligned_to_synset`, `lexicalized_compound`, `in_model`, `co_occurrence`, `recording_of`, `model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor`, etc. |

> **Note on per-role units of Track 2 transformation tensors** (FFN rows, attention head Q/K patterns, MoE expert neurons, LoRA rank components, embedding rows, etc.): these are NOT compositions in the substrate. They manifest as **typed attestation EDGES** in the Relation row above, between existing content entities (typically two `word_form` tokens). The phantom entity types `attention_pattern`, `attention_head`, `ffn_neuron`, `embedding_position`, etc. that appear in older docs and in transitional `entity_type` seed rows 19-54 are deprecated by the 2026-05-08 architectural correction. See [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §III and AP-25 in [`45-anti-patterns.md`](45-anti-patterns.md).

Identity = BLAKE3 hash. Hash IS the foreign key everywhere on the entity surface — there are no surrogate `id BIGSERIAL` columns. Same content from any decomposer collapses to one entity row via `ON CONFLICT (hash) DO NOTHING`. Multiple structural classifications of the same content (e.g. `dog` is both `word_form` and `lemma`) materialize as multiple rows in `substrate.entity_classification` against the same `entity_hash`. Atoms carry metadata via junction tables (`codepoint_property` is the canonical example). Compositions emerge through their **LINESTRINGZM physicality (vertices = ordered child centroids)** plus typed adjacency edges between consecutive atoms — the composition's hash is the Merkle of its ordered child hashes, and recursive: a parent composition's LINESTRINGZM uses each child composition's centroid as a single vertex. Relations carry trajectory geometry through participants in role order plus a Glicko-2 rating per arena.

Atoms, compositions, and relations are stored separately for indexing reasons. They are NOT separate concepts — they are one substrate vocabulary partitioned by structural kind.

## Two strict layers — see `docs/specs/sql/infrastructure-vs-substrate.md`

**App-layer infrastructure** (bounded cardinality, microsecond JOIN, rebuildable from seeds):
- Reference tables: `entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `semantic_relation_type`, `general_category`, `script`, `block`, `break_property`, `language`, `tensor_role`, `architecture_class`, plus runtime additions.
- Junction tables: `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`, `provenance_edge_authority`. Glicko-2 junction confidence currently appears on `entity_pos` and `pattern_deprel`.

**Substrate content** (content-addressed, irreducible, deterministic):
- `substrate.entity` (PK `hash` only — NOT composite with entity_type_id)
- `substrate.entity_classification` (PK `(entity_hash, entity_type_id, provenance_id)`) — carries the structural classification(s)
- `substrate.edge` (PK `(edge_type_id, hash)`)
- `substrate.edge_member` (composite hash FKs to edge and entity)
- `substrate.physicality` (composite hash FK to entity, geometry(GeometryZM))
- `substrate.entity_significance` (PK `(context_type_id, entity_hash)`)
- `substrate.edge_significance` (PK `(context_type_id, edge_type_id, edge_hash)`)
- `substrate.sequence` (composition/reconstruction ordering: `parent_hash`, `ordinal`, `child_hash`, `rle_count`)

Pushing classification (POS, sense, language) into `substrate.entity` is the most common drift. It belongs in reference + junction tables. Macrolanguage / supersession / has_alternate_name are likewise NOT substrate.edge content — they're metadata between language CODES (rows in `substrate.language` reference table), and live in reference-layer junctions.

## Glicko-2 lives on FOUR distinct surfaces

| Surface | Rates |
|---|---|
| `substrate.entity_significance(context_type_id, entity_hash)` | trustworthiness of THIS CONTENT in this arena |
| `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | strength of THIS ATTESTED RELATION in this arena |
| `entity_pos(entity_hash, pos_id).mu` | confidence that this entity bears this POS classification |
| `pattern_deprel(entity_hash, deprel_id).mu` | strength of this dependency pattern ↔ deprel binding |

Substrate significance rates *what is there*. Junction Glicko rates *what we say about what is there*. Do not merge relation trust, entity trust, and classification confidence.

## Arenas are open-vocabulary, not a fixed list of 10

`substrate.significance_context` ships with 10 starter codes from `sql/schema/seed/significance_context.sql` (`lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, `model_trust`, `source_authority`, `semantic_relevance`, `corroboration_strength`, `frequency_significance`, `attention_pattern_confidence`, `morphological_productivity`). The architecture allows arbitrary additions: `pragmatic_register` (proposed in `docs/specs/engine/substrate-governance.md`), `English-medical-pharmacology`, `Qwen3-vs-Llama3-attention`, `arXiv-2024-vs-textbook-2010`, etc.

Code that hard-codes the 10 starter arena codes is wrong. The pipeline's edge-significance priming MUST cross-product against whatever arenas exist at insert time, with no WHERE filter on context code. Adding a new arena later MUST auto-backfill into existing edges via a substrate function (not a one-shot migration).

## Seed-uses-core (non-negotiable)

Every text-bearing content from any seed (Wiktionary citations, WordNet glosses, UD sentences, Tatoeba sentences, safetensors config JSON values, image captions, audio transcripts) MUST be routed through the core text decomposer (`Hartonomous.Core.Text.CanonicalTextDecomposer.Emit` or the core text path). Same content collapses to ONE `text_composition` regardless of which seed contributed it. Seed decomposers MUST NOT call `ComputeHash(string)` or `ComputeAtomicStringHash(string)` on user-visible text to produce text_composition-tier entities themselves — that produces phantom duplicates instead of reusing the existing core decomposer's hashing.

## Inference vs ingestion (Law #8)

- **Ingestion** (`src/Hartonomous.Decomposers/`): deterministic, records ALL candidate senses/structures/evidence without disambiguation. Same input + same decomposer version = byte-identical state.
- **Inference** (`src/Hartonomous.Engine/`): traverses existing edges, reweights via Glicko-2. May create session-scoped output compositions. Does NOT create new structural knowledge edges. If inference code calls `IIngestionPipeline.SubmitBatchAsync()` with structural edges, that's a boundary violation.

## Cross-references
- [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) — canonical substrate specification (authoritative; this rule is a slice of §II and §IV)
- `docs/familiar-principle.md` — the conceptual frame
- `docs/specs/sql/infrastructure-vs-substrate.md` — full layer discipline + probe case studies
- `docs/specs/engine/arenas-and-significance.md` — Glicko-2 mechanics + arena examples
- `.claude/rules/25-physicality-4d.md` — the geometry layer
- `.claude/rules/35-inference-and-godel.md` — A* + Glicko-2 inference centerpiece
- `.claude/rules/45-anti-patterns.md` — failure modes documented from observed drift (canonical AP location; AP-25 covers per-role-unit-as-entity sabotage)
