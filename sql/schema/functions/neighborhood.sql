-- substrate.neighborhood(p_entity_hash, p_arena_id, p_frechet_threshold) —
-- the hub view of one entity. Each substrate.entity sits at a hub: every
-- typed edge it participates in (outbound, inbound), every composition it
-- belongs to (sequence parents), every entity geometrically near it
-- (Fréchet over physicality trajectories) is part of its neighborhood.
--
-- Different decomposers produce different surface forms — WordNet uses
-- "competitor.n.01", Wiktionary uses "competitor", Tatoeba uses bare
-- "competitor" inside attested sentences. Their content hashes may differ
-- but their geometric trajectories cluster. Fréchet bridges these surface
-- variants so the brain finds neighbors that aren't explicitly edge-linked.
--
-- Returns one row per neighbor with the relation kind: 'outbound_edge',
-- 'inbound_edge', 'sequence_parent', 'sequence_child', 'frechet_neighbor'.
-- The brain uses this as the raw signal layer that intersect / recall
-- ranking operates on.
DROP FUNCTION IF EXISTS substrate.neighborhood(BYTEA, INT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.neighborhood(
    p_entity_hash       BYTEA,
    p_arena_id          INT              DEFAULT NULL,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    relation         TEXT,
    neighbor_hash    BYTEA,
    edge_type_code   TEXT,
    edge_role_code   TEXT,
    edge_mu          DOUBLE PRECISION,
    frechet_distance DOUBLE PRECISION,
    sequence_ordinal INT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- 1. Outbound edges: this entity is in the source role.
    SELECT
        'outbound_edge'::TEXT AS relation,
        em_t.entity_hash      AS neighbor_hash,
        et.code               AS edge_type_code,
        r_t.code              AS edge_role_code,
        COALESCE(es.mu, p.initial_mu * et.semantic_weight * p.derivation_decay) AS edge_mu,
        NULL::DOUBLE PRECISION AS frechet_distance,
        NULL::INT             AS sequence_ordinal
    FROM substrate.edge_member em_s
    JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
    JOIN substrate.edge e ON e.edge_type_id = em_s.edge_type_id AND e.hash = em_s.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.provenance p  ON p.id  = e.provenance_id
    JOIN substrate.edge_member em_t
      ON em_t.edge_type_id = em_s.edge_type_id
     AND em_t.edge_hash    = em_s.edge_hash
     AND em_t.entity_hash <> em_s.entity_hash
    JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id
    LEFT JOIN substrate.edge_significance es
      ON es.context_type_id = COALESCE(p_arena_id, es.context_type_id)
     AND es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND (p_arena_id IS NULL OR es.context_type_id = p_arena_id)
    WHERE em_s.entity_hash = p_entity_hash

    UNION ALL

    -- 2. Inbound edges: this entity is in a target / non-source role.
    SELECT
        'inbound_edge'::TEXT,
        em_other.entity_hash,
        et.code,
        r_self.code,
        COALESCE(es.mu, p.initial_mu * et.semantic_weight * p.derivation_decay),
        NULL::DOUBLE PRECISION,
        NULL::INT
    FROM substrate.edge_member em_self
    JOIN substrate.edge_role r_self ON r_self.id = em_self.edge_role_id
    JOIN substrate.edge e ON e.edge_type_id = em_self.edge_type_id AND e.hash = em_self.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.provenance p  ON p.id  = e.provenance_id
    JOIN substrate.edge_member em_other
      ON em_other.edge_type_id = em_self.edge_type_id
     AND em_other.edge_hash    = em_self.edge_hash
     AND em_other.entity_hash <> em_self.entity_hash
    LEFT JOIN substrate.edge_significance es
      ON es.context_type_id = COALESCE(p_arena_id, es.context_type_id)
     AND es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND (p_arena_id IS NULL OR es.context_type_id = p_arena_id)
    WHERE em_self.entity_hash = p_entity_hash
      AND r_self.code <> 'source'

    UNION ALL

    -- 3. Composition parents: compositions containing this entity.
    SELECT
        'composition_parent'::TEXT,
        s.parent_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.composition_parents(p_entity_hash) s

    UNION ALL

    -- 4. Composition children: entities this composition contains (if any).
    SELECT
        'composition_child'::TEXT,
        s.child_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.get_composition_children(p_entity_hash) s

    UNION ALL

    -- 5. Geometric neighbors: entities whose physicality is 4D-near.
    -- Bridges decomposer surface variants whose content hashes differ but
    -- whose 4D physicality coordinates cluster. Skipped when threshold<=0
    -- — the geometric branch can be a heavy join over physicality and
    -- callers may want to disable it for cheap edge-only lookups.
    SELECT
        'frechet_neighbor'::TEXT,
        p_other.entity_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        substrate.dist_4d(p_self.geom, p_other.geom),
        NULL::INT
    FROM substrate.physicality p_self
    JOIN substrate.physicality p_other
      ON p_other.entity_hash <> p_self.entity_hash
     AND p_other.physicality_type_id = p_self.physicality_type_id
    WHERE p_self.entity_hash = p_entity_hash
      AND p_frechet_threshold > 0
      AND p_self.geom IS NOT NULL
      AND p_other.geom IS NOT NULL
      AND substrate.dist_4d(p_self.geom, p_other.geom) <= p_frechet_threshold;
$$;

COMMENT ON FUNCTION substrate.neighborhood(BYTEA, INT, DOUBLE PRECISION) IS
    'Hub view of one entity: outbound edges, inbound edges, sequence parents, sequence children, geometric (Fréchet) neighbors. Cross-decomposer surface variants bridge here via geometric proximity over substrate.physicality. The raw signal the brain operates on.';
