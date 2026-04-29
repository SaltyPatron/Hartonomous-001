-- substrate.prime_edge_significance_for_staging() — compound-formula rewrite.
--
-- CREATE OR REPLACE replaces the previous flat-prior version. Initial μ for
-- a new edge in a given arena is now the four-product:
--
--   μ₀ = COALESCE(
--          pea.initial_mu,                                        -- explicit override (provenance × edge_type)
--          p.initial_mu × et.semantic_weight × p.derivation_decay -- computed default
--        )
--
--   σ₀ = COALESCE(pea.initial_sigma, p.initial_sigma)
--
-- Why each factor:
--   p.initial_mu          — source authority for the source's modality
--   et.semantic_weight    — content-kind value (POS/sense/antonym >> related/similar_to)
--   p.derivation_decay    — lineage discount (OMW = 0.92 × WordNet's authority)
--   pea (junction table)  — explicit per-(source, edge_type) overrides
--                           (Wiktionary's etymology authority overrides
--                            Wiktionary.initial_mu × has_etymology.semantic_weight)
--
-- Cross-products against every arena currently in significance_context
-- (open-vocabulary, no cherry-picking — AP-1).
CREATE OR REPLACE FUNCTION substrate.prime_edge_significance_for_staging()
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT
        sc.id,
        e.edge_type_id,
        e.hash,
        COALESCE(
            pea.initial_mu,
            p.initial_mu * et.semantic_weight * p.derivation_decay
        ) AS mu,
        COALESCE(
            pea.initial_sigma,
            p.initial_sigma
        ) AS sigma,
        0.06,
        0
      FROM staging_edge s
      JOIN substrate.edge e
        ON e.edge_type_id = s.edge_type_id
       AND e.hash         = s.hash
      JOIN substrate.edge_type   et ON et.id = e.edge_type_id
      JOIN substrate.provenance  p  ON p.id  = e.provenance_id
      CROSS JOIN substrate.significance_context sc
      LEFT JOIN substrate.provenance_edge_authority pea
        ON pea.provenance_id = p.id
       AND pea.edge_type_id  = e.edge_type_id
        ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.prime_edge_significance_for_staging() IS
    'Per-batch: prime substrate.edge_significance with compound-formula μ and σ from (provenance × edge_type × modality × lineage), with optional explicit overrides via substrate.provenance_edge_authority. Open-vocabulary across every arena currently in significance_context.';
