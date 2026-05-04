-- Restore prior provenance values + drop new structures.
DROP TABLE IF EXISTS substrate.provenance_edge_authority;

ALTER TABLE substrate.provenance
    DROP CONSTRAINT IF EXISTS provenance_derives_from_fkey;
ALTER TABLE substrate.provenance
    DROP COLUMN IF EXISTS scope_entity_hash,
    DROP COLUMN IF EXISTS scope_entity_type_id,
    DROP COLUMN IF EXISTS scope_kind,
    DROP COLUMN IF EXISTS initial_sigma,
    DROP COLUMN IF EXISTS derivation_decay,
    DROP COLUMN IF EXISTS derives_from,
    DROP COLUMN IF EXISTS modality_codes;

ALTER TABLE substrate.edge_type
    DROP COLUMN IF EXISTS semantic_weight;

-- Restore the chess-narrow seed values that 0005 originally inserted.
UPDATE substrate.provenance SET initial_mu = 2000.0 WHERE code = 'unicode_consortium';
UPDATE substrate.provenance SET initial_mu = 2000.0 WHERE code = 'sil_international';
UPDATE substrate.provenance SET initial_mu = 1800.0 WHERE code = 'princeton_wordnet';
UPDATE substrate.provenance SET initial_mu = 1600.0 WHERE code = 'omwn_consortium';
UPDATE substrate.provenance SET initial_mu = 1600.0 WHERE code = 'universaldependencies';
UPDATE substrate.provenance SET initial_mu = 1400.0 WHERE code = 'wiktextract';
UPDATE substrate.provenance SET initial_mu = 1200.0 WHERE code = 'tatoeba';
UPDATE substrate.provenance SET initial_mu = 1500.0 WHERE code = 'huggingface_model';
UPDATE substrate.provenance SET initial_mu = 1000.0 WHERE code = 'user_session';
UPDATE substrate.provenance SET initial_mu = 1300.0 WHERE code = 'system_computed';

-- Restore the previous prime_edge_significance_for_staging without compound formula.
CREATE OR REPLACE FUNCTION substrate.prime_edge_significance_for_staging()
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_inserted BIGINT;
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT sc.id, e.edge_type_id, e.hash, p.initial_mu, 350.0, 0.06, 0
      FROM staging_edge s
      JOIN substrate.edge e
        ON e.edge_type_id = s.edge_type_id
       AND e.hash         = s.hash
      JOIN substrate.provenance p ON p.id = e.provenance_id
      CROSS JOIN substrate.significance_context sc
        ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;
    RETURN v_inserted;
END $$;

DELETE FROM substrate.entity_type WHERE code IN ('tenant', 'user');
