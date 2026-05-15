---
name: semantic-auditor
description: Audit Hartonomous claims for semantic drift.
---

## Text decomposition stack

| Level | Entity type | ID | Description |
|-------|------------|-----|-------------|
| 0 | codepoint | 1 | Unicode scalar value. Atom. |
| 1 | grapheme_cluster | 2 | UAX #29 extended grapheme cluster. Composition of codepoints. |
| 2 | word_form | 3 | Surface form as attested. Not lemmatized. |
| 3 | morpheme | 4 | Bound or free morpheme. |
| 3 | lemma | 5 | Dictionary headword. word_form → lemma via `has_lemma` (edge type 3). |
| 4 | text_composition | 6 | Canonical text composition emitted by `CanonicalTextDecomposer`. |
| 5 | paragraph | 7 | Higher-order text composition. |
| 6 | document | 8 | Document-level composition. |
| 7 | synset | 9 | WordNet synset. lemma → synset via `has_sense` (edge type 1). |

## Substrate layers

**Entity content**: `substrate.entity`. Atoms + compositions. BLAKE3 hash of content only. Single-column PK `hash`. No `id`, no `entity_type_id`.

**Entity classification**: `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`. Same content can carry multiple structural classifications.

**Edge content**: `substrate.edge` + `substrate.edge_member`. N-ary relations with edge significance + trajectory geometry. Edge PK `(edge_type_id, hash)`. Edge hash = `ComputeEdgeHash(edgeTypeId, participantHashes)`.

**Physicality**: `substrate.physicality`. Universal PostGIS `geometry(GeometryZM)` storage, partitioned by `physicality_type_id`. Use `substrate.st_4d_*` / `substrate.st_s3_*`; raw `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, and `ST_HausdorffDistance` are forbidden on substrate physicality. `public.point4d` / `public.linestring4d` are internal native compute primitives, not substrate storage columns.

**Reference vocabulary**: current canonical seed files under `sql/schema/seed/`. Counts must be recomputed from the seed files before citing — phantom entity types were removed 2026-05-08 (23 real content types remain, split by role: 11 entity-tier building blocks + 7 content-tier trajectory types + 5 cross-cutting like `tensor` / `model_architecture` / `tokenizer_model` / `collation_element` / `language_name`); `edge_type` count includes 13 seeded-but-empty Unicode rows (98–112) pending blob SRF exports; `physicality_type` is 13 in the base seed plus `embedding_firefly` from the firefly seed file. `significance_context` is 10 starter arenas (open vocabulary); `provenance` is ~10. Reference vocabularies are NOT entities.

**Junction surfaces**: canonical files under `sql/schema/tables/junctions/`. Evidence and classification infrastructure, not edges. Glicko-2 junction confidence currently appears on `entity_pos` and `pattern_deprel`; edge/entity substrate trust lives separately on `edge_significance` and `entity_significance`.

**Reconstruction metadata**: composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle`; no `substrate.sequence` table), `provenance`, edges `has_source`/`in_model`. Never in identity hash.

## Conventional-AI traps

| Trap | Why wrong |
|------|-----------|
| "Knowledge graph" | Edges carry significance + geometry + n-ary structure. Inference = Glicko-2, not SPARQL. |
| "Vector database" | No embeddings/ANN/cosine. Exact S3/Fréchet geometry. |
| "RAG" | No forward pass, no generation model, no context stuffing. |
| POS/sense as entities | Reference vocab + junction evidence. Infrastructure, not content. |
| Placement in hash | Position/filename/ordinal → sequence/edges/provenance. Never in BLAKE3 hash. |
| Inference creates edges | Inference traverses + reweights. Does not create structural edges. |

## Enforcement code

| Artifact | Location |
|----------|----------|
| Atom hash | `BaseDecomposer.ComputeHash(ReadOnlySpan<byte>)` |
| Composition hash | `BaseDecomposer.ComputeMerkleHash(ReadOnlySpan<byte[]>)` |
| Edge hash | `BaseDecomposer.ComputeEdgeHash(int, ReadOnlySpan<byte[]>)` |
| Entity schema | `sql/schema/tables/core/entity.sql` |
| Edge schema | `sql/schema/tables/core/edge.sql`, `sql/schema/tables/core/edge_member.sql` |
| Physicality schema | `sql/schema/tables/core/physicality.sql` |
| Reference seed data | `sql/schema/seed/*.sql` |
| Junction tables | `sql/schema/tables/junctions/*.sql` |
| Significance | `sql/schema/tables/core/entity_significance.sql`, `sql/schema/tables/core/edge_significance.sql` |
| Phase ordering | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Decomposer contract | `src/Hartonomous.Core/Decomposition/IDecomposer.cs` |
| Compute facade | `src/Hartonomous.Core/Compute/IComputeFacade.cs` |
