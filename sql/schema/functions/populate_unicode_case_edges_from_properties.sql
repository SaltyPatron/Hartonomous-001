CREATE OR REPLACE FUNCTION substrate.populate_unicode_case_edges_from_properties()
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    inserted_count BIGINT;
BEGIN
    WITH edge_specs(edge_code, source_hash, target_hash) AS (
        SELECT 'maps_to_lowercase', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_lowercase
        WHERE source.simple_lowercase IS NOT NULL
          AND source.simple_lowercase <> source.codepoint_value

        UNION ALL

        SELECT 'maps_to_uppercase', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_uppercase
        WHERE source.simple_uppercase IS NOT NULL
          AND source.simple_uppercase <> source.codepoint_value

        UNION ALL

        SELECT 'maps_to_titlecase', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_titlecase
        WHERE source.simple_titlecase IS NOT NULL
          AND source.simple_titlecase <> source.codepoint_value

        UNION ALL

        SELECT 'case_folds_to', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_case_fold
        WHERE source.simple_case_fold IS NOT NULL
          AND source.simple_case_fold <> source.codepoint_value
    ),
    edge_rows AS (
        SELECT
            et.id AS edge_type_id,
            substrate.unicode_edge_hash(et.id, ARRAY[edge_specs.source_hash, edge_specs.target_hash]::substrate.hash_value[]) AS edge_hash,
            edge_specs.source_hash,
            edge_specs.target_hash,
            provenance.id AS provenance_id,
            provenance.initial_mu AS provenance_initial_mu,
            provenance.initial_sigma AS provenance_initial_sigma,
            provenance.derivation_decay,
            et.semantic_weight,
            ST_MakeLine4D(ARRAY[
                substrate.geometry4d_centroid(source_physicality.geom),
                substrate.geometry4d_centroid(target_physicality.geom)
            ]) AS geom
        FROM edge_specs
        JOIN substrate.edge_type et ON et.code = edge_specs.edge_code
        JOIN substrate.provenance provenance ON provenance.code = 'unicode_consortium'
        JOIN substrate.physicality_type s3_type ON s3_type.code = 's3_position'
        JOIN substrate.physicality source_physicality
          ON source_physicality.physicality_type_id = s3_type.id
         AND source_physicality.entity_hash = edge_specs.source_hash
         AND source_physicality.content_hash = edge_specs.source_hash
        JOIN substrate.physicality target_physicality
          ON target_physicality.physicality_type_id = s3_type.id
         AND target_physicality.entity_hash = edge_specs.target_hash
         AND target_physicality.content_hash = edge_specs.target_hash
    ),
    inserted_edges AS (
        INSERT INTO substrate.edge (edge_type_id, hash, geom, provenance_id)
        SELECT edge_type_id, edge_hash, geom, provenance_id
        FROM edge_rows
        ON CONFLICT DO NOTHING
        RETURNING edge_type_id, hash
    ),
    all_edges AS (
        SELECT edge_type_id, edge_hash, source_hash, target_hash
        FROM edge_rows
        CROSS JOIN (SELECT count(*) AS inserted_edge_count FROM inserted_edges) edge_insert_barrier
    ),
    inserted_significance AS (
        INSERT INTO substrate.edge_significance (
            context_type_id,
            edge_type_id,
            edge_hash,
            attestation_type_id,
            mu,
            sigma,
            volatility,
            games
        )
        SELECT
            context.id,
            edge_rows.edge_type_id,
            edge_rows.edge_hash,
            attestation.id,
            COALESCE(
                provenance_edge_authority.initial_mu,
                edge_rows.provenance_initial_mu * edge_rows.semantic_weight * edge_rows.derivation_decay
            ),
            COALESCE(provenance_edge_authority.initial_sigma, edge_rows.provenance_initial_sigma),
            0.06,
            0
        FROM edge_rows
        CROSS JOIN substrate.significance_context context
        CROSS JOIN substrate.attestation_type attestation
        LEFT JOIN substrate.provenance_edge_authority
          ON provenance_edge_authority.provenance_id = edge_rows.provenance_id
         AND provenance_edge_authority.edge_type_id = edge_rows.edge_type_id
        WHERE attestation.code = 'positive_evidence'
        ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING
        RETURNING 1
    ),
    inserted_members AS (
        INSERT INTO substrate.edge_member (
            edge_type_id,
            edge_hash,
            entity_hash,
            edge_role_id,
            role_position
        )
        SELECT edge_type_id, edge_hash, source_hash, source_role.id, 0
        FROM all_edges
        CROSS JOIN substrate.edge_role source_role
        WHERE source_role.code = 'source'

        UNION ALL

        SELECT edge_type_id, edge_hash, target_hash, target_role.id, 1
        FROM all_edges
        CROSS JOIN substrate.edge_role target_role
        WHERE target_role.code = 'target'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    SELECT count(*) INTO inserted_count
    FROM inserted_members;

    RETURN inserted_count;
END;
$$;
