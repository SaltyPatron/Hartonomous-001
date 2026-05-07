-- Per-entity classification metadata. Content (entity_hash) is identity;
-- classification (entity_type_id) is metadata. Multiple decomposers can
-- independently assert classifications on the same content; provenance
-- distinguishes them. ("dog" attested as word_form by Tatoeba and as lemma
-- by WordNet → two classification rows, one entity row.)
CREATE TABLE IF NOT EXISTS substrate.entity_classification (
    entity_hash    substrate.hash_value NOT NULL,
    entity_type_id INT  NOT NULL REFERENCES substrate.entity_type(id),
    provenance_id  INT  NOT NULL REFERENCES substrate.provenance(id),
    asserted_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (entity_hash, entity_type_id, provenance_id)
);

COMMENT ON TABLE substrate.entity_classification IS
    'Per-entity classification metadata. Content (entity_hash) is identity; classification (entity_type_id) is metadata. Multiple decomposers can independently assert classifications on the same content; provenance distinguishes them.';
