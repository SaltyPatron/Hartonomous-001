-- Stage 0025: substrate.entity_classification junction.
--
-- Phase C step 1 of the unification refactor (docs/specs/text-decomposer-unification.md).
-- The substrate's content-addressed identity claim says: same content =
-- same BLAKE3 hash = same entity. The composite primary key
-- (entity_type_id, hash) on substrate.entity violates this: the same
-- content "dog" classified as both word_form AND lemma gets stored as TWO
-- rows. Classifications belong on a junction, not in identity.
--
-- This migration ADDS the junction. It does NOT yet drop entity_type_id
-- from substrate.entity or its dependents — that's migrations 0026 / 0027.
-- Additive change; no breakage.
--
-- A trigger / inline drain logic emits a classification row for every
-- entity emitted with a type code. Multiple decomposers attaching the
-- same hash with different types produce N classification rows but ONE
-- entity row (post-0027).
--
-- Provenance is per-classification: it records which decomposer asserted
-- the classification (Tatoeba says "dog is a word_form"; WordNet says
-- "dog is a lemma"; both rows coexist on the same entity hash).

CREATE TABLE substrate.entity_classification (
    entity_hash    substrate.hash_value NOT NULL,
    entity_type_id INT  NOT NULL REFERENCES substrate.entity_type(id),
    provenance_id  INT  NOT NULL REFERENCES substrate.provenance(id),
    asserted_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (entity_hash, entity_type_id, provenance_id)
);

CREATE INDEX idx_entity_classification_type ON substrate.entity_classification(entity_type_id, entity_hash);
CREATE INDEX idx_entity_classification_provenance ON substrate.entity_classification(provenance_id);

COMMENT ON TABLE substrate.entity_classification IS
    'Per-entity classification metadata. Content (entity_hash) is identity; classification (entity_type_id) is metadata. Multiple decomposers can independently assert classifications on the same content; provenance distinguishes them.';

-- No backfill needed on fresh installs: substrate.entity is empty when this
-- migration applies in the canonical drop/create/migrate flow. Existing
-- entities are emitted by the decomposers post-migration via the streaming
-- pipeline's classification staging path. The migration is structural-only.
