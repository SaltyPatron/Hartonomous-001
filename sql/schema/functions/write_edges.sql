-- Substrate-native bulk edge write. Set-based INSERT via unnest + ON
-- CONFLICT DO NOTHING. Edge identity is (edge_type_id, edge_hash) where
-- edge_hash is computed BY THE CALLER from edge_type_id + role-ordered
-- participant hashes via Hartonomous.Core.Compute.Common.Merkle.ComputeEdgeHash
-- — the substrate just stores what arrives.
--
-- Parameters are 4 parallel arrays: edge_type_id, edge_hash, provenance_id,
-- geometry_payload (EWKB BYTEA, nullable — populated inline by the caller
-- via mantissa-packed LINESTRINGZM through participants).
CREATE OR REPLACE FUNCTION substrate.write_edges(
    p_edge_type_ids      INT[],
    p_edge_hashes        BYTEA[],
    p_provenance_ids     INT[],
    p_geometry_payloads  BYTEA[]
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    n_in      INT;
    n_written INT;
BEGIN
    n_in := COALESCE(cardinality(p_edge_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_edge_type_ids) <> n_in
       OR cardinality(p_provenance_ids) <> n_in
       OR cardinality(p_geometry_payloads) <> n_in THEN
        RAISE EXCEPTION 'write_edges: array length mismatch';
    END IF;

    INSERT INTO substrate.edge (edge_type_id, hash, provenance_id, geom)
    SELECT DISTINCT ON (t.edge_type_id, t.edge_hash)
           t.edge_type_id,
           t.edge_hash::substrate.hash_value,
           t.provenance_id,
           CASE WHEN t.geometry_payload IS NULL THEN NULL
                ELSE ST_GeomFromEWKB(t.geometry_payload)
           END
      FROM unnest(p_edge_type_ids, p_edge_hashes, p_provenance_ids, p_geometry_payloads)
           AS t(edge_type_id, edge_hash, provenance_id, geometry_payload)
     ORDER BY t.edge_type_id, t.edge_hash
    ON CONFLICT (edge_type_id, hash) DO NOTHING;

    GET DIAGNOSTICS n_written = ROW_COUNT;
    RETURN n_written;
END $$;

COMMENT ON FUNCTION substrate.write_edges(INT[], BYTEA[], INT[], BYTEA[]) IS
    'Substrate-native bulk edge write. INSERT via unnest + ON CONFLICT (edge_type_id, hash) DO NOTHING. edge_hash computed caller-side from (edge_type_id, role-ordered participant hashes) per substrate identity contract.';
