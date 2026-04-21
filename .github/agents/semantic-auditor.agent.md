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
| 4 | ud_token | 7 | UD-tokenized unit with POS/deprel. |
| 4 | ud_sentence | 6 | UD-parsed sentence. Composition of ud_tokens. |
| 5 | synset | 13 | WordNet synset. lemma → synset via `has_sense` (edge type 1). |
| 5 | word_sense | 14 | WordNet sense key. |
| 5 | wikt_sense | 15 | Wiktionary sense with etymology. |
| 5 | inflected_form | 16 | Inflected form → lemma via `inflection_of` (edge type 9). |

## Substrate layers

**Entity content**: `substrate.entity`. Atoms + compositions. BLAKE3 hash of content only. 25 types.

**Edge content**: `substrate.edge` + `substrate.edge_member`. N-ary relations with significance + trajectory geometry. 33 types, 7 roles. Edge hash = `ComputeEdgeHash(edgeTypeId, participantHashes)`.

**Physicality**: `substrate.physicality`. POINTZM/LINESTRINGZM/MULTILINESTRINGZM. 13 types. GiST-indexed. `ST_FrechetDistance`.

**Reference vocabulary**: migration `0004`. `entity_type` (25), `edge_type` (33), `edge_role` (7), `physicality_type` (13), `significance_context` (10), `provenance` (10), `pos` (17+), `deprel`, `morph_feature`, `sense`, `lexname` (45), `language`, `general_category`, `script`, `block`, `break_property`, `architecture_class`, `tensor_role` (27). NOT entities.

**Junction surfaces**: migration `0007`. `entity_pos` (Glicko-2), `entity_sense` (Glicko-2), `entity_language`, `entity_morph_feature`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel` (Glicko-2). NOT edges.

**Reconstruction metadata**: `sequence.ordinal_position`, `provenance`, edges `has_source`/`in_model`. Never in identity hash.

## Conventional-AI traps

| Trap | Why wrong |
|------|-----------|
| "Knowledge graph" | Edges carry significance + geometry + n-ary structure. Inference = Glicko-2, not SPARQL. |
| "Vector database" | No embeddings/ANN/cosine. Exact S3/Fréchet geometry. |
| "RAG" | No forward pass, no generation model, no context stuffing. |
| POS/sense as entities | Reference vocab (0004) + junction evidence (0007). Infrastructure, not content. |
| Placement in hash | Position/filename/ordinal → sequence/edges/provenance. Never in BLAKE3 hash. |
| Inference creates edges | Inference traverses + reweights. Does not create structural edges. |

## Enforcement code

| Artifact | Location |
|----------|----------|
| Atom hash | `BaseDecomposer.ComputeHash(ReadOnlySpan<byte>)` |
| Composition hash | `BaseDecomposer.ComputeMerkleHash(ReadOnlySpan<byte[]>)` |
| Edge hash | `BaseDecomposer.ComputeEdgeHash(int, ReadOnlySpan<byte[]>)` |
| Entity schema | `sql/migrations/0006_core_tables.up.sql` |
| Reference tables | `sql/migrations/0004_reference_tables.up.sql` |
| Seed data | `sql/migrations/0005_phase1_seed.up.sql` |
| Junction tables | `sql/migrations/0007_junction_tables.up.sql` |
| Significance | `substrate.significance` in 0006 (10 arenas) |
| Phase ordering | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Decomposer contract | `src/Hartonomous.Core/Decomposition/IDecomposer.cs` |
| Compute facade | `src/Hartonomous.Core/Compute/IComputeFacade.cs` |
