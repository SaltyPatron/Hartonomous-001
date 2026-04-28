CREATE OR REPLACE FUNCTION substrate.reference_int64_set(
    p_table  TEXT,
    p_column TEXT
) RETURNS TABLE(value BIGINT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, column=%', p_table, p_column;
    END IF;
    RETURN QUERY EXECUTE format('SELECT %I::bigint FROM %s', p_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_int64_set(TEXT, TEXT) IS
    'Generic loader: returns the BIGINT values of one column from a reference/junction table.';
