-- substrate.populate_unicode_full_case_mapping_edges_from_ext()
--
-- Emits has_full_case_mapping edges for codepoints whose case fold expands
-- to multiple target codepoints (Latin ß → 'ss', Greek final sigma forms,
-- Turkish locale dotted-I, etc.). Mirrors the decomposition-edges pattern:
-- materialise a text_composition entity for the multi-CP fold target with
-- LINESTRINGZM ingestion_trajectory physicality + the typed edge from
-- source codepoint to that composition + per-arena positive_evidence
-- significance.
--
-- Singleton case folds (length 1) are covered by populate_unicode_case_edges
-- via the case_folds_to(codepoint, codepoint) edge type.
--
-- Note (P3 follow-up): this slice handles full_case_fold only.
-- SpecialCasing.txt also defines full_uppercase / full_lowercase /
-- full_titlecase expansions. The substrate.codepoint_atom composite type
-- does not currently expose those arrays; once the embedded UCD blob
-- surfaces full_uc / full_lc / full_tc, this function extends with three
-- more passes over the same pattern.
--
-- Pre-requisite: populate_codepoint_atoms. Idempotent via ON CONFLICT.
CREATE OR REPLACE FUNCTION substrate.populate_unicode_full_case_mapping_edges_from_ext()
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
      FROM substrate.edge_type WHERE code = 'has_full_case_mapping';
    SELECT id INTO v_source_role FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role FROM substrate.edge_role WHERE code = 'target';

    WITH source_folds AS (
        SELECT
            a.cp             AS source_cp,
            a.hash           AS source_hash,
            a.full_case_fold AS targets
          FROM substrate.ucd_codepoints() a
         WHERE a.full_case_fold IS NOT NULL
           AND array_length(a.full_case_fold, 1) >= 2
    ),
    target_hashes AS (
        SELECT
            sf.source_cp,
            sf.source_hash,
            ord.ordinality AS pos,
            cp_atom.hash   AS target_cp_hash
          FROM source_folds sf
          CROSS JOIN LATERAL unnest(sf.targets) WITH ORDINALITY AS ord(target_cp, ordinality)
          CROSS JOIN LATERAL substrate.cp_atom(ord.target_cp::int) AS cp_atom
    ),
    composition_rows AS (
        SELECT
            source_cp,
            source_hash,
            blake3_hash(
                string_agg(target_cp_hash, ''::bytea ORDER BY pos)
            )::substrate.hash_value AS composition_hash,
            ST_SetSRID(
                ST_MakeLine(array_agg(
                    ST_MakePoint(
                        substrate.bb_pack_hash_lo(substrate.bb_hash_lo(target_cp_hash::substrate.hash_value)),
                        substrate.bb_pack_ordinal_rle((pos - 1)::int, 1),
                        substrate.bb_pack_hash_hi(substrate.bb_hash_hi(target_cp_hash::substrate.hash_value)),
                        substrate.bb_pack_metadata(0)
                    ) ORDER BY pos
                )),
                0
            ) AS composition_geom
          FROM target_hashes
         GROUP BY source_cp, source_hash
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
                ARRAY[cr.source_hash, cr.composition_hash]::substrate.hash_value[]
            ) AS edge_hash,
            cr.source_hash,
            cr.composition_hash,
            ST_MakeLine(ARRAY[
                substrate.geometryzm_centroid_point(src_phys.geom),
                substrate.geometryzm_centroid_point(cr.composition_geom)
            ]) AS edge_geom
          FROM composition_rows cr
          JOIN substrate.physicality src_phys
            ON src_phys.physicality_type_id = v_s3_phys
           AND src_phys.entity_hash = cr.source_hash
           AND src_phys.content_hash = cr.source_hash
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
        SELECT es.edge_type_id, es.edge_hash, es.source_hash, v_source_role, 0 FROM edge_specs es
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

COMMENT ON FUNCTION substrate.populate_unicode_full_case_mapping_edges_from_ext() IS
    'Materialise text_composition + ingestion_trajectory physicality + has_full_case_mapping edges + per-arena positive_evidence significance for codepoints whose case fold expands to >= 2 targets. Reads full_case_fold INT[] from substrate.ucd_codepoints. Singleton case folds use case_folds_to via populate_unicode_case_edges. Pre-requisite: populate_codepoint_atoms.';
