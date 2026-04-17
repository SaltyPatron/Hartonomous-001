-- 0021_entity_model_source.up.sql
-- Junction table, stored procedures, and reporting views that replace the jammed
-- per-model provenance string ("hf:{idFingerprint}@{snap8}") with typed, indexable,
-- FK-joinable model identity.
--
-- Identity now flows through:
--     model_registry  →  model_publisher  →  model_source
--                                              ↓
--                                 entity_model_source  ←  substrate.entity
--
-- Per-model specificity lives in model_source + entity_model_source. The category-tier
-- substrate.provenance row "huggingface_model" is the only thing edges reference.
--
-- All upserts are procedures (not inline SQL in C#). All lookups are views or STABLE
-- PARALLEL SAFE functions. Callers CALL / SELECT — they never compose SQL against the
-- identity tables directly.

-- ── Junction: entity ↔ model_source ─────────────────────────────────────────

-- substrate.entity is LIST-partitioned on entity_type_id, so its PK is (id, entity_type_id).
-- FKs into the partitioned table must include both columns — standard PG partitioning rule.
-- Carrying entity_type_id here also makes the junction directly filterable by type (e.g.,
-- "all tensor entities for this model") without a second join.

CREATE TABLE substrate.entity_model_source (
    entity_id        BIGINT      NOT NULL,
    entity_type_id   INT         NOT NULL,
    model_source_id  BIGINT      NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    observed_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_id, entity_type_id, model_source_id),
    FOREIGN KEY (entity_id, entity_type_id)
        REFERENCES substrate.entity(id, entity_type_id) ON DELETE CASCADE
);

-- Covers the query directions we actually run:
--   * "every entity from this model_source" → idx_entity_model_source_model
--   * "tensor/firefly entities only for this model" → idx_entity_model_source_model_type
--   * "every model that introduced this entity" (dedup inspection) → idx_entity_model_source_entity
CREATE INDEX idx_entity_model_source_model       ON substrate.entity_model_source(model_source_id);
CREATE INDEX idx_entity_model_source_model_type  ON substrate.entity_model_source(model_source_id, entity_type_id);
CREATE INDEX idx_entity_model_source_entity      ON substrate.entity_model_source(entity_id, entity_type_id);

COMMENT ON TABLE substrate.entity_model_source IS
    'Links entities (model_architecture, tensor, bpe_token firefly, …) to the model_source that introduced them. Dedup-friendly: one entity, many source rows.';

-- ── Category-tier provenance row used by all HF-sourced edges ──────────────

INSERT INTO substrate.provenance (code, curator_class, initial_mu)
VALUES ('huggingface_model', 'model_derived', 60000.0)
ON CONFLICT (code) DO NOTHING;

-- ── Functions: model identity upserts ──────────────────────────────────────
-- Functions (not procedures) so callers can SELECT them in positional mode and read
-- the id back as a scalar. Npgsql positional CALL forbids OUT params (only the
-- CommandType.StoredProcedure path supports them); SELECT-from-function sidesteps
-- that entirely. The was_created flag wasn't consumed by any caller — drop it.

CREATE OR REPLACE FUNCTION substrate.upsert_model_registry(
    p_code         VARCHAR(32),
    p_display_name VARCHAR(128)
) RETURNS INT
LANGUAGE plpgsql AS $$
DECLARE
    v_id INT;
BEGIN
    SELECT id INTO v_id FROM substrate.model_registry WHERE code = p_code;
    IF FOUND THEN
        RETURN v_id;
    END IF;

    INSERT INTO substrate.model_registry (code, display_name)
    VALUES (p_code, p_display_name)
    ON CONFLICT (code) DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN
        SELECT id INTO STRICT v_id FROM substrate.model_registry WHERE code = p_code;
    END IF;
    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.upsert_model_publisher(
    p_registry_id  INT,
    p_slug         VARCHAR(128),
    p_display_name VARCHAR(256)
) RETURNS INT
LANGUAGE plpgsql AS $$
DECLARE
    v_id INT;
