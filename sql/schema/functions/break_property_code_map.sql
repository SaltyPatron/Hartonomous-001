CREATE OR REPLACE FUNCTION substrate.break_property_code_map()
RETURNS TABLE (id INT, code VARCHAR(32))
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT bp.id, bp.code
      FROM substrate.break_property bp
     ORDER BY bp.id;
$f$;

COMMENT ON FUNCTION substrate.break_property_code_map() IS
    'Return break_property id/code rows for C# UAX #29 cache compatibility.';
