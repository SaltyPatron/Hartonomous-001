CREATE OR REPLACE FUNCTION substrate.populate_morph_features(
    p_keys   TEXT[],
    p_values TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.morph_feature (key, value)
    SELECT * FROM unnest(p_keys, p_values)
    ON CONFLICT (key, value) DO NOTHING;
END $$;
