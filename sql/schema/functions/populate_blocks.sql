CREATE OR REPLACE FUNCTION substrate.populate_blocks(
    p_codes        TEXT[],
    p_range_starts INT[],
    p_range_ends   INT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.block (code, range_start, range_end)
    SELECT * FROM unnest(p_codes, p_range_starts, p_range_ends)
    ON CONFLICT (code) DO NOTHING;
END $$;
