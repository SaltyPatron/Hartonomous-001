CREATE OR REPLACE FUNCTION substrate.break_property_full_map()
RETURNS TABLE (id INT, category TEXT, enum_id INT, code TEXT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT bp.id, bp.category::text, bp.enum_id, bp.code::text
      FROM substrate.break_property bp
     ORDER BY bp.category, bp.enum_id;
$f$;

COMMENT ON FUNCTION substrate.break_property_full_map() IS
    'Full break_property rows (id, category, enum_id, code) keyed for composite (category, enum_id) lookup in the UCD decomposer.';
