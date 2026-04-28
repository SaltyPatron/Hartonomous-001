-- 0040_prime_edge_significance.up.sql
--
-- Master plan #61 — initial replacement for the SignificanceField stub.
-- Bulk-inserts edge-level significance rows in the semantic_relevance arena
-- using each edge's provenance trust prior as the starting mu. Without this,
-- every edge sits at the Glicko-2 default (1500) and traversal can't rank
-- paths — every result returns identical scoring, which the user correctly
-- diagnosed as "all the same ELO ranking, zero accuracy."
--
-- After this migration:
--   * WordNet-provenance edges score 95000 in semantic_relevance
--   * UD-provenance edges score 92000
--   * OMW-provenance edges score 90000
--   * UCD-provenance edges score 100000 (authoritative_standard tier)
--   * Wiktionary edges score 68000
--   * Tatoeba edges score 50000
--
-- Paths through higher-trust sources rank above lower-trust sources, which is
-- the minimum signal needed for inference to differentiate. Subsequent arena
-- plays (corroboration_strength) refine these as queries traverse edges.

INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
SELECT NULL,
       e.id,
       (SELECT id FROM substrate.significance_context WHERE code = 'semantic_relevance'),
       p.initial_mu,
       350.0,
       0.06,
       0
FROM substrate.edge e
JOIN substrate.provenance p ON p.id = e.provenance_id
ON CONFLICT DO NOTHING;

-- Also prime the lexical_disambiguation arena (used by the CLI/inference engine
-- defaults) so per-arena traversal scoring works without per-arena seeding.
INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
SELECT NULL,
       e.id,
       (SELECT id FROM substrate.significance_context WHERE code = 'lexical_disambiguation'),
       p.initial_mu,
       350.0,
       0.06,
       0
FROM substrate.edge e
JOIN substrate.provenance p ON p.id = e.provenance_id
ON CONFLICT DO NOTHING;
