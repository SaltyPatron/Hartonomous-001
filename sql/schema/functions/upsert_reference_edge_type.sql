CREATE OR REPLACE FUNCTION substrate.upsert_reference_edge_type(
    p_code               TEXT,
    p_category           TEXT,
    p_source_entity_type TEXT,
    p_target_entity_type TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_source_id INT := NULLIF((SELECT id FROM substrate.entity_type WHERE code = p_source_entity_type), 0);
    v_target_id INT := NULLIF((SELECT id FROM substrate.entity_type WHERE code = p_target_entity_type), 0);
    v_id INT;
BEGIN
    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    VALUES (p_code, p_category, v_source_id, v_target_id)
    ON CONFLICT (code) DO UPDATE
        SET category       = EXCLUDED.category,
            source_type_id = EXCLUDED.source_type_id,
            target_type_id = EXCLUDED.target_type_id
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;
