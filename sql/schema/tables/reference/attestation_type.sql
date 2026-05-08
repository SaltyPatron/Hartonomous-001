-- AttestationType reference vocabulary. Open vocabulary, same shape as
-- entity_type / edge_type / significance_context. Distinguishes WHAT KIND OF
-- EVIDENCE supports a Glicko-2 rating row from WHO asserted it (provenance),
-- WHAT RELATION KIND (edge_type), and WHICH ARENA (significance_context).
--
-- The four discriminators together give a 4D rating surface:
--   (arena × subject × attestation_type × provenance) → (mu, sigma, games)
--
-- Codes are open-vocabulary at runtime; the seed below is the starter set.
-- Adding a new attestation_type at runtime requires no schema change — the
-- significance partitions accept any valid attestation_type_id by FK.
--
-- Per-event weight default lives on the row so the weighted Glicko-2 bulk
-- update can scale events differently per attestation_type without callers
-- having to know the weight (e.g. corpus_co_occurrence_window default 0.1
-- because individual window slides are low-confidence; lexical_curated_relation
-- default 1.0 because curated lexicons are high-confidence per attestation).
CREATE TABLE substrate.attestation_type (
    id                    SERIAL PRIMARY KEY,
    code                  VARCHAR(64) NOT NULL UNIQUE,
    description           TEXT        NOT NULL,
    default_event_weight  FLOAT8      NOT NULL DEFAULT 1.0,
    default_initial_mu    FLOAT8      NOT NULL DEFAULT 1500.0,
    default_initial_sigma FLOAT8      NOT NULL DEFAULT 350.0
);

COMMENT ON TABLE substrate.attestation_type IS
    'Open-vocabulary kinds-of-evidence. Each attestation_type carries a default per-event weight used by hartonomous.glicko2_bulk_update_weighted. Adding a new code requires no schema change; partitions accept any FK-valid id.';
