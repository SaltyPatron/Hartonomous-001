-- substrate.populate_unicode_named_sequences_from_ext()
--
-- UCD NamedSequences.txt — Consortium-blessed multi-codepoint sequences
-- with canonical names. Each row: (codepoint_sequence int[], name text).
--
-- Materialises:
--   1. text_composition entity for the codepoint sequence (Merkle hash
--      over ordered codepoint hashes; mantissa-packed LINESTRINGZM
--      physicality).
--   2. text_composition entity for the name text (ASCII bytes →
--      codepoint hashes; same shape).
--   3. has_named_sequence(name_composition → codepoint_composition) edge
--      with per-arena positive_evidence significance.
--
-- Pre-req: populate_codepoint_atoms.
-- Idempotent via ON CONFLICT.
CREATE OR REPLACE FUNCTION substrate.populate_unicode_named_sequences_from_ext()
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    v_text_composition_etype INT;
    v_unicode_provenance     INT;
    v_provenance_mu          FLOAT8;
    v_provenance_sigma       FLOAT8;
    v_provenance_decay       FLOAT8;
    v_ingest_traj_phys       INT;
    v_positive_attest        INT;
    v_edge_type_id           INT;
    v_edge_semantic_weight   FLOAT8;
    v_source_role            INT;
    v_target_role            INT;
    v_edges_inserted         BIGINT := 0;