BEGIN
    SELECT id INTO v_id
    FROM substrate.model_publisher
    WHERE registry_id = p_registry_id AND slug = p_slug;
    IF FOUND THEN
        RETURN v_id;
    END IF;

    INSERT INTO substrate.model_publisher (registry_id, slug, display_name)
    VALUES (p_registry_id, p_slug, p_display_name)
    ON CONFLICT (registry_id, slug) DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN
        SELECT id INTO STRICT v_id
        FROM substrate.model_publisher
        WHERE registry_id = p_registry_id AND slug = p_slug;
    END IF;
    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.upsert_model_source(
    p_registry_id  INT,
    p_publisher_id INT,
    p_model_slug   TEXT,
    p_revision     BYTEA
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_id BIGINT;
BEGIN
    IF octet_length(p_revision) NOT IN (20, 32) THEN
        RAISE EXCEPTION 'model_source.revision must be exactly 20 (git-sha1) or 32 (BLAKE3) bytes, got %', octet_length(p_revision);
    END IF;

    SELECT id INTO v_id
    FROM substrate.model_source
    WHERE registry_id = p_registry_id
      AND publisher_id = p_publisher_id
      AND model_slug = p_model_slug
      AND revision = p_revision;
    IF FOUND THEN
        RETURN v_id;
    END IF;

    INSERT INTO substrate.model_source (registry_id, publisher_id, model_slug, revision)
    VALUES (p_registry_id, p_publisher_id, p_model_slug, p_revision)
    ON CONFLICT (registry_id, publisher_id, model_slug, revision) DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN
        SELECT id INTO STRICT v_id
        FROM substrate.model_source
        WHERE registry_id = p_registry_id
          AND publisher_id = p_publisher_id
          AND model_slug = p_model_slug
          AND revision = p_revision;
    END IF;
    RETURN v_id;
END;
$$;

CREATE OR REPLACE FUNCTION substrate.upsert_architecture_class(
    p_code VARCHAR
) RETURNS INT
LANGUAGE plpgsql AS $$
DECLARE
    v_id INT;
BEGIN
    SELECT id INTO v_id FROM substrate.architecture_class WHERE code = p_code;
    IF FOUND THEN
        RETURN v_id;
    END IF;

    INSERT INTO substrate.architecture_class (code)
    VALUES (p_code)
    ON CONFLICT (code) DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN
        SELECT id INTO STRICT v_id FROM substrate.architecture_class WHERE code = p_code;
    END IF;
    RETURN v_id;
END;
$$;

-- Bulk link: three equal-length arrays in one set-based INSERT. Callers use this
-- instead of per-row inserts — matches the CLAUDE.md "batch everything" mandate.
CREATE OR REPLACE FUNCTION substrate.link_entity_model_sources(
    p_entity_ids        BIGINT[],
    p_entity_type_ids   INT[],
    p_model_source_ids  BIGINT[]
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_rows BIGINT;
BEGIN
    IF array_length(p_entity_ids, 1) IS DISTINCT FROM array_length(p_model_source_ids, 1)
       OR array_length(p_entity_ids, 1) IS DISTINCT FROM array_length(p_entity_type_ids, 1) THEN
        RAISE EXCEPTION 'link_entity_model_sources: array lengths must match. entity_ids=%, entity_type_ids=%, model_source_ids=%',
            array_length(p_entity_ids, 1),
            array_length(p_entity_type_ids, 1),
            array_length(p_model_source_ids, 1);
    END IF;

    WITH ins AS (
        INSERT INTO substrate.entity_model_source (entity_id, entity_type_id, model_source_id)
        SELECT e, et, s
        FROM unnest(p_entity_ids, p_entity_type_ids, p_model_source_ids) AS t(e, et, s)
        ON CONFLICT (entity_id, entity_type_id, model_source_id) DO NOTHING
        RETURNING 1
    )
    SELECT COUNT(*) INTO v_rows FROM ins;
    RETURN v_rows;
END;
$$;

-- ── Views: typed, indexed reporting surface ────────────────────────────────

CREATE OR REPLACE VIEW substrate.v_model_source_detail AS
SELECT
    ms.id            AS model_source_id,
    r.code           AS registry_code,
    r.display_name   AS registry_display_name,
    mp.slug          AS publisher_slug,
    mp.display_name  AS publisher_display_name,
    ms.model_slug    AS model_slug,
    ms.revision      AS revision,
    encode(ms.revision, 'hex') AS revision_hex,
    ms.discovered_at AS discovered_at
FROM substrate.model_source ms
JOIN substrate.model_registry  r  ON r.id  = ms.registry_id
JOIN substrate.model_publisher mp ON mp.id = ms.publisher_id;

COMMENT ON VIEW substrate.v_model_source_detail IS
    'Joined per-model identity: registry + publisher + slug + revision (hex and raw). Primary filter surface for model-aware queries.';

CREATE OR REPLACE VIEW substrate.v_entity_model_provenance AS
SELECT
    ems.entity_id      AS entity_id,
    et.code            AS entity_type_code,
    v.model_source_id  AS model_source_id,
    v.registry_code    AS registry_code,
    v.publisher_slug   AS publisher_slug,
    v.model_slug       AS model_slug,
    v.revision_hex     AS revision_hex,
    ems.observed_at    AS observed_at
FROM substrate.entity_model_source ems
JOIN substrate.entity        e  ON e.id  = ems.entity_id
JOIN substrate.entity_type   et ON et.id = e.entity_type_id
JOIN substrate.v_model_source_detail v ON v.model_source_id = ems.model_source_id;

COMMENT ON VIEW substrate.v_entity_model_provenance IS
    'Every (entity → model_source) link with denormalized identity for fast filtering in reports, sanity checks, and dedup inspection.';
