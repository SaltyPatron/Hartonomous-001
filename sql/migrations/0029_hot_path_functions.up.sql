-- 0029_hot_path_functions.up.sql
-- Server-side functions replacing inline SQL in API endpoints and engine hot paths.
-- Every read that inference, traversal, or the API touches goes through here.

-- ═══════════════════════════════════════════════════════════════════
-- 1. Entity reads
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.get_entity_info(p_entity_id bigint)
RETURNS TABLE (
    entity_id       bigint,
    entity_type_id  integer,
    entity_type_code varchar,
    hash            bytea
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT e.id, e.entity_type_id, et.code, e.hash
    FROM substrate.entity e
    JOIN substrate.entity_type et ON e.entity_type_id = et.id
    WHERE e.id = p_entity_id;
$$;

CREATE OR REPLACE FUNCTION substrate.get_entity_by_hash(p_hash bytea)
RETURNS TABLE (
    entity_id       bigint,
    entity_type_id  integer,
    entity_type_code varchar,
    hash            bytea
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT e.id, e.entity_type_id, et.code, e.hash
    FROM substrate.entity e
    JOIN substrate.entity_type et ON e.entity_type_id = et.id
    WHERE e.hash = p_hash;
$$;

CREATE OR REPLACE FUNCTION substrate.list_entities(
    p_type_id   integer,
    p_cursor    bigint DEFAULT 0,
    p_limit     integer DEFAULT 100
)
RETURNS TABLE (
    entity_id       bigint,
    entity_type_id  integer,
    entity_type_code varchar,
    hash            bytea
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT e.id, e.entity_type_id, et.code, e.hash
    FROM substrate.entity e
    JOIN substrate.entity_type et ON e.entity_type_id = et.id
    WHERE e.entity_type_id = p_type_id
      AND e.id > p_cursor
    ORDER BY e.id
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 2. Classification reads — ONE function, ONE round-trip, JSONB out
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.get_entity_classifications(p_entity_id bigint)
RETURNS jsonb
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT jsonb_build_object(
        'pos', COALESCE((
            SELECT jsonb_agg(jsonb_build_object(
                'code', p.code, 'mu', ep.mu, 'sigma', ep.sigma
            ) ORDER BY ep.mu DESC NULLS LAST)
            FROM substrate.entity_pos ep
            JOIN substrate.pos p ON p.id = ep.pos_id
            WHERE ep.entity_id = p_entity_id
        ), '[]'::jsonb),
        'languages', COALESCE((
            SELECT jsonb_agg(l.code ORDER BY l.code)
            FROM substrate.entity_language el
            JOIN substrate.language l ON l.id = el.language_id
            WHERE el.entity_id = p_entity_id
        ), '[]'::jsonb),
        'senses', COALESCE((
            SELECT jsonb_agg(jsonb_build_object(
                'code', s.code, 'mu', es.mu, 'sigma', es.sigma
            ) ORDER BY es.mu DESC NULLS LAST)
            FROM substrate.entity_sense es
            JOIN substrate.sense s ON s.id = es.sense_id
            WHERE es.entity_id = p_entity_id
        ), '[]'::jsonb),
        'morphFeatures', COALESCE((
            SELECT jsonb_agg(mf.key || '=' || mf.value ORDER BY mf.key)
            FROM substrate.entity_morph_feature emf
            JOIN substrate.morph_feature mf ON mf.id = emf.morph_feature_id
            WHERE emf.entity_id = p_entity_id
        ), '[]'::jsonb)
    );
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 3. Edge reads
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.get_edge_info(p_edge_id bigint)
RETURNS TABLE (
    edge_id         bigint,
    edge_type_id    integer,
    edge_type_code  varchar,
    hash            bytea,
    provenance_code varchar,
    members         jsonb
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        e.id,
        e.edge_type_id,
        et.code,
        e.hash,
        p.code,
        COALESCE((
            SELECT jsonb_agg(jsonb_build_object(
                'entityId', em.entity_id,
                'role', er.code
            ) ORDER BY er.id)
            FROM substrate.edge_member em
            JOIN substrate.edge_role er ON em.edge_role_id = er.id
            WHERE em.edge_id = e.id
        ), '[]'::jsonb)
    FROM substrate.edge e
    JOIN substrate.edge_type et ON e.edge_type_id = et.id
    JOIN substrate.provenance p ON e.provenance_id = p.id
    WHERE e.id = p_edge_id;
$$;

CREATE OR REPLACE FUNCTION substrate.get_entity_edge_ids(
    p_entity_id     bigint,
    p_direction     text DEFAULT 'both',
    p_edge_type_id  integer DEFAULT NULL,
    p_cursor        bigint DEFAULT 0,
    p_limit         integer DEFAULT 100
)
RETURNS TABLE (
    edge_id         bigint,
    edge_type_code  varchar
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT DISTINCT e.id, et.code
    FROM substrate.edge_member em
    JOIN substrate.edge e ON em.edge_id = e.id
    JOIN substrate.edge_type et ON e.edge_type_id = et.id
    JOIN substrate.edge_role er ON em.edge_role_id = er.id
    WHERE em.entity_id = p_entity_id
      AND e.id > p_cursor
      AND (p_direction = 'both'
           OR (p_direction = 'outbound' AND er.code = 'source')
           OR (p_direction = 'inbound' AND er.code = 'target'))
      AND (p_edge_type_id IS NULL OR e.edge_type_id = p_edge_type_id)
    ORDER BY e.id
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 4. Significance reads
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.resolve_context_id(p_code text)
RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT id FROM substrate.significance_context WHERE code = p_code;
$$;

CREATE OR REPLACE FUNCTION substrate.get_entity_significance(
    p_entity_id bigint,
    p_arena     text DEFAULT NULL
)
RETURNS TABLE (
    significance_id bigint,
    arena_code      varchar,
    mu              float8,
    sigma           float8,
    volatility      float8,
    games           integer
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT s.id, sc.code, s.mu, s.sigma, s.volatility, s.games
    FROM substrate.significance s
    JOIN substrate.significance_context sc ON s.context_type_id = sc.id
    WHERE s.entity_id = p_entity_id
      AND (p_arena IS NULL OR sc.code = p_arena)
    ORDER BY s.mu DESC;
$$;

CREATE OR REPLACE FUNCTION substrate.get_significant_neighbors(
    p_entity_id bigint,
    p_arena     text,
    p_limit     integer DEFAULT 20
)
RETURNS TABLE (
    neighbor_entity_id bigint,
    entity_type_code   varchar,
    mu                 float8,
    sigma              float8
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT DISTINCT em2.entity_id, et.code, s.mu, s.sigma
    FROM substrate.edge_member em1
    JOIN substrate.edge_member em2
        ON em1.edge_id = em2.edge_id AND em1.entity_id <> em2.entity_id
    JOIN substrate.entity ent ON em2.entity_id = ent.id
    JOIN substrate.entity_type et ON ent.entity_type_id = et.id
    LEFT JOIN substrate.significance s ON s.entity_id = em2.entity_id
    LEFT JOIN substrate.significance_context sc
        ON s.context_type_id = sc.id AND sc.code = p_arena
    WHERE em1.entity_id = p_entity_id
    ORDER BY s.mu DESC NULLS LAST
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 5. Traversal enrichment (batch lookups)
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.enrich_edges(p_edge_ids bigint[])
RETURNS TABLE (
    edge_id         bigint,
    edge_type_code  varchar
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT e.id, et.code
    FROM substrate.edge e
    JOIN substrate.edge_type et ON e.edge_type_id = et.id
    WHERE e.id = ANY(p_edge_ids);
$$;

CREATE OR REPLACE FUNCTION substrate.enrich_significance(
    p_entity_ids     bigint[],
    p_context_type_id integer
)
RETURNS TABLE (
    entity_id bigint,
    mu        float8
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT s.entity_id, s.mu
    FROM substrate.significance s
    WHERE s.entity_id = ANY(p_entity_ids)
      AND s.context_type_id = p_context_type_id;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 6. Sequence children (recomposer + tree walk)
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.get_entity_children(p_parent_id bigint)
RETURNS TABLE (
    child_id         bigint,
    ordinal_position integer,
    rle_count        integer
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT s.child_id, s.ordinal_position, s.rle_count
    FROM substrate.sequence s
    WHERE s.parent_id = p_parent_id
    ORDER BY s.ordinal_position;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 7. Significance prune
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.prune_significance(
    p_context_type_id integer,
    p_mu_threshold    float8
)
RETURNS integer
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE
AS $$
DECLARE
    v_deleted integer;
BEGIN
    DELETE FROM substrate.significance
    WHERE context_type_id = p_context_type_id
      AND mu < p_mu_threshold;
    GET DIAGNOSTICS v_deleted = ROW_COUNT;
    RETURN v_deleted;
END;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 8. Health / monitoring summary — ONE call replaces 6 inline queries
-- ═══════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS jsonb
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT jsonb_build_object(
        'totalEntities', (SELECT count(*) FROM substrate.entity),
        'totalEdges', (SELECT count(*) FROM substrate.edge),
        'entitiesByType', COALESCE((
            SELECT jsonb_object_agg(et.code, cnt ORDER BY cnt DESC)
            FROM (
                SELECT entity_type_id, count(*) AS cnt
                FROM substrate.entity
                GROUP BY entity_type_id
                ORDER BY cnt DESC
                LIMIT 20
            ) sub
            JOIN substrate.entity_type et ON et.id = sub.entity_type_id
        ), '{}'::jsonb),
        'meanMuByArena', COALESCE((
            SELECT jsonb_object_agg(sc.code, avg_mu)
            FROM (
                SELECT context_type_id, avg(mu) AS avg_mu
                FROM substrate.significance
                GROUP BY context_type_id
            ) sub
            JOIN substrate.significance_context sc ON sc.id = sub.context_type_id
        ), '{}'::jsonb),
        'storageSizeBytes', pg_database_size(current_database())
    );
$$;

CREATE OR REPLACE FUNCTION substrate.ingestion_summary()
RETURNS TABLE (
    decomposer_code     varchar,
    entities_created    bigint,
    edges_created       bigint,
    entities_per_second float8,
    is_stuck            boolean,
    last_report         timestamptz
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        decomposer_code,
        COALESCE(SUM(entities_ingested), 0),
        COALESCE(SUM(edges_created), 0),
        SUM(entities_ingested) / GREATEST(
            EXTRACT(EPOCH FROM (MAX(completed_at) - MIN(started_at))), 1
        ),
        bool_or(status = 'running' AND started_at < now() - interval '5 minutes'),
        MAX(completed_at)
    FROM monitor.ingestion_progress
    GROUP BY decomposer_code;
$$;
