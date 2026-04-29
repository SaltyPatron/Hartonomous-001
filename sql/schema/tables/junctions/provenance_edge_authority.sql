-- substrate.provenance_edge_authority — explicit overrides for (source, edge_type) μ.
--
-- The default initial_μ for an edge is computed:
--   p.initial_mu × et.semantic_weight × p.derivation_decay
--
-- That's right for most cases — a source's per-modality authority times the
-- structural value of the edge-kind it's emitting, with optional lineage
-- decay. But some sources have specialty authority that breaks the default
-- product: Wiktionary's etymology is much stronger than the default would
-- give (Wiktionary.initial_mu × has_etymology.semantic_weight); WordNet's
-- etymology is much weaker than the default would give (WordNet's general
-- authority is high but it's not curating etymology).
--
-- Explicit rows in this table override the default for those specialty
-- combinations. PK = (provenance_id, edge_type_id).
CREATE TABLE substrate.provenance_edge_authority (
    provenance_id INT    NOT NULL REFERENCES substrate.provenance(id),
    edge_type_id  INT    NOT NULL REFERENCES substrate.edge_type(id),
    initial_mu    FLOAT8 NOT NULL,
    initial_sigma FLOAT8 NOT NULL DEFAULT 350.0,
    PRIMARY KEY (provenance_id, edge_type_id)
);

COMMENT ON TABLE substrate.provenance_edge_authority IS
    'Explicit (source × edge_type) μ/σ overrides. Powers the COALESCE in prime_edge_significance_for_staging — used when a source has specialty authority that doesn''t match the default p.initial_mu × et.semantic_weight × p.derivation_decay product.';
