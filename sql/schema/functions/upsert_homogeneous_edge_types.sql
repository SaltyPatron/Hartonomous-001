CREATE OR REPLACE FUNCTION substrate.upsert_homogeneous_edge_types(
    p_codes            TEXT[],
    p_category         TEXT,
    p_entity_type_code TEXT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_type_id INT := (SELECT id FROM substrate.entity_type WHERE code = p_entity_type_code);
BEGIN
    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    SELECT c, p_category, v_type_id, v_type_id FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO UPDATE
        SET category       = EXCLUDED.category,
            source_type_id = EXCLUDED.source_type_id,
            target_type_id = EXCLUDED.target_type_id;
END $$;
