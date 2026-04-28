-- substrate.resolve_entity_handles(p_hashes BYTEA[], p_type_codes TEXT[])
--
-- Bulk hash → composite handle resolution. For every (hash, type_code) pair
-- candidates implied by the input arrays, returns the rows that exist in
-- substrate.entity. Same shape as the previous ResolveEntityIdsAsync but
-- composite-key throughout: returns (entity_type_id, entity_type_code, hash)
-- rather than a surrogate id.
--
-- p_type_codes scopes the search — a 32-byte hash by itself is ambiguous
-- because the same content bytes could (in principle) land in any partition.
-- Callers pass the entity type codes valid for their content (e.g. ["lemma",
-- "word_form", "synset"] when seeding inference from a prompt).
CREATE OR REPLACE FUNCTION substrate.resolve_entity_handles(
    p_hashes     BYTEA[],
    p_type_codes TEXT[]
)
RETURNS TABLE (
    entity_type_id   INT,
    entity_type_code VARCHAR,
    entity_hash      BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT e.entity_type_id, et.code, e.hash
    FROM substrate.entity e
    JOIN substrate.entity_type et ON et.id = e.entity_type_id
    WHERE e.hash = ANY(p_hashes)
      AND et.code = ANY(p_type_codes);
$$;

COMMENT ON FUNCTION substrate.resolve_entity_handles(BYTEA[], TEXT[]) IS
    'Bulk content-hash → composite-handle resolution. Returns existing (entity_type_id, entity_type_code, hash) rows for every (hash, type) candidate.';
