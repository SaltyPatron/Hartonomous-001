-- Bridge function for OMW (and any cross-lexicon decomposer) to resolve
-- WordNet synsets by their authoring offset string. Returns one row per
-- has_wordnet_offset edge: (offset_doc_hash, synset_hash). Callers compute
-- the offset_doc_hash via BLAKE3 of the canonical offset string ("XXXXXXXX-p")
-- and look up the substrate's content-pure synset hash from the result map.
--
-- Why this exists: synset identity is content-pure (BLAKE3 Merkle of sorted
-- member lemma hashes + gloss byte hash). The WordNet offset is placement
-- metadata recorded as substrate content via has_wordnet_offset edges, NOT
-- baked into the synset's identity hash. This function exposes the bridge
-- in one round-trip so downstream decomposers can resolve synsets by their
-- external authoring identifier without recomputing content hashes.
CREATE OR REPLACE FUNCTION substrate.load_wordnet_offset_synset_map()
RETURNS TABLE(offset_doc_hash BYTEA, synset_hash BYTEA)
LANGUAGE sql
AS $$
    SELECT
        em_target.entity_hash AS offset_doc_hash,
        em_source.entity_hash AS synset_hash
    FROM substrate.edge_member em_source
    JOIN substrate.edge e
        ON  e.edge_type_id = em_source.edge_type_id
        AND e.hash         = em_source.edge_hash
    JOIN substrate.edge_member em_target
        ON  em_target.edge_type_id = em_source.edge_type_id
        AND em_target.edge_hash    = em_source.edge_hash
    JOIN substrate.edge_role rs
        ON rs.id = em_source.edge_role_id AND rs.code = 'source'
    JOIN substrate.edge_role rt
        ON rt.id = em_target.edge_role_id AND rt.code = 'target'
    WHERE e.edge_type_id = (
        SELECT id FROM substrate.edge_type WHERE code = 'has_wordnet_offset'
    );
$$;
