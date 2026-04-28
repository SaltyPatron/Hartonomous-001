CREATE OR REPLACE FUNCTION substrate.upsert_model_source(
    p_registry_id  INT,
    p_publisher_id INT,
    p_model_slug   TEXT,
    p_revision     BYTEA
) RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE v_id BIGINT;
BEGIN
    INSERT INTO substrate.model_source (model_id, publisher_id, source_path, source_format, revision_hash)
    VALUES (p_registry_id, p_publisher_id, p_model_slug, 'safetensors', p_revision)
    ON CONFLICT (model_id, source_path, revision_label) DO UPDATE
        SET revision_hash = EXCLUDED.revision_hash
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;
