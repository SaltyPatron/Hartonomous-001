-- 0020_model_source_decomposition.up.sql
-- Replace jammed per-model provenance strings ("hf:{...}@{snap8}") with decomposed,
-- typed, indexable model-identity tables.
--
-- Per-model identity has four orthogonal tiers:
--   1. registry (huggingface, kaggle, local_file, …) — small reference table
--   2. publisher (deepseek-ai, meta, google, …) — reference table keyed by (registry, slug)
--   3. model slug — free-form text per publisher
--   4. revision — full-width BLAKE3 or git-sha bytes
--
-- Each tier is filterable, indexable, and FK-joinable. Revision is bytea(32) — full width,
-- never truncated to fit an arbitrary varchar cap.
--
-- substrate.provenance remains as-is — its role is the category-tier trust prior
-- (curator_class seeding Glicko-2 μ). Per-model identity is orthogonal and belongs here.

-- ── Registry reference table ────────────────────────────────────────────────

CREATE TABLE substrate.model_registry (
    id           SERIAL PRIMARY KEY,
    code         VARCHAR(32) NOT NULL UNIQUE,
    display_name VARCHAR(128) NOT NULL
);
COMMENT ON TABLE substrate.model_registry IS
    'Where a model was obtained from. Registry-tier identity — small, stable, filterable.';

INSERT INTO substrate.model_registry (code, display_name) VALUES
    ('huggingface', 'Hugging Face Hub'),
    ('kaggle',      'Kaggle Models'),
    ('local_file',  'Local filesystem'),
    ('github',      'GitHub release artifact'),
    ('ollama',      'Ollama model registry')
ON CONFLICT (code) DO NOTHING;

-- ── Publisher reference table ───────────────────────────────────────────────

CREATE TABLE substrate.model_publisher (
    id           SERIAL PRIMARY KEY,
    registry_id  INT NOT NULL REFERENCES substrate.model_registry(id) ON DELETE RESTRICT,
    slug         VARCHAR(128) NOT NULL,
    display_name VARCHAR(256),
    UNIQUE (registry_id, slug)
);
CREATE INDEX idx_model_publisher_registry ON substrate.model_publisher(registry_id);
CREATE INDEX idx_model_publisher_slug     ON substrate.model_publisher(slug);
COMMENT ON TABLE substrate.model_publisher IS
    'Publisher / organization identity, scoped within a registry. Reusable across all of a publisher''s models.';
COMMENT ON COLUMN substrate.model_publisher.slug IS
    'As it appears in the registry — e.g. "deepseek-ai", "meta-llama", "google".';

-- ── Model source table (per-model-instance identity) ────────────────────────

CREATE TABLE substrate.model_source (
    id             BIGSERIAL PRIMARY KEY,
    registry_id    INT NOT NULL REFERENCES substrate.model_registry(id) ON DELETE RESTRICT,
    publisher_id   INT NOT NULL REFERENCES substrate.model_publisher(id) ON DELETE RESTRICT,
    model_slug     TEXT NOT NULL,
    revision       BYTEA NOT NULL,
    discovered_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT model_source_revision_length CHECK (octet_length(revision) IN (20, 32)),
    UNIQUE (registry_id, publisher_id, model_slug, revision)
);
CREATE INDEX idx_model_source_publisher ON substrate.model_source(publisher_id);
CREATE INDEX idx_model_source_slug      ON substrate.model_source(model_slug);
CREATE INDEX idx_model_source_revision  ON substrate.model_source(revision);

COMMENT ON TABLE substrate.model_source IS
    'Per-model-instance identity decomposed into typed, indexable tiers. Replaces the jammed "hf:{...}@{snap8}" provenance code pattern.';
COMMENT ON COLUMN substrate.model_source.model_slug IS
    'Repository/model name within the publisher — e.g. "DeepSeek-Coder-V2-Lite-Instruct", "all-MiniLM-L6-v2".';
COMMENT ON COLUMN substrate.model_source.revision IS
    'Full-width revision hash: 20 bytes for git-sha1, 32 bytes for BLAKE3. Never truncated.';

-- ── Checkpoint state per (model_source, pass) — used by the pass orchestrator ─

CREATE TABLE substrate.model_pass_checkpoint (
    id                BIGSERIAL PRIMARY KEY,
    model_source_id   BIGINT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    pass_id           VARCHAR(64) NOT NULL,
    completed_at      TIMESTAMPTZ,
    entity_count      BIGINT NOT NULL DEFAULT 0,
    edge_count        BIGINT NOT NULL DEFAULT 0,
    last_error        TEXT,
    UNIQUE (model_source_id, pass_id)
);
CREATE INDEX idx_model_pass_checkpoint_model ON substrate.model_pass_checkpoint(model_source_id);
CREATE INDEX idx_model_pass_checkpoint_pass  ON substrate.model_pass_checkpoint(pass_id);

COMMENT ON TABLE substrate.model_pass_checkpoint IS
    'Orchestration state for IModelAnalysisPass runs. completed_at NULL = not yet run; non-null last_error = failed.';

-- ── Seed common publishers so first-run doesn't have to upsert dozens ──────

INSERT INTO substrate.model_publisher (registry_id, slug, display_name)
SELECT r.id, p.slug, p.display_name
FROM substrate.model_registry r
CROSS JOIN (VALUES
    ('deepseek-ai',             'DeepSeek AI'),
    ('meta-llama',              'Meta AI (Llama)'),
    ('google',                  'Google'),
    ('microsoft',               'Microsoft'),
    ('Qwen',                    'Alibaba Qwen'),
    ('nvidia',                  'NVIDIA'),
    ('black-forest-labs',       'Black Forest Labs'),
    ('Ultralytics',             'Ultralytics (YOLO)'),
    ('sentence-transformers',   'Sentence Transformers'),
    ('openai',                  'OpenAI'),
    ('mistralai',               'Mistral AI'),
    ('stabilityai',             'Stability AI'),
    ('facebook',                'Facebook AI'),
    ('bert-base-uncased',       'BERT (community)'),
    ('fishaudio',               'Fish Audio'),
    ('laion',                   'LAION')
) AS p(slug, display_name)
WHERE r.code = 'huggingface'
ON CONFLICT (registry_id, slug) DO NOTHING;
