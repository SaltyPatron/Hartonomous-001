# Substrate model (the four pillars + sub-surfaces)

Source: `.claude/rules/15-substrate-trinity-and-layers.md`, `docs/00-substrate-spec.md` §II, `.claude/rules/25-physicality-4d.md`.

## Pillars

| Concept | Where | Role | In pre-audit frame? |
|---|---|---|---|
| `substrate.entity` (PK hash) | `.claude/rules/15`, spec §II.1 | One column: BLAKE3 of content. Hash IS the FK everywhere. No surrogate id. No `entity_type_id` on entity row itself. | Y |
| Entity vs content tier split | `.claude/rules/15`, spec §II.1 | Entity-tier = reusable building blocks (codepoint, grapheme_cluster, word_form, morpheme, lemma, synset, collation_element, language_name, tensor, model_architecture, tokenizer_model). Content-tier = trajectories through entities (text_composition, paragraph, document, audio_recording, audio_chunk, pixel_region, video_frame). | Y |
| `substrate.entity_classification` | `.claude/rules/15` | Structural classifications live here, not on entity. Same content can carry multiple classifications without fragmenting identity. | Y |
| `substrate.edge` + `substrate.edge_member` | `.claude/rules/15`, spec §II.2 | Typed n-ary relations. Edges are NOT entities. Hash = `ComputeEdgeHash(edge_type_id, role-ordered participant hashes)`. Partitioned by `edge_type_id`. | Y |
| `substrate.physicality` (universal GeometryZM) | `.claude/rules/25`, spec §II.3 | Single table partitioned by physicality_type_id. POINTZM atom + LINESTRINGZM/MULTILINESTRINGZM/POLYGONZM/etc. compositions. 212-bit mantissa per vertex. GiST-indexed via `gist_geometry_ops_nd`. | Y |
| `substrate.entity_significance(context_type_id, entity_hash)` | spec §IV | Glicko-2 ratings for entity trustworthiness per arena. | Y |
| `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | spec §IV | Glicko-2 ratings for attestation-edge strength per arena. The load-bearing classification consensus surface. | Y |

## Sub-surfaces

| Concept | Where | Role | In pre-audit frame? |
|---|---|---|---|
| Mantissa-packed vertex stream as indexed child manifest | `.claude/rules/15`, `docs/specs/sql/mantissa-exploitation.md` | LINESTRINGZM vertex Y = `bb_pack_ordinal_rle`; (X, Z) = `bb_pack_hash_lo` / `bb_pack_hash_hi`; M = `bb_pack_metadata`. The geometry IS the sequence; no `substrate.sequence` table. Reverse-resolve via `substrate.entity_by_hash_prefix` composite btree on `(hash_bits_0_51, hash_bits_52_103)`. | Y |
| Recursive Merkle centroid composition | `.claude/rules/25` | `centroid(composition) = mean(centroids of ordered constituents)`. Tier-promotion rule. `substrate.st_4d_centroid` aggregate IS the recursion engine. Recursion bottoms out at modality's atom POINTZM with real content-derived coords (codepoint S³, audio sample, pixel intensity, tensor cell). | Y |
| Radial tiering — `tier_hint = 1 - ‖centroid‖₄d` | `.claude/rules/25` | Codepoints project to S³ unit-sphere; compositions land strictly inside open 4-ball; deeper Merkle DAG depth → centroid closer to origin. Substrate-native tier query without classification JOIN: `WHERE substrate.entity_tier_hint(hash) > 0.7`. Combine with `hilbert_index BETWEEN $a AND $b` for angular + radial range scan. | N |
| Memoization: write-once-per-entity centroid | `.claude/rules/25` | Recomputing in hot paths is forbidden. The word `the` has ONE centroid referenced billions of times. | Partial |
| Denormalized `centroid_x/y/z/m + hilbert_index` on `substrate.entity` | `.claude/rules/25` | Maintained by AFTER trigger on physicality (`substrate.update_entity_centroid_from_physicality`). O(1) reads per entity, no JOIN. embedding_firefly partition excluded (per-model decoration, not entity identity). Columns are deterministic by Merkle invariant. | N |
| 4D operator surface (`substrate.st_4d_*`, `substrate.st_s3_*`) | `.claude/rules/25`, `sql/schema/functions/` | st_4d_distance / st_4d_centroid / st_4d_frechet_distance / st_4d_hausdorff_distance / st_4d_dot / st_4d_norm / st_4d_normalize / st_s3_distance / st_s3_centroid. Polymorphic dispatch over GeometryZM subtypes (same-shape AND cross-shape pairs). Forbidden: raw PostGIS ST_Distance / ST_Centroid / ST_FrechetDistance / ST_HausdorffDistance (project to 2D and drop M). | Partial |
| `point4d` / `linestring4d` native compute primitives | `.claude/rules/25`, `docs/specs/native/4d-type-and-index.md` | Internal flat-array types for C kernels; zero PostGIS marshalling overhead. NOT substrate-level user-visible types. NOT substitute for substrate-level GeometryZM storage. | N |
| Sequence/time as one of the four axes | `.claude/rules/25`, `frame/26-MANTISSA-EXPLOITATION.md` | When modality is temporal/sequential, mapping time to an axis is architecturally optimal — 4D GiST treats axes uniformly. Cross-modal alignment becomes 4D-bbox intersect on shared time axis. | N |

## Edge trajectories ARE relation fingerprints

Every edge gets `geom` (GeometryZM) populated at insert from participants' centroids in role order. The trajectory IS the relation's structural fingerprint. `gender_correspondence(king, queen)` and `gender_correspondence(man, woman)` should have geometrically similar trajectories. Analogy completion is `substrate.st_4d_frechet_distance(:query_traj, edge.geom) ORDER BY 1 LIMIT 1` — single Fréchet call on stored geometries, not vector arithmetic.

Same primitive applies to ANY domain with trajectories: linguistic analogy, frayed-edge detection, application error/fault discovery, security pattern matching, performance regression discovery, fraud/anomaly detection, scientific outcome matching. Categorical search misses what doesn't wear the right tag; substrate's geometry-first approach finds it anyway. If pipeline inserts an edge without populating its `geom`, the relation cannot participate in any of these workflows.

## Substrate vs infrastructure layer discipline

**Substrate content** (content-addressed, irreducible, deterministic):
- `substrate.entity` (PK hash only — NOT composite with `entity_type_id`)
- `substrate.entity_classification`, `substrate.edge` + `substrate.edge_member`
- `substrate.physicality` (atom POINTZM + composition LINESTRINGZM)
- `substrate.entity_significance` + `substrate.edge_significance`
- `substrate.entity_model_source`

**App-layer infrastructure** (bounded cardinality, microsecond JOIN, rebuildable from seeds):
- Reference vocabularies: entity_type, edge_type, edge_role, physicality_type, provenance, significance_context, attestation_type, pos, deprel, morph_feature, sense, lexname, semantic_relation_type, general_category, script, block, break_property, language, tensor_role, architecture_class
- Junctions: entity_classification, entity_pos (Glicko-2), entity_language, entity_morph_feature, entity_lexname, codepoint_property, model_architecture_class, tensor_tensor_role, pattern_deprel (Glicko-2), provenance_edge_authority, provenance_modality

Pushing classification (POS, sense, language, structural-kind) into `substrate.entity` is the most common drift. It belongs in the reference + junction layer. Macrolanguage / supersession / has_alternate_name are NOT substrate.edge content — they're metadata between language CODES (rows in `substrate.language` reference table) and live in reference-layer junctions.

## Arenas — open vocabulary

`substrate.significance_context` ships with starter codes in `sql/schema/seed/significance_context.sql`: lexical_disambiguation, syntactic_role_fitness, translation_quality, model_trust, source_authority, semantic_relevance, corroboration_strength, frequency_significance, attention_pattern_confidence, morphological_productivity. Practitioners add their own at runtime. New arenas auto-backfill into existing edges via substrate functions — not via one-shot migration. Code that hardcodes a subset is wrong (AP-1).

Cross-references:
- `frame/01-SUBSTRATE-LAWS.md` — invariants
- `frame/26-MANTISSA-EXPLOITATION.md` — per-physicality-type axis conventions
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — firefly POINTZM in 4D physicality
- `frame/20-VORONOI-CONSENSUS.md` — geometric anomaly family
- `.claude/rules/15-substrate-trinity-and-layers.md` — full discipline
