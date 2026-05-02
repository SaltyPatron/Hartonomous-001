# Edge Type Addition Checklist

**Status:** Canonical
**Audience:** Engineers adding new edge types to the substrate.

## Pre-flight

- [ ] Edge type's purpose documented in `20-technical/11-edge-types-catalog.md`.
- [ ] Code follows convention: snake_case, descriptive (`hypernym`, `dep_nsubj`, `translation_of`, `beaten_path`).
- [ ] Category declared correctly: structural, semantic, syntactic, cross_lingual, cross_modal, model_derived, unicode.

## Identity (Law 1a)

- [ ] Edge type ID will be part of edge identity hash (`BLAKE3(edge_type_id || participants)`).
- [ ] Different edge types between same entities produce different edge rows (this is correct — preserves type-distinct attestations).

## Schema impact

- [ ] New row inserted in `ref.edge_type` via migration with:
  - `code`
  - `category`
  - `arity` (typically 2; some are higher-arity)
  - `directionality` (directed/undirected)
  - `symmetry` if applicable
  - `transitivity` if applicable
  - `inverse_id` linking to its inverse type if exists (e.g., hypernym ↔ hyponym)
  - `semantic_family` grouping similar types
  - `description`

- [ ] New partition created on `substrate.edge` for this edge_type_id.
- [ ] New partition created on `substrate.edge_member` matching.
- [ ] Indexes (B-tree on identity, GiST on linestring4d if applicable) created on partitions.

## Geometry

- [ ] Edges of this type have a `linestring4d` trajectory through participants in role order.
- [ ] Roles are declared via `ref.edge_role` and the order of participants determines trajectory.
- [ ] Trajectory is computed at insert from participants' centroids.

## Significance

- [ ] Initial trust priors propagate from `provenance.initial_mu` per edge.
- [ ] Lazy materialization in `substrate.edge_significance` is the default (no eager priming).
- [ ] Glicko-2 updates work for this edge type (validation: ingest two attestations, run outcome event, verify mu changes).

## Required participants

- [ ] Source and target entity types documented (e.g., `hypernym` connects synset → synset, `has_sense` connects lemma → synset).
- [ ] Foreign key composite from `edge_member.entity_type_id, entity_hash` to `substrate.entity` enforced.

## Decomposer

- [ ] At least one decomposer produces edges of this type, documented.
- [ ] Decomposer correctly populates `geom`/`linestring4d` from participant centroids.

## Inverse handling

- [ ] If this edge type has an inverse (e.g., hypernym ↔ hyponym), the inverse's `inverse_id` points to this type and vice versa.
- [ ] Substrate functions that need both directions handle this via inverse traversal.

## Validation

- [ ] Edge type does NOT bake type into entity hash (Law 1).
- [ ] Edge type IS part of edge hash (Law 1a).
- [ ] Direction matters when applicable (asymmetric edges have one canonical role order).

## Documentation

- [ ] Edge type added to `20-technical/11-edge-types-catalog.md` with:
  - Purpose
  - Source and target entity types
  - Roles (and their semantics)
  - Inverse relationship if exists
  - Decomposers that produce this type
  - Example queries

## Cross-references

- Edge type catalog: `20-technical/11-edge-types-catalog.md`
- Identity layer: `10-architecture/02-identity-and-convergence.md`
- Geometry: `10-architecture/03-geometry-4d.md`
- Significance: `10-architecture/04-significance-glicko.md`
