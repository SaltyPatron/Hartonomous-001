DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
    p_type_ids INT[], p_hashes BYTEA[]
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, provenance_id INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id, e.hash, e.provenance_id
      FROM unnest(p_type_ids, p_hashes) AS in_(t, h)
      JOIN substrate.edge e ON e.edge_type_id = in_.t AND e.hash = in_.h;
$f$;
