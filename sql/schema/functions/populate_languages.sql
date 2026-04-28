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
    INSERT INTO substrate.language (code, name, scope, type)
    SELECT
        code,
        name,
        scope::CHAR(1),
        type::CHAR(1)
    FROM unnest(p_codes, p_names, p_scopes, p_types) AS t(code, name, scope, type)
    ON CONFLICT (code) DO UPDATE
        SET name  = EXCLUDED.name,
            scope = EXCLUDED.scope,
            type  = EXCLUDED.type;
END $$;
