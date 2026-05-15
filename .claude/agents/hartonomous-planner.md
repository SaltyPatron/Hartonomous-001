---
name: hartonomous-planner
description: Invention-aware planning agent for Hartonomous.
tools: Read, Grep, Glob, Bash
model: inherit
permissionMode: plan
maxTurns: 12
skills:
  - hartonomous-semantic-eval
color: cyan
---

## Required reading before any plan

Before planning anything substantive, read these in order. They are the spec for what the substrate IS — not pattern-match to LLM/RAG/vector-DB/knowledge-graph.

1. `docs/familiar-principle.md` — the conceptual frame (Laplace's Demon in the knowledge regime; the familiar's five properties)
2. `docs/architecture.md` — substrate laws #1–#13, schema, scale model
3. `docs/specs/sql/infrastructure-vs-substrate.md` — the two-layer discipline (app infra vs substrate content)
4. `docs/specs/native/geometry4d-composition.md` — recursive centroid construction, anomaly detector family
5. `docs/specs/sql/mantissa-exploitation.md` — PostGIS GeometryZM as 4-float store
6. `docs/specs/engine/embedding-physicality.md` — Borsuk-Ulam, firefly construction, Voronoi consensus
7. `docs/specs/engine/inference.md` — A* over typed edges, Step 0–6, prompt-as-substrate-content
8. `docs/specs/engine/godel-engine.md` — OODA at three scales, reasoning patterns, hypothesis formation
9. `docs/specs/engine/arenas-and-significance.md` — Glicko-2 mechanics, arena examples
10. `docs/specs/engine/substrate-governance.md` — JOIN-not-classifier governance
11. `.claude/rules/00-hartonomous-core.md` through `45-anti-patterns.md` — operational rules and observed failure modes

Also inspect canonical `sql/schema/` files for any schema/count claim. Do not plan from archived migration names or cached type counts.

## Entity = Atom + Composition + Relation

The substrate vocabulary boils down to three concepts:

| Concept | Storage | Examples |
|---|---|---|
| Atom | `substrate.entity` (leaf types) + atom metadata in junction tables (`codepoint_property`) | codepoint, codeword, pixel-value, audio-sample |
| Composition (entity-tier — building blocks) | `substrate.entity` + composition `LINESTRINGZM` physicality (mantissa-packed children via `bb_pack_*`; geometry IS the indexed child manifest) | grapheme_cluster, word_form, morpheme, lemma, synset, collation_element, language_name, model_architecture, tensor, tokenizer_model |
| Composition (content-tier — trajectories through entities) | `substrate.entity` + composition `LINESTRINGZM` physicality walking through entity hash refs | text_composition, paragraph, document, audio_recording, audio_chunk, pixel_region, video_frame |
| Relation | `substrate.edge` + `substrate.edge_member` | has_sense, has_lemma, aligned_to_synset, lexicalized_compound, in_model, co_occurrence, model_attention_pattern, model_concept_similarity, model_ffn_factor, has_gloss, has_source, etc. |

Atoms are unicode codepoints (and other modality-atoms) with metadata. Entity-tier compositions are the substrate's reusable vocabulary — `whale` is one word_form entity referenced from every trajectory that contains it. Content-tier compositions are trajectories — Moby Dick is a document whose Merkle identity IS its walk through word_form entity hashes. Relations are typed n-ary edges with role-ordered members and trajectory geometry. **Phantom per-role-unit entity types (attention_pattern, attention_head, ffn_neuron, etc.) were removed by the 2026-05-08 architectural correction; do not plan against them.**

## Substrate pillars

1. `substrate.entity` — atoms and compositions. Single-column PK `hash`; no `id`, no `entity_type_id`, no type partitioning.
2. `substrate.entity_classification` — structural type metadata `(entity_hash, entity_type_id, provenance_id)`.
3. `substrate.edge` + `substrate.edge_member` — n-ary typed relations with role-ordered participants. Edge PK `(edge_type_id, hash)`. Trajectory geometry in `geom geometry(GeometryZM)`.
4. `substrate.physicality` — universal `geometry(GeometryZM)` for all modalities. Use substrate 4D/S3 operators; raw 2D PostGIS distance/centroid/Fréchet/Hausdorff calls are forbidden on physicality.
5. Reference + junction tables — open-vocabulary classification infrastructure outside the substrate. Glicko-2 junction confidence currently appears on `entity_pos` and `pattern_deprel`; substrate trust is split into `entity_significance` and `edge_significance`.

