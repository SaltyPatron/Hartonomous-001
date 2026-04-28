CREATE OR REPLACE FUNCTION substrate.populate_scripts(p_codes TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.script (code)
    SELECT DISTINCT c FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO NOTHING;
END $$;
