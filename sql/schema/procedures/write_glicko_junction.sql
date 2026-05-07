CREATE OR REPLACE PROCEDURE substrate.write_glicko_junction(
    p_table_name TEXT,
    p_ref_column TEXT,
    p_entity_hashes BYTEA[],
    p_ref_ids INT[],
    p_mus DOUBLE PRECISION[],
    p_sigmas DOUBLE PRECISION[]
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT := lower(CASE WHEN left(p_table_name, 10) = 'substrate.' THEN substring(p_table_name FROM 11) ELSE p_table_name END);
    v_ref_column TEXT := lower(p_ref_column);
BEGIN
    IF p_entity_hashes IS NULL OR p_ref_ids IS NULL OR p_mus IS NULL OR p_sigmas IS NULL THEN
        RAISE EXCEPTION 'Junction arrays cannot be null';
    END IF;

    IF cardinality(p_entity_hashes) <> cardinality(p_ref_ids)
        OR cardinality(p_entity_hashes) <> cardinality(p_mus)
        OR cardinality(p_entity_hashes) <> cardinality(p_sigmas) THEN
        RAISE EXCEPTION 'Junction array lengths must match: hashes %, refs %, mus %, sigmas %',
            cardinality(p_entity_hashes), cardinality(p_ref_ids), cardinality(p_mus), cardinality(p_sigmas);
    END IF;

    IF v_table_name = 'entity_pos' AND v_ref_column = 'pos_id' THEN
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, mu, sigma)
        SELECT src.entity_hash, src.ref_id, src.mu, src.sigma
          FROM unnest(p_entity_hashes, p_ref_ids, p_mus, p_sigmas) AS src(entity_hash, ref_id, mu, sigma)
        ON CONFLICT (entity_hash, pos_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'pattern_deprel' AND v_ref_column = 'deprel_id' THEN
        INSERT INTO substrate.pattern_deprel (entity_hash, deprel_id, mu, sigma)
        SELECT src.entity_hash, src.ref_id, src.mu, src.sigma
          FROM unnest(p_entity_hashes, p_ref_ids, p_mus, p_sigmas) AS src(entity_hash, ref_id, mu, sigma)
        ON CONFLICT (entity_hash, deprel_id) DO NOTHING;
        RETURN;
    END IF;

    RAISE EXCEPTION 'Unsupported Glicko junction target %.%', v_table_name, v_ref_column;
END $$;

COMMENT ON PROCEDURE substrate.write_glicko_junction(TEXT, TEXT, BYTEA[], INT[], DOUBLE PRECISION[], DOUBLE PRECISION[]) IS
    'Bulk insert allowlisted Glicko-bearing junction rows. Routing is SQL-side and explicit.';
