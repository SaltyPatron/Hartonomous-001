CREATE OR REPLACE FUNCTION substrate.populate_break_properties(
    p_codes      TEXT[],
    p_categories TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.break_property (code, category)
    SELECT * FROM unnest(p_codes, p_categories)
    ON CONFLICT (code, category) DO NOTHING;
END $$;