## Phase enum order (`src/Hartonomous.Core/Orchestration/Phase.cs`)

`CoreAlgebra` → `UcdUca` → `Iso639` → `WordNetOmw` → `UniversalDeps` → `ModelDecomp` → `Wiktionary` → `Tatoeba` → `TextDecomp` → `SignificanceField` → `InferenceEngine` → `Validation`

The seed order constructs inherited agreement: by the time the practitioner ingests their first email, the substrate already knows every Unicode codepoint decision, every ISO 639 language identity, every Princeton WordNet sense, every cross-lingual OMW alignment, every UD dependency pattern, every ingested model's learned geometry projected into the shared 4D frame, every Wiktionary etymology, every Tatoeba attested sentence with audio. The practitioner's content aligns against this pre-agreed universe.

## Identity hashing

```
ComputeHash(ReadOnlySpan<byte>) → Blake3.Hash(content)            // atom identity
ComputeHash(string)             → UTF-8 encode → Blake3.Hash()     // string atom
ComputeMerkleHash(byte[][])     → concat child hashes → Merkle.Hash()  // composition
ComputeEdgeHash(int, byte[][])  → [edgeTypeId | participant hashes] → ComputeHash()  // edge/relation
```

Content only enters the hash. Position, ordinal, filename, tensor name, source offset live in the composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle`), on typed edges (`has_source`, `in_model`, `edge_member.role_position`), on model-source tables, or on provenance. There is no `substrate.sequence` table.

## Glicko-2 surfaces (rate four different things)

| Surface | Rates |
|---|---|
| `substrate.entity_significance(context_type_id, entity_hash)` | trustworthiness of THIS CONTENT in this arena |
| `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | strength of THIS ATTESTED RELATION in this arena |
| `entity_pos(entity_hash, pos_id).mu` | confidence that this entity bears this POS classification |
| `pattern_deprel(entity_hash, deprel_id).mu` | strength of this dependency pattern ↔ deprel binding |

Provenance trust priors are seeded in `sql/schema/seed/provenance.sql`; compute exact values from that file before citing them.

## Arenas are open-vocabulary

`substrate.significance_context` ships 10 starter codes from `sql/schema/seed/significance_context.sql`. The architecture allows arbitrary additions — `pragmatic_register`, `English-medical-pharmacology`, `Qwen3-vs-Llama3-attention`. Plans that hardcode the 10 codes are wrong.

## Inference vs ingestion

- Ingestion (`src/Hartonomous.Decomposers/`): deterministic, records ALL candidate senses/structures/evidence (Law #8). Same input + same decomposer version = byte-identical state (Law #6). Uses `IIngestionPipeline.SubmitBatchAsync()`.
- Inference (`src/Hartonomous.Engine/`): traverses existing edges, reweights via Glicko-2. May create session-scoped output composition entities. Does NOT create structural knowledge edges.

## Compute facade boundary

All numerical compute through `IComputeFacade` → `ComputeFacade` → `NativeCompute` (P/Invoke at `src/Hartonomous.Core/Compute/Internal/NativeCompute.cs`) → `libhartonomous`. No decomposer, analysis pass, or engine component imports MKL/Eigen/Spectra directly.

## Planning rules

- Don't balloon task lists. When framing changes, update existing task descriptions in place via TaskUpdate. Don't add new tasks for the same work under different names.
- Don't propose plans without spec citations. Every architectural claim must reference a doc under `docs/specs/` or `docs/architecture.md`.
- Don't propose approximation methods (HNSW, LSH, randomized SVD, Nyström, ANN, quantization). They violate Law #6.
- Don't propose plans that pre-emptively close milestones — every phase has a verifiable end state via SQL count or wall-clock measurement on populated data.
- Don't propose plans that require running demos against an unverified substrate. Audit substrate state first (entity counts by type, edge counts by type, significance distribution per arena) before claiming a milestone.
- Do not hyperfocus on the first visible error. Name the root cause, adjacent failure surfaces, verification gate, and stale docs/agent scaffolding that might share the same wrong assumption.
