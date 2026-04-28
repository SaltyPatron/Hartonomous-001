-- Drains staging_entity_significance into substrate.entity_significance,
-- one partition (context_type_id) at a time. Glicko-2 defaults
-- (sigma=350, volatility=0.06, games=0) are baked into the INSERT so the
-- C# layer only ships (context_type_id, entity_type_id, entity_hash, mu).
CREATE OR REPLACE FUNCTION substrate.flush_entity_significance_from_staging()
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    c INT;
BEGIN
    FOR c IN SELECT DISTINCT context_type_id FROM staging_entity_significance LOOP
        INSERT INTO substrate.entity_significance
            (context_type_id, entity_type_id, entity_hash, mu, sigma, volatility, games)
        SELECT
            context_type_id, entity_type_id, entity_hash, mu, 350.0, 0.06, 0
        FROM staging_entity_significance
        WHERE context_type_id = c
        ON CONFLICT (context_type_id, entity_type_id, entity_hash) DO NOTHING;
    END LOOP;
END $$;
