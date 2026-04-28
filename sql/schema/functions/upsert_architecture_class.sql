CREATE OR REPLACE FUNCTION substrate.upsert_architecture_class(p_code TEXT)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    INSERT INTO substrate.architecture_class (code) VALUES (p_code)
    ON CONFLICT (code) DO UPDATE SET code = EXCLUDED.code
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;
