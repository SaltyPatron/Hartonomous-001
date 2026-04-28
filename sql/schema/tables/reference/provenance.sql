CREATE TABLE substrate.provenance (
    id            SERIAL PRIMARY KEY,
    code          VARCHAR(64) NOT NULL UNIQUE,
    curator_class VARCHAR(32) NOT NULL,
    initial_mu    FLOAT8 NOT NULL
);
COMMENT ON TABLE substrate.provenance IS
    'Source provenance with trust prior. initial_mu seeds Glicko-2 significance for entities and edges from this source.';
COMMENT ON COLUMN substrate.provenance.curator_class IS
    'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';
