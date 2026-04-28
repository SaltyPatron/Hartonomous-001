CREATE OR REPLACE FUNCTION substrate.reference_id_by_code(
    p_table TEXT,
    p_code  TEXT
) RETURNS INT
LANGUAGE plpgsql STABLE
AS $$
DECLARE v_id INT;
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference table: %', p_table;
    END IF;
    EXECUTE format('SELECT id FROM %s WHERE code = $1', p_table)
        INTO v_id USING p_code;
    RETURN v_id;
END $$;
COMMENT ON FUNCTION substrate.reference_id_by_code(TEXT, TEXT) IS
    'Generic loader: return the SERIAL id for a single (code) lookup against any reference table.';
