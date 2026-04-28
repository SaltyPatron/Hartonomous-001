-- Glicko-2 ratings on entities, per arena. Split from edge_significance to
-- avoid the XOR-discriminator complexity of a single significance table —
-- queries that rank entities only touch this table; queries that rank edges
-- only touch edge_significance.
CREATE TABLE substrate.entity_significance (
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_type_id  INT NOT NULL,
    entity_hash     substrate.hash_value NOT NULL,
    mu              substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma           substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility      substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, entity_type_id, entity_hash)
    -- FK to substrate.entity application-enforced (composite-FK validation
    -- against partitioned parents is the documented PG18 partitionwise-FK
    -- issue). Pipeline batch ordering guarantees the entity exists before
    -- its significance row is written.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.entity_significance IS
    'Glicko-2 trust per (entity, arena). Hash-addressable via (entity_type_id, entity_hash). Partitioned by context_type_id. FK application-enforced.';
