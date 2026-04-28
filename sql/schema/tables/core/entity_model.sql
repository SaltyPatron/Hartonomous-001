-- Entity types 23..25: tensor, model_architecture, attention_pattern.
CREATE TABLE substrate.entity_model
    PARTITION OF substrate.entity FOR VALUES IN (23, 24, 25);
