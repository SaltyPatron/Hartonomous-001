-- substrate.classify(seed_hash, junction_kind, k)
--
-- Top-k labels for an entity from a junction table, ranked by Glicko-2 mu
-- desc, sigma asc (tighter confidence wins ties). Junction kinds:
--   'pos'           → substrate.entity_pos          (Glicko-2 native, stratified)
--   'sense'         → has_sense substrate edges     (Glicko-2 edge significance)
--   'pattern_deprel'→ substrate.pattern_deprel      (Glicko-2 native, stratified)
--   'language'      → substrate.entity_language     (no Glicko, single per-entity assertion)
--   'morph_feature' → substrate.entity_morph_feature(no Glicko, per-feature assertion)
--   'classification'→ substrate.entity_classification(entity_type provenance trail)
--
-- This is reference-table-resolution, not edge traversal. The substrate's
-- "what kind of thing is this entity" surface is junction-indexed and
-- microsecond-fast. Edge-graph traversal lives in substrate.infer / .recall.
DROP FUNCTION IF EXISTS substrate.classify(BYTEA, TEXT, INT);
CREATE OR REPLACE FUNCTION substrate.classify(
    p_seed_hash      BYTEA,
    p_junction_kind  TEXT,
    p_k              INT DEFAULT 10
) RETURNS TABLE (
    label_id    INT,
    label_code  TEXT,
    mu          DOUBLE PRECISION,
    sigma       DOUBLE PRECISION,
    games       INT,
    elapsed_ms  INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started TIMESTAMP := clock_timestamp();
BEGIN
    IF p_junction_kind = 'pos' THEN
        RETURN QUERY
         SELECT p.id,
             p.code,
             AVG(ep.mu)::DOUBLE PRECISION,
             AVG(ep.sigma)::DOUBLE PRECISION,
             COALESCE(SUM(ep.games), 0)::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_pos ep
        JOIN substrate.pos p ON p.id = ep.pos_id
        WHERE ep.entity_hash = p_seed_hash
         GROUP BY p.id, p.code
         ORDER BY AVG(ep.mu) DESC, AVG(ep.sigma) ASC, p.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'sense' THEN
        RETURN QUERY
         WITH constants AS (
             SELECT et.id AS edge_type_id,
                 er_source.id AS source_role_id,
                 er_target.id AS target_role_id,
                 sc.id AS context_type_id
            FROM substrate.edge_type et
            JOIN substrate.edge_role er_source ON er_source.code = 'source'
            JOIN substrate.edge_role er_target ON er_target.code = 'target'
            JOIN substrate.significance_context sc ON sc.code = 'lexical_disambiguation'
              WHERE et.code = 'has_sense'
         ), ranked AS (
             SELECT encode(target_member.entity_hash, 'hex') AS label_code,
                 COALESCE(AVG(es.mu), 1500.0)::DOUBLE PRECISION AS mu,
                 COALESCE(AVG(es.sigma), 350.0)::DOUBLE PRECISION AS sigma,
                 COALESCE(SUM(es.games), 0)::INT AS games
            FROM constants c
            JOIN substrate.edge e
              ON e.edge_type_id = c.edge_type_id
            JOIN substrate.edge_member source_member
              ON source_member.edge_type_id = e.edge_type_id
             AND source_member.edge_hash = e.hash
             AND source_member.edge_role_id = c.source_role_id
             AND source_member.entity_hash = p_seed_hash
            JOIN substrate.edge_member target_member
              ON target_member.edge_type_id = e.edge_type_id
             AND target_member.edge_hash = e.hash
             AND target_member.edge_role_id = c.target_role_id
            LEFT JOIN substrate.edge_significance es
              ON es.context_type_id = c.context_type_id
             AND es.edge_type_id = e.edge_type_id
             AND es.edge_hash = e.hash
              GROUP BY target_member.entity_hash
         )
         SELECT row_number() OVER (ORDER BY ranked.mu DESC, ranked.sigma ASC, ranked.label_code ASC)::INT AS label_id,
             ranked.label_code,
             ranked.mu,
             ranked.sigma,
             ranked.games,
             EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
           FROM ranked
          ORDER BY ranked.mu DESC, ranked.sigma ASC, ranked.label_code ASC
          LIMIT p_k;

    ELSIF p_junction_kind = 'pattern_deprel' THEN
        RETURN QUERY
         SELECT d.id,
             d.code,
             AVG(pd.mu)::DOUBLE PRECISION,
             AVG(pd.sigma)::DOUBLE PRECISION,
             COALESCE(SUM(pd.games), 0)::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.pattern_deprel pd
        JOIN substrate.deprel d ON d.id = pd.deprel_id
        WHERE pd.entity_hash = p_seed_hash
         GROUP BY d.id, d.code
         ORDER BY AVG(pd.mu) DESC, AVG(pd.sigma) ASC, d.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'language' THEN
        RETURN QUERY
         SELECT l.id, l.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_language el
        JOIN substrate.language l ON l.id = el.language_id
        WHERE el.entity_hash = p_seed_hash
        ORDER BY l.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'morph_feature' THEN
        RETURN QUERY
        SELECT mf.id, mf.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_morph_feature emf
        JOIN substrate.morph_feature mf ON mf.id = emf.morph_feature_id
        WHERE emf.entity_hash = p_seed_hash
        ORDER BY mf.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'classification' THEN
        RETURN QUERY
        SELECT et.id, et.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_classification ec
        JOIN substrate.entity_type et ON et.id = ec.entity_type_id
        WHERE ec.entity_hash = p_seed_hash
        ORDER BY et.code ASC
        LIMIT p_k;

    ELSE
        RAISE EXCEPTION 'unknown junction_kind: %, expected pos|sense|pattern_deprel|language|morph_feature|classification', p_junction_kind
            USING ERRCODE = 'invalid_parameter_value';
    END IF;
END $$;

COMMENT ON FUNCTION substrate.classify(BYTEA, TEXT, INT) IS
    'Top-k labels for an entity. pos/pattern_deprel aggregate stratified junction Glicko rows; sense ranks has_sense edges in lexical_disambiguation and returns synset hashes as labels; language, morph_feature, classification return default rating values for a stable non-null result shape.';
