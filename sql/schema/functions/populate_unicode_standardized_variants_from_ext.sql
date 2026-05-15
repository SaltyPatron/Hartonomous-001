-- substrate.populate_unicode_standardized_variants_from_ext()
--
-- UCD StandardizedVariants.txt + emoji-variation-sequences.txt.
-- Each row: (base_codepoint, variation_selector_codepoint, description, scope).
--
-- Materialises:
--   1. text_composition entity for the 2-element [base, vs] codepoint
--      sequence (Merkle hash over ordered codepoint hashes)
--   2. ingestion_trajectory LINESTRINGZM physicality (mantissa-packed)
--   3. entity_classification under unicode_consortium
--   4. has_standardized_variant(base_codepoint → variant_composition) edge
--      with per-arena positive_evidence significance
--
-- Pre-req: populate_codepoint_atoms.
-- Idempotent via ON CONFLICT.
CREATE OR REPLACE FUNCTION substrate.populate_unicode_standardized_variants_from_ext()
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
      FROM substrate.edge_type WHERE code = 'has_standardized_variant';
    SELECT id INTO v_source_role FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role FROM substrate.edge_role WHERE code = 'target';

    WITH variant_rows AS (
        SELECT
            base_codepoint,
            variation_selector_codepoint,
            base.hash AS base_hash,
            vs.hash   AS vs_hash
          FROM substrate.ucd_standardized_variants()
          CROSS JOIN LATERAL substrate.cp_atom(base_codepoint) AS base
          CROSS JOIN LATERAL substrate.cp_atom(variation_selector_codepoint) AS vs
    ),
    composition_rows AS (
        SELECT
            base_codepoint,
            base_hash,
            blake3_hash(base_hash::bytea || vs_hash::bytea)::substrate.hash_value AS composition_hash,
            ST_SetSRID(
                ST_MakeLine(ARRAY[
                    ST_MakePoint(
                        substrate.bb_pack_hash_lo(substrate.bb_hash_lo(base_hash::substrate.hash_value)),
                        substrate.bb_pack_ordinal_rle(0, 1),
                        substrate.bb_pack_hash_hi(substrate.bb_hash_hi(base_hash::substrate.hash_value)),
                        substrate.bb_pack_metadata(0)
                    ),
                    ST_MakePoint(
                        substrate.bb_pack_hash_lo(substrate.bb_hash_lo(vs_hash::substrate.hash_value)),
                        substrate.bb_pack_ordinal_rle(1, 1),
                        substrate.bb_pack_hash_hi(substrate.bb_hash_hi(vs_hash::substrate.hash_value)),
                        substrate.bb_pack_metadata(0)
                    )
                ]), 0
            ) AS composition_geom
          FROM variant_rows
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
                ARRAY[cr.base_hash, cr.composition_hash]::substrate.hash_value[]
            ) AS edge_hash,
            cr.base_hash,
            cr.composition_hash,
            ST_MakeLine4D(ARRAY[
                substrate.geometry4d_centroid(src_phys.geom),
                substrate.geometry4d_centroid(cr.composition_geom)
            ]) AS edge_geom
          FROM composition_rows cr
          JOIN substrate.physicality src_phys
            ON src_phys.physicality_type_id = v_s3_phys
           AND src_phys.entity_hash = cr.base_hash
           AND src_phys.content_hash = cr.base_hash
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
        SELECT es.edge_type_id, es.edge_hash, es.base_hash, v_source_role, 0 FROM edge_specs es
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

COMMENT ON FUNCTION substrate.populate_unicode_standardized_variants_from_ext() IS
    'Materialise 2-element [base, variation_selector] text_composition entities + ingestion_trajectory physicality + has_standardized_variant edges + per-arena positive_evidence significance. Pre-req: populate_codepoint_atoms.';
