CREATE OR REPLACE FUNCTION substrate.reference_code_text_map(
    p_table        TEXT,
    p_value_column TEXT
) RETURNS TABLE(code TEXT, value_text TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, value=%', p_table, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT code::text, %I::text FROM %s',
        p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_text_map(TEXT, TEXT) IS
    'Generic loader: returns (code, some-other-text-column) for reference tables.';
