CREATE OR REPLACE FUNCTION substrate.reference_language_alias_map()
RETURNS TABLE(id INT, code TEXT, part1 TEXT, part2b TEXT, part2t TEXT)
LANGUAGE sql STABLE
AS $$
    SELECT id, code::text, part1::text, part2b::text, part2t::text
    FROM substrate.language;
$$;
COMMENT ON FUNCTION substrate.reference_language_alias_map() IS
    'Returns the four ISO-form alias columns from substrate.language for building the canonical-id alias map (code, part1, part2b, part2t). ~8k rows.';
