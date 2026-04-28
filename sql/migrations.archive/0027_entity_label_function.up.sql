-- Migration 0028: entity_label function
-- Reconstructs human-readable content labels for text entities by walking
-- the sequence table → codepoint_property to rebuild strings.
-- Now that 0027 enforces unique (parent_id, ordinal_position), this join is clean.

CREATE OR REPLACE FUNCTION substrate.entity_label(p_entity_id bigint)
RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT string_agg(chr(cp.codepoint_value), '' ORDER BY s.ordinal_position)
    FROM substrate.sequence s
    JOIN substrate.codepoint_property cp ON cp.entity_id = s.child_id
    WHERE s.parent_id = p_entity_id;
$$;

COMMENT ON FUNCTION substrate.entity_label IS
    'Reconstruct text label for an entity from its codepoint sequence. Returns NULL if entity has no codepoint children.';

-- Batch version for efficient multi-entity lookups.
CREATE OR REPLACE FUNCTION substrate.entity_labels(p_entity_ids bigint[])
RETURNS TABLE (entity_id bigint, label text)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT s.parent_id, string_agg(chr(cp.codepoint_value), '' ORDER BY s.ordinal_position)
    FROM substrate.sequence s
    JOIN substrate.codepoint_property cp ON cp.entity_id = s.child_id
    WHERE s.parent_id = ANY(p_entity_ids)
    GROUP BY s.parent_id;
$$;

COMMENT ON FUNCTION substrate.entity_labels IS
    'Batch version of entity_label. Returns (entity_id, label) for all entities that have codepoint sequences.';
