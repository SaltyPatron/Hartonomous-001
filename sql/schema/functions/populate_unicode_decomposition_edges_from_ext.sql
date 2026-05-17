-- substrate.populate_unicode_decomposition_edges_from_ext()
--
-- Emits Unicode canonical / compatibility decomposition edges from the
-- embedded UCD 17.0.0 catalog.
--
-- For each codepoint with a non-empty decomposition_mapping:
--   1. text_composition entity (Merkle hash = BLAKE3 over ordered child
--      codepoint hashes — matches Hartonomous.Core.Compute.Common.Merkle.Hash32)
--   2. entity_classification under unicode_consortium provenance
--   3. ingestion_trajectory LINESTRINGZM physicality with mantissa-packed
--      child refs. For singleton decompositions (one target codepoint)
--      the vertex is doubled so PostGIS LINESTRINGZM's >=2-vertex minimum
--      is satisfied; readers deduplicate via identical (X, Z) hash prefix.
--   4. typed edge: has_canonical_decomposition for decomp_type=1,
--      has_compatibility_decomposition for decomp_type 2..17, role-ordered
--      (source=codepoint, target=text_composition)
--   5. canonical_composes_to(text_composition → codepoint) for every
--      canonical decomposition — the NFC composition direction.
--      NOTE: this over-emits for codepoints in the Full_Composition_Exclusion
--      list (~80 codepoints out of ~2000). Full correctness requires
--      surfacing Full_Composition_Exclusion in the embedded UCD blob; that
--      filter lands in a follow-up slice.
--   6. per-arena edge_significance rows under positive_evidence
--
-- Pre-requisite: substrate.populate_codepoint_atoms (source codepoint
-- s3_position physicality used to build edge.geom).
--
-- Idempotent via ON CONFLICT. Pure WITH-chain (no TEMP TABLE) — re-entry
-- safe under multi-call transactions.
CREATE OR REPLACE FUNCTION substrate.populate_unicode_decomposition_edges_from_ext()
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
    v_canonical_edge_type    INT;
    v_compat_edge_type       INT;
    v_composes_edge_type     INT;
    v_canonical_semantic     FLOAT8;
    v_compat_semantic        FLOAT8;
    v_composes_semantic      FLOAT8;
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
    SELECT id INTO v_s3_phys
      FROM substrate.physicality_type WHERE code = 'entity';
    v_positive_attest := substrate.resolve_attestation_type_id('positive_evidence');

    SELECT id, semantic_weight INTO v_canonical_edge_type, v_canonical_semantic
      FROM substrate.edge_type WHERE code = 'has_canonical_decomposition';
    SELECT id, semantic_weight INTO v_compat_edge_type, v_compat_semantic
      FROM substrate.edge_type WHERE code = 'has_compatibility_decomposition';
    SELECT id, semantic_weight INTO v_composes_edge_type, v_composes_semantic
      FROM substrate.edge_type WHERE code = 'canonical_composes_to';

    SELECT id INTO v_source_role FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role FROM substrate.edge_role WHERE code = 'target';

    WITH source_decomps AS (
        SELECT
            a.cp                    AS source_cp,
            a.hash                  AS source_hash,
            a.decomp_type           AS decomp_type,
            a.decomposition_mapping AS targets
          FROM substrate.ucd_codepoints() a
         WHERE a.decomp_type > 0
           AND a.decomposition_mapping IS NOT NULL
           AND array_length(a.decomposition_mapping, 1) >= 1
    ),
    target_hashes AS (
        SELECT
            sd.source_cp,
            sd.source_hash,
            sd.decomp_type,
            ord.ordinality          AS pos,
            cp_atom.hash            AS target_cp_hash
          FROM source_decomps sd
          CROSS JOIN LATERAL unnest(sd.targets) WITH ORDINALITY AS ord(target_cp, ordinality)
          CROSS JOIN LATERAL substrate.cp_atom(ord.target_cp::int) AS cp_atom
    ),
    composition_pre AS (
        SELECT
            source_cp,
            source_hash,
            decomp_type,
            count(*)::int AS target_count,
            blake3_hash(
                string_agg(target_cp_hash, ''::bytea ORDER BY pos)
            )::substrate.hash_value AS composition_hash,
            array_agg(
                ST_MakePoint(
                    substrate.bb_pack_hash_lo(substrate.bb_hash_lo(target_cp_hash::substrate.hash_value)),
                    substrate.bb_pack_ordinal_rle((pos - 1)::int, 1),
                    substrate.bb_pack_hash_hi(substrate.bb_hash_hi(target_cp_hash::substrate.hash_value)),
                    substrate.bb_pack_metadata(0)
                ) ORDER BY pos
            ) AS vertex_array
          FROM target_hashes
         GROUP BY source_cp, source_hash, decomp_type
    ),
    composition_rows AS (
        SELECT
            source_cp,
            source_hash,
            decomp_type,
            composition_hash,
            -- PostGIS LINESTRINGZM requires >= 2 vertices. For singleton
            -- decompositions (target_count=1) duplicate the lone vertex —
            -- readers deduplicate via identical (X,Z) hash prefix.
            ST_SetSRID(
                ST_MakeLine(
                    CASE WHEN target_count = 1
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
    -- Forward direction: codepoint → text_composition
    --   decomp_type = 1     → has_canonical_decomposition
    --   decomp_type 2..17   → has_compatibility_decomposition
    forward_edge_specs AS (
        SELECT
            CASE WHEN cr.decomp_type = 1 THEN v_canonical_edge_type
                 ELSE v_compat_edge_type
            END AS edge_type_id,
            CASE WHEN cr.decomp_type = 1 THEN v_canonical_semantic
                 ELSE v_compat_semantic
            END AS semantic_weight,
            substrate.unicode_edge_hash(
                CASE WHEN cr.decomp_type = 1 THEN v_canonical_edge_type
                     ELSE v_compat_edge_type
                END,
                ARRAY[cr.source_hash, cr.composition_hash]::substrate.hash_value[]
            ) AS edge_hash,
            cr.source_hash      AS pos0_hash,
            cr.composition_hash AS pos1_hash,
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
    -- Reverse direction: text_composition → codepoint
    --   decomp_type = 1 only → canonical_composes_to
    -- Compatibility decompositions do NOT round-trip (UAX #15) — no
    -- compat_composes_to edge type exists.
    reverse_edge_specs AS (
        SELECT
            v_composes_edge_type AS edge_type_id,
            v_composes_semantic  AS semantic_weight,
            substrate.unicode_edge_hash(
                v_composes_edge_type,
                ARRAY[cr.composition_hash, cr.source_hash]::substrate.hash_value[]
            ) AS edge_hash,
            cr.composition_hash AS pos0_hash,
            cr.source_hash      AS pos1_hash,
            ST_MakeLine(ARRAY[
                substrate.geometryzm_centroid_point(cr.composition_geom),
                substrate.geometryzm_centroid_point(src_phys.geom)
            ]) AS edge_geom
          FROM composition_rows cr
          JOIN substrate.physicality src_phys
            ON src_phys.physicality_type_id = v_s3_phys
           AND src_phys.entity_hash = cr.source_hash
           AND src_phys.content_hash = cr.source_hash
         WHERE cr.decomp_type = 1
    ),
    all_edge_specs AS (
        SELECT edge_type_id, semantic_weight, edge_hash, pos0_hash, pos1_hash, edge_geom
          FROM forward_edge_specs
        UNION ALL
        SELECT edge_type_id, semantic_weight, edge_hash, pos0_hash, pos1_hash, edge_geom
          FROM reverse_edge_specs
    ),
    insert_edges AS (
        INSERT INTO substrate.edge (edge_type_id, hash, geom, provenance_id)
        SELECT edge_type_id, edge_hash, edge_geom, v_unicode_provenance
          FROM all_edge_specs
        ON CONFLICT DO NOTHING
        RETURNING edge_type_id, hash
    ),
    insert_members AS (
        INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
        SELECT es.edge_type_id, es.edge_hash, es.pos0_hash, v_source_role, 0
          FROM all_edge_specs es
        UNION ALL
        SELECT es.edge_type_id, es.edge_hash, es.pos1_hash, v_target_role, 1
          FROM all_edge_specs es
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
                     v_provenance_mu * es.semantic_weight * v_provenance_decay),
            COALESCE(pea.initial_sigma, v_provenance_sigma),
            0.06, 0
          FROM all_edge_specs es
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

COMMENT ON FUNCTION substrate.populate_unicode_decomposition_edges_from_ext() IS
    'Materialise text_composition entities + ingestion_trajectory LINESTRINGZM physicality + has_canonical_decomposition + has_compatibility_decomposition + canonical_composes_to edges + per-arena positive_evidence significance from the embedded UCD 17.0.0 catalog. Singleton decompositions get doubled-vertex LINESTRINGZM (PostGIS minimum). canonical_composes_to over-emits for Full_Composition_Exclusion codepoints (filter pending separate slice). Pre-req: populate_codepoint_atoms.';
