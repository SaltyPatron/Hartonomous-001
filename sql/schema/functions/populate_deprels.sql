CREATE OR REPLACE FUNCTION substrate.populate_deprels(p_codes TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.deprel (code)
    SELECT DISTINCT c FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO NOTHING;

    -- Resolve subtyped deprels' parent_id (e.g. 'acl:relcl' → parent 'acl').
    UPDATE substrate.deprel d
       SET parent_id = parent.id
      FROM substrate.deprel parent
     WHERE d.parent_id IS NULL
       AND position(':' IN d.code) > 0
       AND parent.code = split_part(d.code, ':', 1);
END $$;
