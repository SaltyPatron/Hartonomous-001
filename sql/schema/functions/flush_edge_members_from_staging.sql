-- Drains staging_edge_member into substrate.edge_member, one partition
-- (edge_type_id) at a time. edge_member is partitioned by edge_type_id to
-- co-locate with substrate.edge. Per-partition INSERT avoids the same
-- multi-partition routing crash class as flush_entities_from_staging.
CREATE OR REPLACE FUNCTION substrate.flush_edge_members_from_staging()
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    t INT;
BEGIN
    FOR t IN SELECT DISTINCT edge_type_id FROM staging_edge_member LOOP
        INSERT INTO substrate.edge_member
            (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id)
        SELECT DISTINCT
            edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id
        FROM staging_edge_member
        WHERE edge_type_id = t
        ON CONFLICT DO NOTHING;
    END LOOP;
END $$;
