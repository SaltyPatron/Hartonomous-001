-- Glicko-2 ratings on entities, per arena. Hash-only entity reference
-- (Phase C of unification refactor — substrate.entity has hash-only PK,
-- no entity_type_id).
CREATE TABLE substrate.entity_significance (
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_hash     substrate.hash_value NOT NULL,
    mu              substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma           substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility      substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, entity_hash)
    -- FK to substrate.entity(hash) application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.entity_significance IS
    'Glicko-2 trust per (entity, arena). Hash-only entity reference. Partitioned by context_type_id.';