BEGIN
    SELECT id INTO v_text_composition_etype
      FROM substrate.entity_type WHERE code = 'text_composition';
    SELECT id, initial_mu, initial_sigma, derivation_decay
      INTO v_unicode_provenance, v_provenance_mu, v_provenance_sigma, v_provenance_decay
      FROM substrate.provenance WHERE code = 'unicode_consortium';
    SELECT id INTO v_ingest_traj_phys
      FROM substrate.physicality_type WHERE code = 'content';
    v_positive_attest := substrate.resolve_attestation_type_id('positive_evidence');
    SELECT id, semantic_weight INTO v_edge_type_id, v_edge_semantic_weight
      FROM substrate.edge_type WHERE code = 'has_named_sequence';
    SELECT id INTO v_source_role FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role FROM substrate.edge_role WHERE code = 'target';

    WITH src_rows AS (
        SELECT row_number() OVER () AS rn, codepoint_sequence, name
          FROM substrate.ucd_named_sequences()
    ),
    -- codepoint-sequence composition (the actual sequence the entry names)
    cp_child_lookups AS (
        SELECT
            sr.rn,
            ord.ordinality AS pos,
            cp_atom.hash   AS child_hash
          FROM src_rows sr
          CROSS JOIN LATERAL unnest(sr.codepoint_sequence) WITH ORDINALITY AS ord(cp, ordinality)
          CROSS JOIN LATERAL substrate.cp_atom(ord.cp::int) AS cp_atom
    ),
    cp_compositions AS (
        SELECT
            rn,
            count(*)::int AS child_count,
            blake3_hash(string_agg(child_hash, ''::bytea ORDER BY pos))::substrate.hash_value AS composition_hash,
            array_agg(
                ST_MakePoint(
                    substrate.bb_pack_hash_lo(substrate.bb_hash_lo(child_hash::substrate.hash_value)),
                    substrate.bb_pack_ordinal_rle((pos - 1)::int, 1),
                    substrate.bb_pack_hash_hi(substrate.bb_hash_hi(child_hash::substrate.hash_value)),
                    substrate.bb_pack_metadata(0)
                ) ORDER BY pos
            ) AS vertex_array
          FROM cp_child_lookups
         GROUP BY rn
    ),
    cp_built AS (
        SELECT
            rn,
            composition_hash,
            ST_SetSRID(
                ST_MakeLine(
                    CASE WHEN child_count = 1
                         THEN ARRAY[vertex_array[1], vertex_array[1]]
                         ELSE vertex_array
                    END
                ), 0
            ) AS composition_geom
          FROM cp_compositions
    ),
    -- name composition (ASCII bytes → codepoint hashes)
    name_chars AS (
        SELECT
            sr.rn,
            byte_idx + 1 AS pos,
            cp_atom.hash AS child_hash
          FROM src_rows sr
          CROSS JOIN LATERAL generate_series(0, octet_length(sr.name) - 1) AS byte_idx
          CROSS JOIN LATERAL substrate.cp_atom(get_byte(convert_to(sr.name, 'UTF8'), byte_idx)::int) AS cp_atom
    ),
    name_compositions AS (
        SELECT
            rn,
            count(*)::int AS child_count,
            blake3_hash(string_agg(child_hash, ''::bytea ORDER BY pos))::substrate.hash_value AS composition_hash,
            array_agg(
                ST_MakePoint(
                    substrate.bb_pack_hash_lo(substrate.bb_hash_lo(child_hash::substrate.hash_value)),
                    substrate.bb_pack_ordinal_rle((pos - 1)::int, 1),
                    substrate.bb_pack_hash_hi(substrate.bb_hash_hi(child_hash::substrate.hash_value)),
                    substrate.bb_pack_metadata(0)
                ) ORDER BY pos
            ) AS vertex_array
          FROM name_chars
         GROUP BY rn
    ),
    name_built AS (
        SELECT
            rn,
            composition_hash,
            ST_SetSRID(
                ST_MakeLine(
                    CASE WHEN child_count = 1
                         THEN ARRAY[vertex_array[1], vertex_array[1]]
                         ELSE vertex_array
                    END
                ), 0
            ) AS composition_geom
          FROM name_compositions
    ),
    all_compositions AS (
        SELECT composition_hash, composition_geom FROM cp_built
        UNION
        SELECT composition_hash, composition_geom FROM name_built
    ),
    insert_entities AS (
        INSERT INTO substrate.entity (hash)
        SELECT DISTINCT composition_hash FROM all_compositions
        ON CONFLICT (hash) DO NOTHING
        RETURNING hash
    ),
    insert_classes AS (
        INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
        SELECT DISTINCT composition_hash, v_text_composition_etype, v_unicode_provenance
          FROM all_compositions
        ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING
        RETURNING 1
    ),
    insert_phys AS (
        INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
        SELECT DISTINCT ON (composition_hash)
               v_ingest_traj_phys, composition_hash, composition_hash, composition_geom
          FROM all_compositions
        ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING
        RETURNING entity_hash
    ),
    edge_specs AS (
        SELECT
            v_edge_type_id AS edge_type_id,
            substrate.unicode_edge_hash(
                v_edge_type_id,
                ARRAY[n.composition_hash, c.composition_hash]::substrate.hash_value[]
            ) AS edge_hash,
            n.composition_hash AS name_hash,
            c.composition_hash AS cp_hash,
            ST_MakeLine(ARRAY[
                substrate.geometryzm_centroid_point(n.composition_geom),
                substrate.geometryzm_centroid_point(c.composition_geom)
            ]) AS edge_geom
          FROM name_built n
          JOIN cp_built c ON c.rn = n.rn
    ),
    insert_edges AS (
        INSERT INTO substrate.edge (edge_type_id, hash, geom, provenance_id)
        SELECT edge_type_id, edge_hash, edge_geom, v_unicode_provenance
          FROM edge_specs
        ON CONFLICT DO NOTHING
        RETURNING edge_type_id, hash
    ),
    insert_members AS (
        INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
        SELECT es.edge_type_id, es.edge_hash, es.name_hash, v_source_role, 0 FROM edge_specs es
        UNION ALL
        SELECT es.edge_type_id, es.edge_hash, es.cp_hash, v_target_role, 1 FROM edge_specs es
        ON CONFLICT DO NOTHING
        RETURNING 1
    ),
    insert_sig AS (
        INSERT INTO substrate.edge_significance (
            context_type_id, edge_type_id, edge_hash, attestation_type_id,
            mu, sigma, volatility, games
        )
        SELECT
            ctx.id, es.edge_type_id, es.edge_hash, v_positive_attest,
            COALESCE(pea.initial_mu,
                     v_provenance_mu * v_edge_semantic_weight * v_provenance_decay),
            COALESCE(pea.initial_sigma, v_provenance_sigma),
            0.06, 0
          FROM edge_specs es
          CROSS JOIN substrate.significance_context ctx
          LEFT JOIN substrate.provenance_edge_authority pea
            ON pea.provenance_id = v_unicode_provenance
           AND pea.edge_type_id = es.edge_type_id
        ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING
        RETURNING 1
    ),
    counts AS (
        SELECT (SELECT count(*) FROM insert_edges) AS edge_count
    )
    SELECT edge_count INTO v_edges_inserted FROM counts;

    RETURN v_edges_inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_unicode_named_sequences_from_ext() IS
    'Materialise text_composition entities for both the codepoint sequence AND its canonical name (ASCII text) + ingestion_trajectory physicality + has_named_sequence(name → codepoint_sequence) edges with per-arena positive_evidence significance. Pre-req: populate_codepoint_atoms.';
