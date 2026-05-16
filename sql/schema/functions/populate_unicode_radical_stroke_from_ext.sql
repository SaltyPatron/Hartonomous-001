-- substrate.populate_unicode_radical_stroke_from_ext()
--
-- UCD CJKRadicals.txt — Kangxi radical numbering for CJK unified
-- ideographs. Each row: (radical_number_text, radical_form_codepoint,
-- unified_ideograph_codepoint).
--
-- Emits:
--   1. text_composition entity for the radical_number text (ASCII bytes
--      → codepoint hashes; Merkle hash over ordered codepoint hashes)
--   2. ingestion_trajectory LINESTRINGZM physicality (doubled-vertex for
--      single-character radical numbers)
--   3. has_radical_stroke(unified_ideograph_codepoint → number_composition) edge
--      with per-arena positive_evidence significance
--
-- The radical_form_codepoint (in the CJK Radicals block U+2F00..U+2FDF)
-- is not directly emitted as an edge in this slice — a future
-- has_radical_form(unified, radical_block) edge can layer on the same
-- pre-gen data.
--
-- Pre-req: populate_codepoint_atoms.
-- Idempotent via ON CONFLICT.
CREATE OR REPLACE FUNCTION substrate.populate_unicode_radical_stroke_from_ext()
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
    v_s3_phys                INT;
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
      FROM substrate.physicality_type WHERE code = 'ingestion_trajectory';
    SELECT id INTO v_s3_phys
      FROM substrate.physicality_type WHERE code = 's3_position';
    v_positive_attest := substrate.resolve_attestation_type_id('positive_evidence');
    SELECT id, semantic_weight INTO v_edge_type_id, v_edge_semantic_weight
      FROM substrate.edge_type WHERE code = 'has_radical_stroke';
    SELECT id INTO v_source_role FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role FROM substrate.edge_role WHERE code = 'target';

    WITH radical_rows AS (
        SELECT
            row_number() OVER () AS rn,
            radical_number,
            unified_ideograph_codepoint,
            unified.hash AS unified_hash
          FROM substrate.ucd_cjk_radicals()
          CROSS JOIN LATERAL substrate.cp_atom(unified_ideograph_codepoint) AS unified
    ),
    -- Expand the ASCII radical_number text into per-character codepoint
    -- lookups. octet_length is fine here because radical numbers are ASCII.
    number_chars AS (
        SELECT
            rr.rn,
            rr.unified_hash,
            byte_idx + 1 AS pos,
            cp_atom.hash AS child_hash
          FROM radical_rows rr
          CROSS JOIN LATERAL generate_series(0, octet_length(rr.radical_number) - 1) AS byte_idx
          CROSS JOIN LATERAL substrate.cp_atom(get_byte(convert_to(rr.radical_number, 'UTF8'), byte_idx)::int) AS cp_atom
    ),
    composition_pre AS (
        SELECT
            rn,
            unified_hash,
            count(*)::int AS child_count,
            blake3_hash(
                string_agg(child_hash, ''::bytea ORDER BY pos)
            )::substrate.hash_value AS composition_hash,
            array_agg(
                ST_MakePoint(
                    substrate.bb_pack_hash_lo(substrate.bb_hash_lo(child_hash::substrate.hash_value)),
                    substrate.bb_pack_ordinal_rle((pos - 1)::int, 1),
                    substrate.bb_pack_hash_hi(substrate.bb_hash_hi(child_hash::substrate.hash_value)),
                    substrate.bb_pack_metadata(0)
                ) ORDER BY pos
            ) AS vertex_array
          FROM number_chars
         GROUP BY rn, unified_hash
    ),
    composition_rows AS (
        SELECT
            rn,
            unified_hash,
            composition_hash,
            ST_SetSRID(
                ST_MakeLine(
                    CASE WHEN child_count = 1
                         THEN ARRAY[vertex_array[1], vertex_array[1]]
                         ELSE vertex_array
                    END
                ), 0
            ) AS composition_geom
          FROM composition_pre
    ),
    insert_entities AS (
        INSERT INTO substrate.entity (hash)
        SELECT DISTINCT composition_hash FROM composition_rows
        ON CONFLICT (hash) DO NOTHING
        RETURNING hash
    ),
    insert_classes AS (
        INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
        SELECT DISTINCT composition_hash, v_text_composition_etype, v_unicode_provenance
          FROM composition_rows
        ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING
        RETURNING 1
    ),
    insert_phys AS (
        INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
        SELECT DISTINCT ON (composition_hash)
               v_ingest_traj_phys, composition_hash, composition_hash, composition_geom
          FROM composition_rows
        ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING
        RETURNING entity_hash
    ),
    edge_specs AS (
        SELECT
            v_edge_type_id AS edge_type_id,
            substrate.unicode_edge_hash(
                v_edge_type_id,
                ARRAY[cr.unified_hash, cr.composition_hash]::substrate.hash_value[]
            ) AS edge_hash,
            cr.unified_hash,
            cr.composition_hash,
            ST_MakeLine(ARRAY[
                substrate.geometryzm_centroid_point(src_phys.geom),
                substrate.geometryzm_centroid_point(cr.composition_geom)
            ]) AS edge_geom
          FROM composition_rows cr
          JOIN substrate.physicality src_phys
            ON src_phys.physicality_type_id = v_s3_phys
           AND src_phys.entity_hash = cr.unified_hash
           AND src_phys.content_hash = cr.unified_hash
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
        SELECT es.edge_type_id, es.edge_hash, es.unified_hash, v_source_role, 0 FROM edge_specs es
        UNION ALL
        SELECT es.edge_type_id, es.edge_hash, es.composition_hash, v_target_role, 1 FROM edge_specs es
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

COMMENT ON FUNCTION substrate.populate_unicode_radical_stroke_from_ext() IS
    'Materialise text_composition entities for Kangxi radical numbers (ASCII text → codepoint hash sequences) + ingestion_trajectory physicality + has_radical_stroke edges from CJK unified ideographs to their radical number compositions, with per-arena positive_evidence significance. Pre-req: populate_codepoint_atoms.';
