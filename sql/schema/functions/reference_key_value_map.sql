CREATE OR REPLACE FUNCTION substrate.reference_key_value_map(
    p_table       TEXT,
    p_key_column  TEXT,
    p_value_column TEXT
) RETURNS TABLE(id INT, key_text TEXT, value_text TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_key_column !~ '^[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference args: table=%, key=%, value=%', p_table, p_key_column, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT id, %I::text, %I::text FROM %s',
        p_key_column, p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_key_value_map(TEXT, TEXT, TEXT) IS
    'Generic loader: returns (id, key, value) for tables like morph_feature(key, value) or break_property(code, category).';
