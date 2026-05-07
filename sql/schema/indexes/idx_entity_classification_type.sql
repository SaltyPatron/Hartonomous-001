CREATE INDEX IF NOT EXISTS idx_entity_classification_type
    ON substrate.entity_classification(entity_type_id, entity_hash);
