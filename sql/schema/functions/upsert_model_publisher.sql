CREATE OR REPLACE FUNCTION substrate.upsert_model_publisher(
    p_registry_id   INT,
    p_slug          TEXT,
    p_display_name  TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    -- p_registry_id is a positional vestige of the prior schema; the new
    -- substrate.model_publisher row stands alone keyed by name/slug.
    PERFORM p_registry_id;
    INSERT INTO substrate.model_publisher (name, organization)
    VALUES (p_slug, p_display_name)
    ON CONFLICT (name) DO UPDATE SET organization = EXCLUDED.organization
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;
