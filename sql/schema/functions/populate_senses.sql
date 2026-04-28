CREATE OR REPLACE FUNCTION substrate.populate_senses(
    p_codes      TEXT[],
    p_glosses    TEXT[],
    p_lexname_ids INT[],
    p_pos_ids    INT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.sense (code, gloss, lexname_id, pos_id)
    SELECT * FROM unnest(p_codes, p_glosses, p_lexname_ids, p_pos_ids)
    ON CONFLICT (code) DO NOTHING;
END $$;
