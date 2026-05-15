-- substrate.populate_unicode_confusables_from_ext()
--
-- UTS #39 §4 confusables — pairs of codepoint sequences that look visually
-- identical or near-identical. Source: confusables.txt as parsed into the
-- embedded pre-gen pg_ucd_confusables blob.
--
-- For each row (source_cps[], target_cps[], cls):
--   1. text_composition entity for source_cps (Merkle hash over ordered
--      codepoint hashes), classification + ingestion_trajectory
--      LINESTRINGZM physicality (doubled-vertex for singletons).
--   2. text_composition entity for target_cps with the same shape.
--   3. confusable_with(source_composition, target_composition) edge with
--      per-arena positive_evidence significance under unicode_consortium
--      provenance.
--
-- Idempotent via ON CONFLICT. Pre-req: populate_codepoint_atoms (codepoint
-- entities + S3 physicality must exist for child hash lookup).
CREATE OR REPLACE FUNCTION substrate.populate_unicode_confusables_from_ext()
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
      FROM substrate.physicality_type WHERE code = 'ingestion_trajectory';
    v_positive_attest := substrate.resolve_attestation_type_id('positive_evidence');
    SELECT id, semantic_weight INTO v_edge_type_id, v_edge_semantic_weight
      FROM substrate.edge_type WHERE code = 'confusable_with';
    SELECT id INTO v_source_role FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role FROM substrate.edge_role WHERE code = 'target';

    WITH confusable_rows AS (
        SELECT
            row_number() OVER () AS rn,
            source_codepoints,
            target_codepoints
          FROM substrate.ucd_confusables()
    ),
    -- Expand source-side codepoints + lookup hashes
    src_child_lookups AS (
        SELECT
            cr.rn,
            ord.ordinality AS pos,
            cp_atom.hash   AS child_hash
          FROM confusable_rows cr
          CROSS JOIN LATERAL unnest(cr.source_codepoints) WITH ORDINALITY AS ord(cp, ordinality)
          CROSS JOIN LATERAL substrate.cp_atom(ord.cp::int) AS cp_atom
    ),
    -- Aggregate to per-row source composition
    src_compositions AS (
        SELECT
            rn,
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
          FROM src_child_lookups
         GROUP BY rn
    ),
    -- Same for target side
    tgt_child_lookups AS (
        SELECT
            cr.rn,
            ord.ordinality AS pos,
            cp_atom.hash   AS child_hash
          FROM confusable_rows cr
          CROSS JOIN LATERAL unnest(cr.target_codepoints) WITH ORDINALITY AS ord(cp, ordinality)
          CROSS JOIN LATERAL substrate.cp_atom(ord.cp::int) AS cp_atom
    ),
    tgt_compositions AS (
        SELECT
            rn,
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
          FROM tgt_child_lookups
         GROUP BY rn
    ),
    -- Build composition geom (doubled vertex for singletons)
    src_built AS (
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
          FROM src_compositions
    ),
    tgt_built AS (
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
          FROM tgt_compositions
    ),
    -- Unified set of composition rows for entity / classification / physicality emission
    all_compositions AS (
        SELECT composition_hash, composition_geom FROM src_built
        UNION
        SELECT composition_hash, composition_geom FROM tgt_built
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
    -- Build edge specs joining src and tgt compositions per row
    edge_specs AS (
        SELECT
            v_edge_type_id AS edge_type_id,
            substrate.unicode_edge_hash(
                v_edge_type_id,
                ARRAY[s.composition_hash, t.composition_hash]::substrate.hash_value[]
            ) AS edge_hash,
            s.composition_hash AS source_hash,
            t.composition_hash AS target_hash,
            ST_MakeLine4D(ARRAY[
                substrate.geometry4d_centroid(s.composition_geom),
                substrate.geometry4d_centroid(t.composition_geom)
            ]) AS edge_geom
          FROM src_built s
          JOIN tgt_built t ON t.rn = s.rn
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
        SELECT es.edge_type_id, es.edge_hash, es.target_hash, v_target_role, 1 FROM edge_specs es
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

COMMENT ON FUNCTION substrate.populate_unicode_confusables_from_ext() IS
    'Materialise text_composition entities for both sides of each UTS #39 confusable pair + ingestion_trajectory LINESTRINGZM physicality + confusable_with edges + per-arena positive_evidence significance. Doubled-vertex LINESTRINGZM for singleton codepoint sequences. Pre-req: populate_codepoint_atoms.';
