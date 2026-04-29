-- substrate.edge_type — typed-relation vocabulary.
--
-- Categories partition the LIST-partitioned substrate.edge table for index
-- locality (structural, semantic, syntactic, morphological, cross_lingual,
-- cross_modal, model_derived, unicode).
--
-- semantic_weight is the structural-value tier of the edge-kind for the
-- COALESCE prior formula:
--   μ₀ = COALESCE(pea.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay)
--
-- Tier ladder (set in seed/edge_type.sql):
--   1.0   has_sense, has_lemma, has_form, inflection_of, hypernym, hyponym,
--         instance_hypernym, instance_hyponym, antonym
--   0.9   member/substance/part holonyms+meronyms, has_morpheme
--   0.85  translation_of, aligned_to_synset, translation_link
--   0.7   has_etymology, has_pronunciation, has_hyphenation, has_wikidata
--   0.6   similar_to, also_see, verb_group, attribute, derivationally_related
--   0.5   synonym, related, coordinate_term, derived
CREATE TABLE substrate.edge_type (
    id              SERIAL PRIMARY KEY,
    code            VARCHAR(64) NOT NULL UNIQUE,
    category        VARCHAR(32) NOT NULL,
    source_type_id  INT REFERENCES substrate.entity_type(id),
    target_type_id  INT REFERENCES substrate.entity_type(id),
    -- Structural-value tier for COALESCE prior. Default 1.0 (full weight).
    semantic_weight FLOAT8 NOT NULL DEFAULT 1.0
);
CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);
COMMENT ON TABLE substrate.edge_type IS
    'Operational edge typing with domain/range entity type constraints + structural-value tier (semantic_weight) for the trust-prior formula. Categories: structural, semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, unicode.';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS
    'FK to entity_type — constrains which entity types can be source. NULL means polymorphic source.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS
    'FK to entity_type — constrains which entity types can be target. NULL means polymorphic target.';
COMMENT ON COLUMN substrate.edge_type.semantic_weight IS
    'Structural-value tier 0.5..1.0. POS/sense/antonym/structural carry full weight (1.0); looser semantic relations (synonym, related, coordinate_term) carry less. Multiplied into the COALESCE prior μ at edge_significance lookup time.';
