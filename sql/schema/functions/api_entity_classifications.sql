CREATE OR REPLACE FUNCTION substrate.api_entity_classifications(
    p_entity_hash BYTEA
) RETURNS JSONB
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'entityTypeId', et.id,
                'entityTypeCode', et.code,
                'provenanceId', ec.provenance_id,
                'provenanceCode', p.code
            )
            ORDER BY et.code, p.code
        ),
        '[]'::jsonb
    )
      FROM substrate.entity_classification ec
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
      JOIN substrate.provenance p ON p.id = ec.provenance_id
     WHERE ec.entity_hash = p_entity_hash;
$f$;