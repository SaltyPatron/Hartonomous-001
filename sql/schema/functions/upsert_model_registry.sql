CREATE OR REPLACE FUNCTION substrate.upsert_model_registry(
    p_name         TEXT,
    p_display_name TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    INSERT INTO substrate.model_registry (name)
    VALUES (p_name)
    ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;
