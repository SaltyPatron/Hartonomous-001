CREATE OR REPLACE FUNCTION substrate.populate_general_categories(
    p_codes        TEXT[],
    p_group_codes  TEXT[],
    p_descriptions TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.general_category (code, group_code, description)
    SELECT * FROM unnest(p_codes, p_group_codes, p_descriptions)
    ON CONFLICT (code) DO NOTHING;
END $$;
