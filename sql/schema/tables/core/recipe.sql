-- substrate.recipe — content-addressed recipe entities. Recipes are
-- first-class substrate content (not files), per the three-tier data
-- model:
--   * App tier: starter recipe rows seeded at db-bootstrap from
--     sql/schema/seed/recipe_starter.sql (minilm-base, bert-base,
--     llama-7b, mistral-7b, qwen-7b). The factory defaults.
--   * Substrate tier: recipes auto-derived from SafetensorsDecomposer
--     ingest. Identity = BLAKE3 of canonical recipe JSON. Same model
--     ingested twice → same recipe row (ON CONFLICT DO NOTHING). Cross-
--     practitioner consensus: identical ingestion produces the identical
--     row across substrates.
--   * User tier: practitioner-edited forks. Provenance carries
--     practitioner/tenant identity; `derived_from` typed edge links fork
--     to parent recipe entity.
--
-- The row stores the canonical JSON as bytea so downstream consumers can
-- BLAKE3-verify the entity_hash matches sha-of-payload. A separate
-- (entity_hash, code) lookup junction (substrate.recipe_name) provides
-- human-friendly names → entity_hash resolution; multiple names can map
-- to the same recipe content.
CREATE TABLE substrate.recipe (
    entity_hash    substrate.hash_value NOT NULL,
    canonical_json BYTEA                NOT NULL,
    PRIMARY KEY (entity_hash)
    -- FK to substrate.entity application-enforced. Same content-addressed
    -- discipline as every other substrate entity-payload table.
);

COMMENT ON TABLE substrate.recipe IS
    'Content-addressed recipe payload. entity_hash = BLAKE3(canonical_json). Recipes are substrate content; ingestion emits them automatically; export resolves them by name or hash. Three-tier: app starter recipes seeded at bootstrap, substrate recipes auto-derived from ingest, user recipes are practitioner forks.';
