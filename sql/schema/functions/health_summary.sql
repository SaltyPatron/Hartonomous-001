-- substrate.health_summary() — single-call substrate state probe.
--
-- Returns a JSONB object with entity counts by type, edge counts by type,
-- physicality count, entity-significance mean mu by arena, and storage size.
-- Written against the hash-as-PK schema: queries (entity_type_id, hash)
-- composite keys, NOT a surrogate id column.
--
-- One round-trip replaces ~6 inline counts so the CLI / API health probe
-- doesn't pay 6× connection latency. Returns plain JSONB so the C# side
-- doesn't have to know the column shape.
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS jsonb
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT jsonb_build_object(
        'totalEntities',       (SELECT count(*) FROM substrate.entity),
        'totalEdges',          (SELECT count(*) FROM substrate.edge),
        'totalEdgeMembers',    (SELECT count(*) FROM substrate.edge_member),
        'totalPhysicalities',  (SELECT count(*) FROM substrate.physicality),
        'totalEntitySig',      (SELECT count(*) FROM substrate.entity_significance),
        'totalEdgeSig',        (SELECT count(*) FROM substrate.edge_significance),
        'entitiesByType', COALESCE((
            SELECT jsonb_object_agg(et.code, sub.cnt)
            FROM (
                SELECT entity_type_id, count(*) AS cnt
                FROM substrate.entity
                GROUP BY entity_type_id
            ) sub
            JOIN substrate.entity_type et ON et.id = sub.entity_type_id
        ), '{}'::jsonb),
        'edgesByType', COALESCE((
            SELECT jsonb_object_agg(et.code, sub.cnt)
            FROM (
                SELECT edge_type_id, count(*) AS cnt
                FROM substrate.edge
                GROUP BY edge_type_id
            ) sub
            JOIN substrate.edge_type et ON et.id = sub.edge_type_id
        ), '{}'::jsonb),
        'entityMeanMuByArena', COALESCE((
            SELECT jsonb_object_agg(sc.code, sub.avg_mu)
            FROM (
                SELECT context_type_id, avg(mu) AS avg_mu
                FROM substrate.entity_significance
                GROUP BY context_type_id
            ) sub
            JOIN substrate.significance_context sc ON sc.id = sub.context_type_id
        ), '{}'::jsonb),
        'edgeMeanMuByArena', COALESCE((
            SELECT jsonb_object_agg(sc.code, sub.avg_mu)
            FROM (
                SELECT context_type_id, avg(mu) AS avg_mu
                FROM substrate.edge_significance
                GROUP BY context_type_id
            ) sub
            JOIN substrate.significance_context sc ON sc.id = sub.context_type_id
        ), '{}'::jsonb),
        'storageSizeBytes',    pg_database_size(current_database())
    );
$$;

COMMENT ON FUNCTION substrate.health_summary() IS
    'Single-call substrate state probe. Hash-as-PK aware: counts by type code, not surrogate id. JSONB shape stable for CLI / API consumption.';
