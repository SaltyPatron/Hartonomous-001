# Entity Type Addition Checklist

**Status:** Canonical
**Audience:** Engineers adding new entity types to the substrate.

## Pre-flight

- [ ] Entity type's purpose documented in `20-technical/12-entity-types-catalog.md`.
- [ ] Code follows convention: snake_case, descriptive, modality-prefixed where ambiguous (`text_composition`, `audio_chunk`, `pixel_region`).
- [ ] Modality declared correctly: text, image, audio, video, model, universal.

## Schema impact

- [ ] New row inserted in `ref.entity_type` via migration.
- [ ] New partition created on `substrate.entity` for this entity_type_id.
- [ ] New partition created on `substrate.physicality` if this entity type has its own physicality_type rows that don't share an existing partition.
- [ ] Indexes created on the new partition.

## Identity (Law 1)

- [ ] Hash function for this entity type is documented:
  - For atoms: canonical content encoding (e.g., codepoint integer to LE bytes)
  - For compositions: Merkle of ordered child hashes (standard composition rule)

## Geometry (Law 3)

- [ ] If entity type has physicality, the physicality_type row exists in `ref.physicality_type` with correct `dimensionality` and `coordinate_shape`.
- [ ] CHECK constraints on the partition enforce the correct geometry column population.
- [ ] GiST index appropriate for the surface (4D opclass for native_4d surface, PostGIS default for geometry).

## Required edges

- [ ] Required edge types for entities of this type documented (semantic-fidelity Law 12).
- [ ] Validation gate added: orphan-check query for entities of this type without required edges.

## Decomposer

- [ ] At least one decomposer produces entities of this type, documented in `20-technical/`.
- [ ] Decomposer's contract specifies required edges that must accompany emitted entities.

## Validation

- [ ] No row in `ref.entity_type` codes a classification dimension (POS, sense, language) — those go in `ref.pos`, `ref.sense`, `ref.language` (Law 4 / AP-8).

## Documentation

- [ ] Entity type added to `20-technical/12-entity-types-catalog.md` with:
  - Purpose
  - Typical hash content / canonical encoding
  - Required edges (and their roles)
  - Required physicality types (and their coordinate semantics)
  - Decomposers that produce this type

## Cross-references

- Entity type catalog: `20-technical/12-entity-types-catalog.md`
- Schema reference: `20-technical/00-schema-reference.md`
- Law 4 (classification on junctions, not entity types): `10-architecture/01-substrate-laws.md`
- AP-8: `40-process/01-anti-patterns.md`
