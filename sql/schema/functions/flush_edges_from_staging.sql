-- Drains staging_edge into substrate.edge, one partition (edge_type_id) at a
-- time. Pairs with flush_entities_from_staging — same per-partition INSERT
-- shape, same rationale (task #86 partition-router corruption avoidance).
-- Edge identity is content-addressed (BLAKE3 of edge_type_id + ordered
-- participant hashes) so ON CONFLICT (edge_type_id, hash) is the dedup key.
CREATE OR REPLACE FUNCTION substrate.flush_edges_from_staging()
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    t INT;
BEGIN
    FOR t IN SELECT DISTINCT edge_type_id FROM staging_edge LOOP
        INSERT INTO substrate.edge (edge_type_id, hash, provenance_id)
        SELECT DISTINCT ON (edge_type_id, hash) edge_type_id, hash, provenance_id
        FROM staging_edge
        WHERE edge_type_id = t
        ON CONFLICT (edge_type_id, hash) DO NOTHING;
    END LOOP;
END $$;
