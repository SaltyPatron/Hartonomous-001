CREATE OR REPLACE FUNCTION substrate.query_tensors_for_model_source(
    p_model_source_id INT
)
RETURNS TABLE (
    package_type_code TEXT,
    package_hash      BYTEA,
    ordinal           INT,
    occurrence_type_code TEXT,
    occurrence_hash   BYTEA,
    tensor_type_code  TEXT,
    tensor_hash       BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT DISTINCT
           package_type.code AS package_type_code,
           package_class.entity_hash AS package_hash,
           sequence_row.ordinal,
           occurrence_type.code AS occurrence_type_code,
           sequence_row.child_hash AS occurrence_hash,
           tensor_type.code AS tensor_type_code,
           tensor_sequence.child_hash AS tensor_hash
      FROM substrate.entity_model_source package_source
      JOIN substrate.entity_classification package_class
        ON package_class.entity_hash = package_source.entity_hash
      JOIN substrate.entity_type package_type
        ON package_type.id = package_class.entity_type_id
       AND package_type.code = 'model_package'
      JOIN substrate.sequence sequence_row
        ON sequence_row.parent_hash = package_class.entity_hash
      JOIN substrate.entity_classification occurrence_class
        ON occurrence_class.entity_hash = sequence_row.child_hash
      JOIN substrate.entity_type occurrence_type
        ON occurrence_type.id = occurrence_class.entity_type_id
       AND occurrence_type.code = 'model_package_tensor'
      JOIN substrate.sequence tensor_sequence
        ON tensor_sequence.parent_hash = sequence_row.child_hash
       AND tensor_sequence.ordinal = 1
      JOIN substrate.entity_classification tensor_class
        ON tensor_class.entity_hash = tensor_sequence.child_hash
      JOIN substrate.entity_type tensor_type
        ON tensor_type.id = tensor_class.entity_type_id
       AND tensor_type.code = 'tensor'
     WHERE package_source.model_source_id = p_model_source_id
     ORDER BY package_class.entity_hash ASC, sequence_row.ordinal ASC;
$f$;

COMMENT ON FUNCTION substrate.query_tensors_for_model_source(INT) IS
    'Return one model_source package tensor enumeration from sequence(model_package -> tensor), preserving package-scoped tensor order without conflating shared model_architecture entities.';
