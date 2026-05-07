CREATE OR REPLACE FUNCTION substrate.prompt_document_ready(p_hash BYTEA)
RETURNS TABLE (entity_count BIGINT, sequence_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*) FROM substrate.entity e WHERE e.hash = p_hash)::BIGINT AS entity_count,
        (SELECT count(*) FROM substrate.sequence s WHERE s.parent_hash = p_hash)::BIGINT AS sequence_count;
$f$;

COMMENT ON FUNCTION substrate.prompt_document_ready(BYTEA) IS
    'Return prompt document drain-barrier counts for entity and sequence rows.';