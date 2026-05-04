-- substrate.classify(seed_hash, junction_kind, k)
--
-- Top-k labels for an entity from a junction table, ranked by Glicko-2 mu
-- desc, sigma asc (tighter confidence wins ties). Junction kinds:
--   'pos'           → substrate.entity_pos          (Glicko-2 native)
--   'sense'         → substrate.entity_sense        (Glicko-2 native)
--   'pattern_deprel'→ substrate.pattern_deprel      (Glicko-2 native)
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
        SELECT p.id, p.code, ep.mu, ep.sigma, ep.games,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_pos ep
        JOIN substrate.pos p ON p.id = ep.pos_id
        WHERE ep.entity_hash = p_seed_hash
        ORDER BY ep.mu DESC, ep.sigma ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'sense' THEN
        RETURN QUERY
        SELECT s.id, s.code, es.mu, es.sigma, es.games,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_sense es
        JOIN substrate.sense s ON s.id = es.sense_id
        WHERE es.entity_hash = p_seed_hash
        ORDER BY es.mu DESC, es.sigma ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'pattern_deprel' THEN
        RETURN QUERY
        SELECT d.id, d.code, pd.mu, pd.sigma, pd.games,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.pattern_deprel pd
        JOIN substrate.deprel d ON d.id = pd.deprel_id
        WHERE pd.entity_hash = p_seed_hash
        ORDER BY pd.mu DESC, pd.sigma ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'language' THEN
        RETURN QUERY
        SELECT l.id, l.code, NULL::DOUBLE PRECISION, NULL::DOUBLE PRECISION, NULL::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_language el
        JOIN substrate.language l ON l.id = el.language_id
        WHERE el.entity_hash = p_seed_hash
        ORDER BY l.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'morph_feature' THEN
        RETURN QUERY
        SELECT mf.id, mf.code, NULL::DOUBLE PRECISION, NULL::DOUBLE PRECISION, NULL::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_morph_feature emf
        JOIN substrate.morph_feature mf ON mf.id = emf.morph_feature_id
        WHERE emf.entity_hash = p_seed_hash
        ORDER BY mf.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'classification' THEN
        RETURN QUERY
        SELECT et.id, et.code, NULL::DOUBLE PRECISION, NULL::DOUBLE PRECISION, NULL::INT,
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
    'Top-k labels from a junction table for an entity, ranked by Glicko-2 mu (where present). Junction kinds: pos, sense, pattern_deprel (Glicko-2 native); language, morph_feature, classification (no Glicko, alphabetical).';
