-- Glicko-2 ratings on edges, per arena. Split from entity_significance.
-- Edge cost during A* traversal = 1 / mu in the requested arena. New arenas
-- (open vocabulary) must auto-prime against every existing edge — see
-- substrate.prime_edge_significance.
CREATE TABLE substrate.edge_significance (
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    edge_type_id    INT NOT NULL,
    edge_hash       substrate.hash_value NOT NULL,
    mu              substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma           substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility      substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash)
    -- FK to substrate.edge application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.edge_significance IS
    'Glicko-2 trust per (edge, arena). Hash-addressable via (edge_type_id, edge_hash). Partitioned by context_type_id. FK application-enforced.';
