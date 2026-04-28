CREATE OR REPLACE FUNCTION substrate.reference_code_map(p_table TEXT)
RETURNS TABLE(id INT, code TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    -- Validate the table identifier — only schema-qualified substrate.* names allowed.
    IF p_table !~ '^substrate\.[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference table: %', p_table;
    END IF;
    RETURN QUERY EXECUTE format('SELECT id, code::text FROM %s', p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_map(TEXT) IS
    'Generic loader: returns (id, code) for any reference table with id INT + code column.';
