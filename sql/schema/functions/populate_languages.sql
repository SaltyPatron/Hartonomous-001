CREATE OR REPLACE FUNCTION substrate.populate_languages(
    p_codes  TEXT[],
    p_names  TEXT[],
    p_scopes TEXT[],
    p_types  TEXT[],
    p_part1s TEXT[],
    p_part2bs TEXT[],
    p_part2ts TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.language (code, name, scope, type, part1, part2b, part2t)
    SELECT
        code,
        name,
        scope,
        type,
        NULLIF(part1,  ''),
        NULLIF(part2b, ''),
        NULLIF(part2t, '')
    FROM unnest(p_codes, p_names, p_scopes, p_types, p_part1s, p_part2bs, p_part2ts)
        AS t(code, name, scope, type, part1, part2b, part2t)
    ON CONFLICT (code) DO UPDATE
        SET name   = EXCLUDED.name,
            scope  = EXCLUDED.scope,
            type   = EXCLUDED.type,
            part1  = EXCLUDED.part1,
            part2b = EXCLUDED.part2b,
            part2t = EXCLUDED.part2t;
END $$;
