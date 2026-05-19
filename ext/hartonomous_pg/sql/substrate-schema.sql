/* GENERATED — do not edit by hand. Source: sql/schema/bootstrap.sql + included files.
 * Concatenated by: scripts/build/concat_extension_sql.py
 *
 * This script is substrate schema content — substrate / monitor
 * schemas, domains, composite types, tables, indexes, seed inserts,
 * substrate.* SQL/plpgsql functions, procedures, views. Applied
 * via plain psql -f under the user's database role (no sudo).
 *
 * Prerequisite — the hartonomous extension must already be
 * installed via CREATE EXTENSION hartonomous (which cascades the
 * postgis + btree_gist + pg_trgm prerequisites and installs the
 * .so's C-binding declarations). The substrate / monitor schemas
 * must also exist before this script runs — the extension's
 * C-binding script creates functions inside substrate.*. See
 * scripts/linux/db-bootstrap.sh for the runtime apply path. */

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────
-- BUILD-TIME @INCLUDE MANIFEST for the consolidated PostgreSQL extension.
--
-- This file is no longer the runtime apply path. The substrate is now
-- packaged as a proper PG extension (see ext/hartonomous_pg/) and
-- installed via `CREATE EXTENSION hartonomous`. At build time, the
-- script scripts/build/concat_extension_sql.py walks the @include
-- directives below in order, expands them recursively, strips psql
-- meta-commands, splices in the hand-written
-- ext/hartonomous_pg/sql/hartonomous--1.0.sql.in (C-binding declarations)
-- before the first functions/* include, and emits the consolidated
-- ext/hartonomous_pg/sql/hartonomous--1.0.sql that PostgreSQL runs
-- atomically when CREATE EXTENSION fires.
--
-- Same pattern as PostGIS / pgvector: many small per-object source files
-- + a build-time concatenator → single extension script.
--
-- Order below is the FK + function dependency chain. Reference tables
-- before core tables that FK to them; core tables before junctions that
-- FK to entity; functions last so every table they query exists.
--
-- Schema/extensions/*.sql files (postgis, btree_gist, pg_trgm,
-- hartonomous itself) are SKIPPED by the concatenator: prerequisite
-- extensions are declared in hartonomous.control's `requires` and
-- auto-installed by CREATE EXTENSION ... CASCADE; the hartonomous
-- self-include cannot CREATE EXTENSION inside its own install script.
--
-- ── Phase 1: extensions ──────────────────────────────────────────────
-- (skipped @include schema/extensions/postgis.sql — handled via control file `requires`)
-- (skipped @include schema/extensions/btree_gist.sql — handled via control file `requires`)
-- (skipped @include schema/extensions/pg_trgm.sql — handled via control file `requires`)

-- ── Phase 2: schemas ─────────────────────────────────────────────────

-- ── sql/schema/schemas/substrate.sql ───────────────────────────────────────
CREATE SCHEMA IF NOT EXISTS substrate;
COMMENT ON SCHEMA substrate IS
    'Content-addressed substrate. Every table here is keyed on BLAKE3 hashes; no surrogate IDs.';

-- ── sql/schema/schemas/monitor.sql ───────────────────────────────────────
CREATE SCHEMA IF NOT EXISTS monitor;
COMMENT ON SCHEMA monitor IS
    'Operational telemetry: ingestion progress, phase status, inference metrics, error log. Not part of substrate identity.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 3: domains ─────────────────────────────────────────────────

-- ── sql/schema/domains/hash_value.sql ───────────────────────────────────────
CREATE DOMAIN substrate.hash_value AS BYTEA
    CONSTRAINT hash_value_length CHECK (octet_length(VALUE) = 32);
COMMENT ON DOMAIN substrate.hash_value IS
    'BLAKE3 256-bit hash. The substrate''s only identity surface — entities and edges are keyed on (type_id, hash_value).';

-- ── sql/schema/domains/significance_mu.sql ───────────────────────────────────────
CREATE DOMAIN substrate.significance_mu AS FLOAT8;
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Wide-band: trust priors 20K (user_session) to 100K (authoritative_standard); arena-specific overrides via provenance_edge_authority can exceed source defaults. Values evolve via comparison events. The COALESCE prior formula in the edge_significance view computes effective μ from (provenance × modality × edge_type semantic_weight × lineage decay).';

-- ── sql/schema/domains/significance_sigma.sql ───────────────────────────────────────
CREATE DOMAIN substrate.significance_sigma AS FLOAT8
    CONSTRAINT sigma_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_sigma IS
    'Glicko-2 rating uncertainty. Decreases as evidence accumulates. Strictly positive.';

-- ── sql/schema/domains/significance_volatility.sql ───────────────────────────────────────
CREATE DOMAIN substrate.significance_volatility AS FLOAT8
    CONSTRAINT volatility_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_volatility IS
    'Glicko-2 meta-uncertainty (rate of mu change). Strictly positive.';

-- ── sql/schema/domains/code_value.sql ───────────────────────────────────────
CREATE DOMAIN substrate.code_value AS VARCHAR(128)
    CONSTRAINT code_not_empty CHECK (LENGTH(TRIM(VALUE)) > 0);
COMMENT ON DOMAIN substrate.code_value IS
    'Reference table code column. Never empty or whitespace-only.';

-- ── sql/schema/domains/tier_number.sql ───────────────────────────────────────
CREATE DOMAIN substrate.tier_number AS INTEGER
    CONSTRAINT tier_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.tier_number IS
    'Composition tier. 0 = atom (codepoint, codeword, sample). Emergent from reference depth, not stored as a column.';

-- ── sql/schema/domains/modality_code.sql ───────────────────────────────────────
CREATE DOMAIN substrate.modality_code AS VARCHAR(32)
    CONSTRAINT modality_code_known CHECK (
        VALUE IN ('text', 'image', 'audio', 'video', 'model_weights')
    );
COMMENT ON DOMAIN substrate.modality_code IS
    'Finite provenance authority modality code.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 4: composite types ─────────────────────────────────────────

-- ── sql/schema/types/entity_ref.sql ───────────────────────────────────────
CREATE TYPE substrate.entity_ref AS (
    entity_type_id INT,
    entity_hash    substrate.hash_value
);
COMMENT ON TYPE substrate.entity_ref IS
    'Composite entity reference: the substrate''s sole identity surface. Used as parameter and return type for substrate functions and the hartonomous extension.';

-- ── sql/schema/types/edge_ref.sql ───────────────────────────────────────
CREATE TYPE substrate.edge_ref AS (
    edge_type_id INT,
    edge_hash    substrate.hash_value
);
COMMENT ON TYPE substrate.edge_ref IS
    'Composite edge reference: identity surface for substrate.edge. Used in significance updates and traversal results.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 5: reference tables (no FK to substrate-side) ──────────────

-- ── sql/schema/tables/reference/entity_type.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_type (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(64) NOT NULL UNIQUE,
    modality  VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.entity_type(id)
);

COMMENT ON TABLE substrate.entity_type IS
    'Structural classification of entities by content kind and modality. Identifies which partition of substrate.entity a row belongs to.';

-- ── sql/schema/tables/reference/edge_role.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.edge_role IS
    'Participant roles in n-ary edges (source, target, context, mediator, evidence, head, dependent).';

-- ── sql/schema/tables/reference/physicality_type.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.physicality_type IS
    'Geometry interpretation. What the geometry4d value in substrate.physicality represents (s3_position, contour, weight_distribution, etc.).';

-- ── sql/schema/tables/reference/significance_context.sql ───────────────────────────────────────
CREATE TABLE substrate.significance_context (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.significance_context IS
    'Open-vocabulary arena definitions. Codes can be added at runtime; significance must auto-prime against every existing arena (rule 45 AP-1).';

-- ── sql/schema/tables/reference/attestation_type.sql ───────────────────────────────────────
-- AttestationType reference vocabulary. Open vocabulary, same shape as
-- entity_type / edge_type / significance_context. Distinguishes WHAT KIND OF
-- EVIDENCE supports a Glicko-2 rating row from WHO asserted it (provenance),
-- WHAT RELATION KIND (edge_type), and WHICH ARENA (significance_context).
--
-- The four discriminators together give a 4D rating surface:
--   (arena × subject × attestation_type × provenance) → (mu, sigma, games)
--
-- Codes are open-vocabulary at runtime; the seed below is the starter set.
-- Adding a new attestation_type at runtime requires no schema change — the
-- significance partitions accept any valid attestation_type_id by FK.
--
-- Per-event weight default lives on the row so the weighted Glicko-2 bulk
-- update can scale events differently per attestation_type without callers
-- having to know the weight (e.g. corpus_co_occurrence_window default 0.1
-- because individual window slides are low-confidence; lexical_curated_relation
-- default 1.0 because curated lexicons are high-confidence per attestation).
CREATE TABLE substrate.attestation_type (
    id                    SERIAL PRIMARY KEY,
    code                  VARCHAR(64) NOT NULL UNIQUE,
    description           TEXT        NOT NULL,
    default_event_weight  FLOAT8      NOT NULL DEFAULT 1.0,
    default_initial_mu    FLOAT8      NOT NULL DEFAULT 1500.0,
    default_initial_sigma FLOAT8      NOT NULL DEFAULT 350.0
);

COMMENT ON TABLE substrate.attestation_type IS
    'Open-vocabulary kinds-of-evidence. Each attestation_type carries a default per-event weight used by hartonomous.glicko2_bulk_update_weighted. Adding a new code requires no schema change; partitions accept any FK-valid id.';

-- ── sql/schema/tables/reference/provenance.sql ───────────────────────────────────────
-- substrate.provenance — source of an entity or edge with trust prior.
--
-- The provenance row carries the trust topology axes the substrate combines
-- into per-arena Glicko-2 priors:
--
--   trust = f(provenance × modality × content-kind × lineage × asserter × tenant-scope)
--
-- The COALESCE formula in the substrate's edge_significance view (and in
-- pg_traverse_astar's bulk-fetch) computes effective μ from these axes:
--
--   μ₀ = COALESCE(
--          provenance_edge_authority.initial_mu,
--          p.initial_mu × et.semantic_weight × p.derivation_decay
--        )
--
-- initial_mu lives in the wide-band tier ladder (20K user-tier through 100K
-- authoritative-standard); paired with initial_sigma per source. modality_codes
-- enumerates the modalities a source is authoritative in. derives_from +
-- derivation_decay model authority lineage (e.g. OMW = 0.92 × WordNet).
-- scope_kind / scope_entity_* support per-tenant and per-user provenances —
-- these tenant/user provenances point at their entity row in
-- substrate.entity (entity types 'tenant' / 'user').

CREATE TABLE substrate.provenance (
    id                   SERIAL PRIMARY KEY,
    code                 VARCHAR(64) NOT NULL UNIQUE,
    curator_class        VARCHAR(32) NOT NULL,
    initial_mu           FLOAT8      NOT NULL,
    -- Per-source uncertainty for Glicko-2 priors (was hardcoded 350 before
    -- the wide-band tier ladder reseed).
    initial_sigma        FLOAT8      NOT NULL DEFAULT 350.0,
    -- Modalities this source is authoritative in moved to junction
    -- substrate.provenance_modality (proper relational shape — no array
    -- columns in substrate.* tables; 1NF / FK / btree-indexability discipline).
    -- Lineage: code of an upstream source whose authority this one inherits.
    derives_from         VARCHAR(64),
    -- Lineage decay factor applied when the parent's trust flows through.
    -- 1.0 = no decay; OMW from princeton_wordnet uses 0.92.
    derivation_decay     FLOAT8      NOT NULL DEFAULT 1.0,
    -- Scope: 'global' (default), 'tenant' (org-scoped), 'user' (user-scoped).
    -- Per-tenant and per-user provenances are first-class — their own
    -- substrate.entity_significance rows are their reliability scores.
    scope_kind           TEXT        NOT NULL DEFAULT 'global'
                                     CHECK (scope_kind IN ('global', 'tenant', 'user')),
    -- When scope_kind ≠ 'global', identifies which tenant/user owns this
    -- provenance via composite handle into substrate.entity.
    scope_entity_type_id INT,
    scope_entity_hash    substrate.hash_value,
    -- Self-referential lineage FK; deferred so seeding can insert in any order.
    CONSTRAINT provenance_derives_from_fkey
        FOREIGN KEY (derives_from) REFERENCES substrate.provenance(code)
        DEFERRABLE INITIALLY DEFERRED
);

COMMENT ON TABLE substrate.provenance IS
    'Source of an entity or edge with trust prior. Carries the trust topology axes (lineage, scope) the substrate combines into per-arena Glicko-2 priors via COALESCE(provenance_edge_authority.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay). Modality authority lives in the substrate.provenance_modality junction.';
COMMENT ON COLUMN substrate.provenance.curator_class IS
    'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';
COMMENT ON COLUMN substrate.provenance.initial_mu IS
    'Glicko-2 prior μ. Wide-band ladder: 20K (user_session) → 100K (authoritative_standard). Edge-time prior is multiplied by edge_type.semantic_weight × derivation_decay (with optional provenance_edge_authority override).';
COMMENT ON COLUMN substrate.provenance.derives_from IS
    'Code of an upstream provenance whose authority this one inherits — together with derivation_decay, models trust lineage (OMW ← princeton_wordnet at 0.92).';
COMMENT ON COLUMN substrate.provenance.scope_kind IS
    'global = system-wide source; tenant = org-scoped; user = user-scoped. Tenant/user provenances are first-class substrate citizens — their entity_significance per arena IS their reliability score.';

-- ── sql/schema/tables/reference/architecture_class.sql ───────────────────────────────────────
CREATE TABLE substrate.architecture_class (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.architecture_class IS
    'Model architecture classification (transformer, mamba, mixture-of-experts, etc.).';

-- ── sql/schema/tables/reference/tensor_role.sql ───────────────────────────────────────
CREATE TABLE substrate.tensor_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.tensor_role IS
    'Tensor classification: attention_q, attention_k, attention_v, attention_o, ffn_up, ffn_down, ffn_gate, embed, lm_head, layer_norm_pre, layer_norm_post, rope_freq, moe_router, moe_expert, etc.';

-- ── sql/schema/tables/reference/script.sql ───────────────────────────────────────
CREATE TABLE substrate.script (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.script IS
    'Unicode Script property. 160+ scripts; grows per Unicode version. Populated by UCD seed.';

-- ── sql/schema/tables/reference/block.sql ───────────────────────────────────────
CREATE TABLE substrate.block (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(128) NOT NULL UNIQUE,
    range_start INT NOT NULL,
    range_end   INT NOT NULL
);

COMMENT ON TABLE substrate.block IS
    'Unicode Block ranges. 300+ blocks. range_start/range_end enable O(log n) block lookup by codepoint integer.';

-- ── sql/schema/tables/reference/break_property.sql ───────────────────────────────────────
CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    enum_id  INT NOT NULL,
    UNIQUE(code, category),
    UNIQUE(category, enum_id)
);

COMMENT ON TABLE substrate.break_property IS
    'UAX #29 break properties for segmentation. Five categories: GCB (grapheme), WB (word), SB (sentence), LB (line), InCB (Indic conjunct break). enum_id is the per-category enum value from the embedded UCD blob (UC_GCB_*, UC_WB_*, UC_SB_*, UC_LB_*, UC_INCB_* in pg_ucd_segmentation.h). codepoint_property FK lookups use (category, enum_id) — robust against ID-offset drift when UCD versions add or reorder enum values.';

-- ── sql/schema/tables/reference/bidi_class.sql ───────────────────────────────────────
CREATE TABLE substrate.bidi_class (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(8) NOT NULL UNIQUE,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.bidi_class IS
    'UAX #9 Bidirectional Character Type. ~23 values (L, R, AL, EN, ES, ...). Populated by UCD seed from DerivedBidiClass.txt.';

-- ── sql/schema/tables/reference/east_asian_width.sql ───────────────────────────────────────
CREATE TABLE substrate.east_asian_width (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(2) NOT NULL UNIQUE,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.east_asian_width IS
    'UAX #11 East Asian Width. Six values: N (Neutral), Na (Narrow), A (Ambiguous), W (Wide), F (Fullwidth), H (Halfwidth). Populated by UCD seed from EastAsianWidth.txt.';

-- ── sql/schema/tables/reference/language.sql ───────────────────────────────────────
CREATE TABLE substrate.language (
    id     SERIAL PRIMARY KEY,
    code   VARCHAR(3) NOT NULL UNIQUE CHECK (LENGTH(code) = 3),
    name   VARCHAR(128) NOT NULL,
    scope  VARCHAR(1) NOT NULL CHECK (LENGTH(scope) = 1),
    type   VARCHAR(1) NOT NULL CHECK (LENGTH(type) = 1),
    part1  CHAR(2) NULL CHECK (part1  IS NULL OR LENGTH(part1)  = 2),
    part2b CHAR(3) NULL CHECK (part2b IS NULL OR LENGTH(part2b) = 3),
    part2t CHAR(3) NULL CHECK (part2t IS NULL OR LENGTH(part2t) = 3)
);

COMMENT ON TABLE substrate.language IS
    'ISO 639-3 language inventory (~7,928 rows). The 3-letter ISO 639-3 identifier is `code`. '
    'part1 is ISO 639-1 (2-letter), part2b is ISO 639-2/B (bibliographic), part2t is ISO 639-2/T '
    '(terminology). Part1 is the join key for CLDR locale identifiers (which use ISO 639-1 when '
    'available, else ISO 639-3).';
COMMENT ON COLUMN substrate.language.scope  IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type   IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';
COMMENT ON COLUMN substrate.language.part1  IS 'ISO 639-1 two-letter code. NULL when not assigned.';
COMMENT ON COLUMN substrate.language.part2b IS 'ISO 639-2/B bibliographic three-letter code. Usually equals code or part2t; differs for ~20 languages (e.g. ger vs deu).';
COMMENT ON COLUMN substrate.language.part2t IS 'ISO 639-2/T terminology three-letter code. Usually equals code.';


-- ── sql/schema/tables/reference/general_category.sql ───────────────────────────────────────
CREATE TABLE substrate.general_category (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(4) NOT NULL UNIQUE,
    group_code  VARCHAR(1) NOT NULL,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.general_category IS
    'Unicode General Category property. 30 values in 7 groups (L, M, N, P, S, Z, C).';

-- ── sql/schema/tables/reference/semantic_relation_type.sql ───────────────────────────────────────
CREATE TABLE substrate.semantic_relation_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.semantic_relation_type IS
    'WordNet semantic relation vocabulary. 26 pointer types (hypernym, hyponym, meronym, antonym, etc.).';

-- ── sql/schema/tables/reference/pos.sql ───────────────────────────────────────
CREATE TABLE substrate.pos (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.pos(id)
);
COMMENT ON TABLE substrate.pos IS
    'Part of speech classification. 17 universal UPOS tags + hierarchical subtypes from individual treebanks.';

-- ── sql/schema/tables/reference/deprel.sql ───────────────────────────────────────
CREATE TABLE substrate.deprel (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.deprel(id)
);
COMMENT ON TABLE substrate.deprel IS
    'Universal Dependencies relation types. 37 universal + language-specific subtypes.';

-- ── sql/schema/tables/reference/morph_feature.sql ───────────────────────────────────────
CREATE TABLE substrate.morph_feature (
    id        SERIAL PRIMARY KEY,
    key       VARCHAR(32) NOT NULL,
    value     VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.morph_feature(id),
    UNIQUE(key, value)
);

COMMENT ON TABLE substrate.morph_feature IS
    'Morphological feature key-value pairs (Number=Sing, Tense=Past, Mood=Ind, etc.). Each row = one (key, value).';
COMMENT ON COLUMN substrate.morph_feature.parent_id IS
    'Groups values under a common feature key row.';

-- ── sql/schema/tables/reference/lexname.sql ───────────────────────────────────────
CREATE TABLE substrate.lexname (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.lexname IS
    'WordNet lexicographer categories. 45 values (noun.animal, verb.motion, adj.all, etc.).';

-- ── sql/schema/tables/reference/edge_type.sql ───────────────────────────────────────
-- substrate.edge_type — typed-relation vocabulary.
--
-- Categories partition the LIST-partitioned substrate.edge table for index
-- locality (structural, semantic, syntactic, morphological, cross_lingual,
-- cross_modal, model_derived, unicode).
--
-- semantic_weight is the structural-value tier of the edge-kind for the
-- COALESCE prior formula:
--   μ₀ = COALESCE(pea.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay)
--
-- Tier ladder (set in seed/edge_type.sql):
--   1.0   has_sense, has_lemma, has_form, inflection_of, hypernym, hyponym,
--         instance_hypernym, instance_hyponym, antonym
--   0.9   member/substance/part holonyms+meronyms, has_morpheme
--   0.85  translation_of, aligned_to_synset, translation_link
--   0.7   has_etymology, has_pronunciation, has_hyphenation, has_wikidata
--   0.6   similar_to, also_see, verb_group, attribute, derivationally_related
--   0.5   synonym, related, coordinate_term, derived
CREATE TABLE substrate.edge_type (
    id              SERIAL PRIMARY KEY,
    code            VARCHAR(64) NOT NULL UNIQUE,
    category        VARCHAR(32) NOT NULL,
    source_type_id  INT REFERENCES substrate.entity_type(id),
    target_type_id  INT REFERENCES substrate.entity_type(id),
    -- Structural-value tier for COALESCE prior. Default 1.0 (full weight).
    semantic_weight FLOAT8 NOT NULL DEFAULT 1.0
);

COMMENT ON TABLE substrate.edge_type IS
    'Operational edge typing with domain/range entity type constraints + structural-value tier (semantic_weight) for the trust-prior formula. Categories: structural, semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, unicode.';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS
    'FK to entity_type — constrains which entity types can be source. NULL means polymorphic source.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS
    'FK to entity_type — constrains which entity types can be target. NULL means polymorphic target.';
COMMENT ON COLUMN substrate.edge_type.semantic_weight IS
    'Structural-value tier 0.5..1.0. POS/sense/antonym/structural carry full weight (1.0); looser semantic relations (synonym, related, coordinate_term) carry less. Multiplied into the COALESCE prior μ at edge_significance lookup time.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- provenance_modality junction is created here (before its seed-time INSERT in
-- Phase 6 seed/provenance.sql). The junction belongs in Phase 8 by topic but its
-- seed data is appended to seed/provenance.sql; create the table early so the
-- Phase-6 seed can populate it without a forward reference.

-- ── sql/schema/tables/junctions/provenance_modality.sql ───────────────────────────────────────
-- Junction: which modalities a provenance source is authoritative in.
-- Replaces the prior substrate.provenance.modality_codes array column —
-- proper relational shape with composite PK and bidirectional btree
-- indexes (no array column, no 1NF violation, no FK-integrity bypass).
CREATE TABLE substrate.provenance_modality (
    provenance_id INT NOT NULL REFERENCES substrate.provenance(id) ON DELETE CASCADE,
    modality_code substrate.modality_code NOT NULL,
    PRIMARY KEY (provenance_id, modality_code)
);

-- Reverse-lookup index lives in sql/schema/indexes/provenance_modality_modality_idx.sql
-- per "one primary CREATE object per file" discipline.

COMMENT ON TABLE substrate.provenance_modality IS
    'Junction table: which modalities a provenance source is authoritative in. Replaces the prior modality_codes array column on substrate.provenance — proper relational shape (atomic columns, composite PK, FK to substrate.provenance(id), bidirectional indexes). Empty join = source authoritative for none / text default.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 6: reference seed (entity_type before edge_type — FK code lookup) ─
-- provenance_edge_authority seed is deferred to Phase 8b (after the
-- junction table is created) since it INSERTs against substrate.provenance_edge_authority.

-- ── sql/schema/seed/entity_type.sql ───────────────────────────────────────
-- Entity types. Content-only — every row classifies CONTENT.
--
-- Identity is BLAKE3 over content bytes (per docs/00-substrate-spec.md §II.1).
-- Same content under multiple structural classifications collapses to one
-- entity row with multiple substrate.entity_classification rows.
--
-- Per docs/01-tensor-primitive-spec.md: per-role units of model tensors are
-- attestation EDGES between content entities (NOT separate entity types).
-- Per-tensor analytical surfaces (sparsity, weight distribution, SVD spectrum,
-- etc.) are physicality on the tensor entity (NOT separate entity types).
INSERT INTO substrate.entity_type (code, modality) VALUES
    -- Text
    ('codepoint',          'text'),
    ('grapheme_cluster',   'text'),
    ('word_form',          'text'),
    ('morpheme',           'text'),
    ('lemma',              'text'),
    ('text_composition',   'text'),
    ('paragraph',          'text'),
    ('document',           'text'),
    ('synset',             'text'),
    ('collation_element',  'text'),
    ('language_name',      'text'),
    -- Image
    ('pixel_region',       'image'),
    ('visual_concept',     'image'),
    ('object_query',       'image'),
    -- Audio
    ('audio_recording',    'audio'),
    ('audio_chunk',        'audio'),
    ('codec_codevector',   'audio'),
    -- Video
    ('video_frame',        'video'),
    -- Model package artifacts
    ('tensor',             'model_weights'),
    ('model_architecture', 'model_weights'),
    ('model_package',      'model_weights'),
    ('model_package_tensor','model_weights'),
    ('tokenizer_model',    'model_weights'),
    -- Reference-vocabulary entities (AP-8 correction, 2026-05-14):
    -- POS / lexname / language / morph feature / deprel / sense codes
    -- become content-hashed substrate entities. Corpus decomposers emit
    -- typed edges (has_pos, has_lexname, has_language, has_morph_feature,
    -- has_deprel_pattern) into these as edge targets. The unified Glicko-2
    -- surface (substrate.edge_significance) per (provenance × arena) is
    -- the authoritative consensus surface; legacy junction tables
    -- (entity_pos, entity_lexname, entity_language, entity_morph_feature,
    -- pattern_deprel) remain as denormalized analytics caches per AP-8.
    -- Identity = BLAKE3("{kind}:{code}") via
    -- Hartonomous.Core.Compute.Common.ReferenceVocabularyHashes.
    ('pos',                'text'),
    ('lexname',            'text'),
    ('morph_feature',      'text'),
    ('deprel',             'text'),
    ('sense',              'text'),
    -- Per-codepoint UCD property classifications (Gate 1 #38 refactor,
    -- 2026-05-18): each Unicode property code (general_category Lu, script
    -- Latn, block "Basic Latin", bidi_class AL, east_asian_width W,
    -- break_property "GCB:CR") becomes a content-hashed substrate entity
    -- via ReferenceVocabularyHashes.{GeneralCategory,Script,Block,BidiClass,
    -- EastAsianWidth,BreakProperty}EntityHash. Codepoint atoms emit typed
    -- edges (has_cp_general_category etc.) into these as edge targets.
    -- Cross-UCD-version attestation accumulates on the same edge identities
    -- under the unicode_version_consensus arena.
    ('general_category',   'text'),
    ('script',             'text'),
    ('block',              'text'),
    ('bidi_class',         'text'),
    ('east_asian_width',   'text'),
    ('break_property',     'text');

-- ── sql/schema/seed/physicality_type.sql ───────────────────────────────────────
-- Physicality types: exactly 3 rows.
--
-- Per rule 25-physicality-4d, the substrate has three physicality roles
-- and only three. Geometry SHAPE (POINT vs LINESTRING vs MULTILINESTRING
-- vs POLYGON, all ZM) carries the within-role structural distinction
-- the old per-modality codes (s3_position, waveform, contour, etc.) were
-- redundantly encoding. Modality lives on the entity_type of the entity
-- the physicality attaches to, NOT on physicality_type.
--
--   entity  (id 1) — the building block's own structure.
--                    atoms = POINTZM with real content-derived coords
--                            (codepoint Super-Fibonacci S^3 by UCA rank,
--                             audio sample value, pixel intensity, tensor
--                             cell, etc.).
--                    compositions = LINESTRINGZM through child centroids
--                            (word_form = LINESTRING through codepoint
--                             POINTZMs; grapheme_cluster, lemma, morpheme
--                             all live here). MULTILINESTRINGZM for
--                             branching shapes.
--
--   firefly (id 2) — per-model embedding-row POINTZM specimens attached
--                    to existing word_form entities. MULTIPOINTZM aggregate
--                    per entity across ingested models for cross-model
--                    Voronoi consensus.
--
--   content (id 3) — content-tier composition's mantissa-packed
--                    LINESTRINGZM whose vertices ARE child entity hash
--                    refs via substrate.bb_pack_*. text_composition,
--                    paragraph, document, audio_chunk, pixel_region,
--                    video_frame all carry this. The geometry IS the
--                    indexed child manifest. Reverse-resolve via
--                    substrate.entity_by_hash_prefix composite-btree.
INSERT INTO substrate.physicality_type (code) VALUES
    ('entity'),
    ('firefly'),
    ('content');

-- ── sql/schema/seed/physicality_type_trajectories.sql ───────────────────────────────────────
-- Additional physicality_type rows for the two-trajectory composition model.
--
-- The base seed (sql/schema/seed/physicality_type.sql) declares three primary
-- roles: entity / firefly / content. Compositions emit BOTH a real-coord
-- canonical-shape geometry (entity_shape, id 15) AND a mantissa-packed
-- ingestion trajectory (ingestion_trajectory, id 16). The two roles answer
-- distinct queries:
--
--   entity_shape          — Fréchet / Hausdorff structural-similarity matching
--                           ("is this thing structurally like that thing?").
--                           Vertices are children's identity POINTZM centroids
--                           in real metric space. POINTZM for atoms at modality
--                           anchor coords; LINESTRINGZM (or MULTILINESTRINGZM
--                           for branching shapes) for compositions through
--                           children's real-coord centroids.
--
--   ingestion_trajectory  — recomposition recipe. Vertices encode child
--                           identity bits via bb_pack_hash_lo / bb_pack_hash_hi
--                           / bb_pack_ordinal_rle / bb_pack_metadata.
--                           Reverse-resolve via substrate.entity_by_hash_prefix
--                           composite-btree on (hash_bits_0_51, hash_bits_52_103).
--                           LINESTRINGZM, or MULTILINESTRINGZM for branching /
--                           parallel / multi-tier content.
--
-- IDs are explicit (15, 16) to match downstream verification gates and the
-- decomposer routing in IngestionBatch.AddEntityShape / AddIngestionTrajectory.
-- The sequence is advanced past 16 so future SERIAL inserts pick up at the
-- next available id without collision.
INSERT INTO substrate.physicality_type (id, code) VALUES
    (15, 'entity_shape'),
    (16, 'ingestion_trajectory');

SELECT setval(
    pg_get_serial_sequence('substrate.physicality_type', 'id'),
    (SELECT MAX(id) FROM substrate.physicality_type),
    true
);

-- ── sql/schema/seed/edge_role.sql ───────────────────────────────────────
INSERT INTO substrate.edge_role (code) VALUES
    ('source'), ('target'), ('context'), ('mediator'),
    ('evidence'), ('head'), ('dependent');

-- ── sql/schema/seed/significance_context.sql ───────────────────────────────────────
-- 10 starter arenas. The substrate's significance_context is open vocabulary —
-- new arenas can be inserted at runtime; significance must auto-prime against
-- every arena in this table at the time of insert (rule 45 AP-1).
INSERT INTO substrate.significance_context (code) VALUES
    ('lexical_disambiguation'),
    ('syntactic_role_fitness'),
    ('translation_quality'),
    ('model_trust'),
    ('source_authority'),
    ('semantic_relevance'),
    ('corroboration_strength'),
    ('frequency_significance'),
    ('attention_pattern_confidence'),
    ('morphological_productivity'),
    -- Bigram next-token prior arena. Populated by
    -- substrate.populate_sequence_following_edges from content trajectory
    -- ordinals. Source of generative coherence at inference time.
    ('sequence_following'),
    -- Unicode/ISO/CLDR/encoding cross-source consensus arenas (per
    -- universal-cross-source-attestation framing). Each names a contested
    -- surface where multiple sources fire attestation events on shared
    -- content-addressed edge identities.
    ('unicode_version_consensus'),         -- 30 UCD versions attesting per-cp properties
    ('encoding_position_consensus'),       -- ASCII/ISO 8859/EBCDIC/Windows/JIS/GB/etc.
    ('ivd_collection_consensus'),          -- 5 IVD collections attesting ideographic variants
    ('unihan_reading_consensus'),          -- 4 Unihan reading languages
    ('consortium_discussion_density'),     -- L2/IRG/WG2 working docs (future scope)
    ('script_membership_consensus'),       -- Unicode + ISO 15924 + CLDR + corpus attestation
    ('language_codepoint_coverage_consensus'), -- per-language codepoint usage
    ('locale_definition_consensus');       -- per-CLDR-version locale definition stability

-- ── sql/schema/seed/attestation_type.sql ───────────────────────────────────────
-- Attestation types — generic, sign-discriminating only.
--
-- P1d (2026-05-14 architectural correction): the prior 27 modality-specific
-- rows (model_attention_qk_pattern, model_ffn_full_path, model_lm_head_projection,
-- model_cross_modal_alignment, corpus_co_occurrence_window, lexical_curated_relation,
-- etc.) pidgeonholed the universal substrate into a finite enumeration that
-- had to be extended every time a new modality / model mechanism / source
-- kind appeared. The (provenance × arena) tuple already discriminates
-- evidence by source and by domain — adding a third discrimination axis was
-- redundant AND broke the universal-substrate property because every new
-- source would need an attestation_type extension.
--
-- The substrate's invention rule: every claim about content from every
-- source is the same shape of evidence. ONE Glicko-2 attestation surface
-- (substrate.edge_significance + substrate.entity_significance), with
-- discrimination via:
--   * provenance_id   — WHICH source attested (wordnet / wiktionary / ud /
--                       tatoeba / each ingested AI model / user_session / etc.)
--   * context_type_id — IN WHICH arena (lexical_disambiguation /
--                       syntactic_role_fitness / domain-specific arenas / etc.)
--   * score           — Glicko-2 win/loss/draw (1.0 / 0.0 / 0.5)
--   * weight          — per-event weight magnitude (caller-controlled,
--                       defaults below)
--
-- attestation_type now carries ONLY the sign-bearing discriminator. The
-- column on substrate.edge_significance + substrate.entity_significance is
-- on the removal path (P1e) — once IngestionBatch.AddSignificance and all
-- decomposer callers stop threading it, the column will drop and these
-- three rows become unused infrastructure.
--
-- AP-31 (sign is load-bearing): Glicko score = value > 0 ? 1.0 : 0.0;
-- weight = Math.Abs(value). Caller emits positive_evidence with
-- score=1 OR negative_evidence with score=0; neutral_evidence with
-- score=0.5 widens sigma without moving mu (cross-source divergence /
-- inconclusive signal).
INSERT INTO substrate.attestation_type (code, description, default_event_weight) VALUES
    ('positive_evidence',
     'Sign-positive attestation event. score=1.0 in Glicko-2 update. weight = caller-supplied magnitude (default 1.0).',
     1.0),
    ('negative_evidence',
     'Sign-negative attestation event. score=0.0 in Glicko-2 update. Used for anti-correlation, suppression, antipodal, antonym, rejection-of-inference-path. weight = caller-supplied magnitude.',
     1.0),
    ('neutral_evidence',
     'Sign-neutral attestation event. score=0.5 in Glicko-2 update. Widens sigma without moving mu — cross-source divergence, inconclusive signal, multi-model disagreement. weight = caller-supplied magnitude.',
     0.5);

-- ── sql/schema/seed/tensor_role.sql ───────────────────────────────────────
INSERT INTO substrate.tensor_role (code) VALUES
    ('token_embedding'),
    ('token_type_embedding'),
    ('position_embedding'),
    ('position_embedding_2d'),
    ('rope_freq'),
    ('vq_codebook'),
    ('object_query'),
    ('anchor_grid'),
    ('attention_query'),
    ('attention_key'),
    ('attention_value'),
    ('attention_output'),
    ('cross_attention'),
    ('ffn_gate'),
    ('ffn_up'),
    ('ffn_down'),
    ('moe_router'),
    ('moe_expert_gate'),
    ('moe_expert_up'),
    ('moe_expert_down'),
    ('moe_shared_expert'),
    ('layer_norm'),
    ('batch_norm'),
    ('rms_norm'),
    ('logit_head'),
    ('class_head'),
    ('bbox_head'),
    ('conv_kernel'),
    ('diffusion_block'),
    ('vae_block'),
    ('conformer_layer'),
    ('mel_filterbank'),
    ('audio_codec_encoder'),
    ('audio_codec_decoder'),
    ('vision_feature'),
    ('vision_projection'),
    ('modality_projection'),
    ('lora_a'),
    ('lora_b'),
    ('codebook_scale'),
    ('fp8_scale')
ON CONFLICT (code) DO NOTHING;

-- ── sql/schema/seed/provenance.sql ───────────────────────────────────────
-- substrate.provenance seed — wide-band tier ladder.
--
-- Glicko-2 priors span 20K (user_session) to 100K (authoritative_standard).
-- Modality authority lives in substrate.provenance_modality (junction table
-- with composite PK + bidirectional indexes — no array columns in
-- substrate.*). derives_from + derivation_decay model lineage (OMW = 0.92
-- × WordNet).
--
-- Tier ladder rationale: cross-modal cross-source comparison only works
-- when a source's prior reflects its actual epistemic status. Flat 1500
-- priors made A* over arenas degenerate to uniform-cost BFS — the
-- topology was structurally absent from the substrate.
INSERT INTO substrate.provenance
    (code, curator_class, initial_mu, initial_sigma, derives_from, derivation_decay)
VALUES
    ('unicode_consortium',     'authoritative_standard', 100000,  50, NULL,                1.00),
    ('sil_international',      'authoritative_standard', 100000,  50, NULL,                1.00),
    ('library_of_congress',    'authoritative_standard', 100000,  50, NULL,                1.00),
    ('princeton_wordnet',      'academic_curated',        90000, 100, NULL,                1.00),
    ('omwn_consortium',        'academic_consortium',     85000, 100, 'princeton_wordnet', 0.92),
    ('universaldependencies',  'academic_consortium',     85000, 100, NULL,                1.00),
    ('wiktextract',            'community_curated',       70000, 200, NULL,                1.00),
    ('tatoeba',                'community_contributed',   50000, 350, NULL,                1.00),
    ('huggingface_model',      'model_derived',           60000, 350, NULL,                1.00),
    ('system_computed',        'system_computed',         40000, 350, NULL,                1.00),
    ('user_session',           'user_input',              20000, 500, NULL,                1.00),
    -- ISO / IETF / CLDR per-registry provenances (each is a separate publisher; cross-source consensus accumulates per arena)
    ('iso_15924',              'authoritative_standard',  95000, 100, NULL,                1.00),
    ('iso_3166',               'authoritative_standard',  95000, 100, NULL,                1.00),
    ('ietf_bcp47',             'authoritative_standard',  90000, 100, NULL,                1.00),
    ('cldr',                   'authoritative_standard',  70000, 200, NULL,                1.00),
    -- Encoding-standard provenances — each cross-encoding mapping attests independently
    ('ascii',                  'authoritative_standard',  95000, 100, NULL,                1.00),
    ('iso_8859_1',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_2',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_3',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_4',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_5',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_6',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_7',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_8',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_9',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_10',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_11',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_13',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_14',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_15',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('iso_8859_16',            'authoritative_standard',  90000, 150, NULL,                1.00),
    ('windows_1250',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1251',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1252',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1253',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1254',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1255',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1256',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1257',           'model_derived',           65000, 250, NULL,                1.00),
    ('windows_1258',           'model_derived',           65000, 250, NULL,                1.00),
    ('ebcdic_037',             'authoritative_standard',  80000, 200, NULL,                1.00),
    ('ebcdic_500',             'authoritative_standard',  80000, 200, NULL,                1.00),
    ('ebcdic_1047',            'authoritative_standard',  80000, 200, NULL,                1.00),
    ('koi8_r',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('koi8_u',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('gb18030',                'authoritative_standard',  95000, 100, NULL,                1.00),
    ('jis_x_0201',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('jis_x_0208',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('jis_x_0212',             'authoritative_standard',  90000, 150, NULL,                1.00),
    ('shift_jis',              'authoritative_standard',  85000, 200, NULL,                1.00),
    ('euc_jp',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('euc_kr',                 'authoritative_standard',  85000, 200, NULL,                1.00),
    ('big5',                   'authoritative_standard',  85000, 200, NULL,                1.00),
    ('mac_roman',              'model_derived',           60000, 300, NULL,                1.00),
    -- IVD collection provenances (5 collections per UTS #37)
    ('ivd_adobe_japan1',       'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_hanyo_denshi',       'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_krname',             'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_moji_joho',          'authoritative_standard',  85000, 150, NULL,                1.00),
    ('ivd_msarg',              'authoritative_standard',  85000, 150, NULL,                1.00),
    -- Unihan per-language reading provenances
    ('unihan_kmandarin',       'authoritative_standard',  90000, 150, NULL,                1.00),
    ('unihan_kcantonese',      'authoritative_standard',  90000, 150, NULL,                1.00),
    ('unihan_kjapanese',       'authoritative_standard',  90000, 150, NULL,                1.00),
    ('unihan_kvietnamese',     'authoritative_standard',  90000, 150, NULL,                1.00);

-- Modality authority per source — one junction row per (provenance, modality).
INSERT INTO substrate.provenance_modality (provenance_id, modality_code)
SELECT p.id, m.modality_code
  FROM substrate.provenance p
  JOIN (
      VALUES
        ('unicode_consortium',     'text'::substrate.modality_code),
        ('sil_international',      'text'::substrate.modality_code),
        ('library_of_congress',    'text'::substrate.modality_code),
        ('princeton_wordnet',      'text'::substrate.modality_code),
        ('omwn_consortium',        'text'::substrate.modality_code),
        ('universaldependencies',  'text'::substrate.modality_code),
        ('wiktextract',            'text'::substrate.modality_code),
        ('tatoeba',                'text'::substrate.modality_code),
        ('tatoeba',                'audio'::substrate.modality_code),
        ('huggingface_model',      'text'::substrate.modality_code),
        ('huggingface_model',      'model_weights'::substrate.modality_code),
        ('system_computed',        'text'::substrate.modality_code),
        ('system_computed',        'image'::substrate.modality_code),
        ('system_computed',        'audio'::substrate.modality_code),
        ('system_computed',        'video'::substrate.modality_code),
        ('system_computed',        'model_weights'::substrate.modality_code),
        ('user_session',           'text'::substrate.modality_code),
        ('user_session',           'image'::substrate.modality_code),
        ('user_session',           'audio'::substrate.modality_code),
        ('user_session',           'video'::substrate.modality_code),
        ('user_session',           'model_weights'::substrate.modality_code)
  ) AS m(code, modality_code)
    ON p.code = m.code;

-- ── sql/schema/seed/bidi_class.sql ───────────────────────────────────────
INSERT INTO substrate.bidi_class (id, code, description) VALUES
    (1,  'L',   'Left_To_Right'),
    (2,  'R',   'Right_To_Left'),
    (3,  'AL',  'Arabic_Letter'),
    (4,  'EN',  'European_Number'),
    (5,  'ES',  'European_Separator'),
    (6,  'ET',  'European_Terminator'),
    (7,  'AN',  'Arabic_Number'),
    (8,  'CS',  'Common_Separator'),
    (9,  'NSM', 'Nonspacing_Mark'),
    (10, 'BN',  'Boundary_Neutral'),
    (11, 'B',   'Paragraph_Separator'),
    (12, 'S',   'Segment_Separator'),
    (13, 'WS',  'White_Space'),
    (14, 'ON',  'Other_Neutral'),
    (15, 'LRE', 'Left_To_Right_Embedding'),
    (16, 'LRO', 'Left_To_Right_Override'),
    (17, 'RLE', 'Right_To_Left_Embedding'),
    (18, 'RLO', 'Right_To_Left_Override'),
    (19, 'PDF', 'Pop_Directional_Format'),
    (20, 'LRI', 'Left_To_Right_Isolate'),
    (21, 'RLI', 'Right_To_Left_Isolate'),
    (22, 'FSI', 'First_Strong_Isolate'),
    (23, 'PDI', 'Pop_Directional_Isolate')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    description = EXCLUDED.description;

SELECT setval('substrate.bidi_class_id_seq', (SELECT max(id) FROM substrate.bidi_class));

-- ── sql/schema/seed/east_asian_width.sql ───────────────────────────────────────
INSERT INTO substrate.east_asian_width (id, code, description) VALUES
    (1, 'N',  'Neutral'),
    (2, 'Na', 'Narrow'),
    (3, 'A',  'Ambiguous'),
    (4, 'W',  'Wide'),
    (5, 'F',  'Fullwidth'),
    (6, 'H',  'Halfwidth')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    description = EXCLUDED.description;

SELECT setval('substrate.east_asian_width_id_seq', (SELECT max(id) FROM substrate.east_asian_width));

-- ── sql/schema/seed/general_category.sql ───────────────────────────────────────
-- GENERATED — Unicode General_Category property (UAX #44).
-- Source: ext/hartonomous_pg/src/generated/pg_ucd_inventory.c (uc_inv_gc).
-- id = native-blob byte code + 1; matches substrate.codepoint_property FK convention.
INSERT INTO substrate.general_category (id, code, group_code, description) VALUES
    (1, 'Cn', 'C', 'Unassigned'),
    (2, 'Lu', 'L', 'Uppercase_Letter'),
    (3, 'Ll', 'L', 'Lowercase_Letter'),
    (4, 'Lt', 'L', 'Titlecase_Letter'),
    (5, 'Lm', 'L', 'Modifier_Letter'),
    (6, 'Lo', 'L', 'Other_Letter'),
    (7, 'Mn', 'M', 'Nonspacing_Mark'),
    (8, 'Mc', 'M', 'Spacing_Mark'),
    (9, 'Me', 'M', 'Enclosing_Mark'),
    (10, 'Nd', 'N', 'Decimal_Number'),
    (11, 'Nl', 'N', 'Letter_Number'),
    (12, 'No', 'N', 'Other_Number'),
    (13, 'Pc', 'P', 'Connector_Punctuation'),
    (14, 'Pd', 'P', 'Dash_Punctuation'),
    (15, 'Ps', 'P', 'Open_Punctuation'),
    (16, 'Pe', 'P', 'Close_Punctuation'),
    (17, 'Pi', 'P', 'Initial_Punctuation'),
    (18, 'Pf', 'P', 'Final_Punctuation'),
    (19, 'Po', 'P', 'Other_Punctuation'),
    (20, 'Sm', 'S', 'Math_Symbol'),
    (21, 'Sc', 'S', 'Currency_Symbol'),
    (22, 'Sk', 'S', 'Modifier_Symbol'),
    (23, 'So', 'S', 'Other_Symbol'),
    (24, 'Zs', 'Z', 'Space_Separator'),
    (25, 'Zl', 'Z', 'Line_Separator'),
    (26, 'Zp', 'Z', 'Paragraph_Separator'),
    (27, 'Cc', 'C', 'Control'),
    (28, 'Cf', 'C', 'Format'),
    (29, 'Cs', 'C', 'Surrogate'),
    (30, 'Co', 'C', 'Private_Use')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    group_code = EXCLUDED.group_code,
    description = EXCLUDED.description;

SELECT setval('substrate.general_category_id_seq', (SELECT max(id) FROM substrate.general_category));

-- ── sql/schema/seed/script.sql ───────────────────────────────────────
-- GENERATED — Unicode Script property (ISO 15924).
-- Source: ext/hartonomous_pg/src/generated/pg_ucd_inventory.c (uc_inv_scripts).
-- id = native-blob ushort code + 1; matches substrate.codepoint_property FK convention.
INSERT INTO substrate.script (id, code) VALUES
    (1, 'Unknown'),
    (2, 'Zyyy'),
    (3, 'Latn'),
    (4, 'Bopo'),
    (5, 'Zinh'),
    (6, 'Grek'),
    (7, 'Zzzz'),
    (8, 'Copt'),
    (9, 'Cyrl'),
    (10, 'Armn'),
    (11, 'Hebr'),
    (12, 'Arab'),
    (13, 'Syrc'),
    (14, 'Thaa'),
    (15, 'Nkoo'),
    (16, 'Samr'),
    (17, 'Mand'),
    (18, 'Deva'),
    (19, 'Beng'),
    (20, 'Guru'),
    (21, 'Gujr'),
    (22, 'Orya'),
    (23, 'Taml'),
    (24, 'Telu'),
    (25, 'Knda'),
    (26, 'Mlym'),
    (27, 'Sinh'),
    (28, 'Thai'),
    (29, 'Laoo'),
    (30, 'Tibt'),
    (31, 'Mymr'),
    (32, 'Geor'),
    (33, 'Hang'),
    (34, 'Ethi'),
    (35, 'Cher'),
    (36, 'Cans'),
    (37, 'Ogam'),
    (38, 'Runr'),
    (39, 'Tglg'),
    (40, 'Hano'),
    (41, 'Buhd'),
    (42, 'Tagb'),
    (43, 'Khmr'),
    (44, 'Mong'),
    (45, 'Limb'),
    (46, 'Tale'),
    (47, 'Talu'),
    (48, 'Bugi'),
    (49, 'Lana'),
    (50, 'Bali'),
    (51, 'Sund'),
    (52, 'Batk'),
    (53, 'Lepc'),
    (54, 'Olck'),
    (55, 'Brai'),
    (56, 'Glag'),
    (57, 'Tfng'),
    (58, 'Hani'),
    (59, 'Hira'),
    (60, 'Kana'),
    (61, 'Yiii'),
    (62, 'Lisu'),
    (63, 'Vaii'),
    (64, 'Bamu'),
    (65, 'Sylo'),
    (66, 'Phag'),
    (67, 'Saur'),
    (68, 'Kali'),
    (69, 'Rjng'),
    (70, 'Java'),
    (71, 'Cham'),
    (72, 'Tavt'),
    (73, 'Mtei'),
    (74, 'Linb'),
    (75, 'Lyci'),
    (76, 'Cari'),
    (77, 'Ital'),
    (78, 'Goth'),
    (79, 'Perm'),
    (80, 'Ugar'),
    (81, 'Xpeo'),
    (82, 'Dsrt'),
    (83, 'Shaw'),
    (84, 'Osma'),
    (85, 'Osge'),
    (86, 'Elba'),
    (87, 'Aghb'),
    (88, 'Vith'),
    (89, 'Todr'),
    (90, 'Lina'),
    (91, 'Cprt'),
    (92, 'Armi'),
    (93, 'Palm'),
    (94, 'Nbat'),
    (95, 'Hatr'),
    (96, 'Phnx'),
    (97, 'Lydi'),
    (98, 'Sidt'),
    (99, 'Mero'),
    (100, 'Merc'),
    (101, 'Khar'),
    (102, 'Sarb'),
    (103, 'Narb'),
    (104, 'Mani'),
    (105, 'Avst'),
    (106, 'Prti'),
    (107, 'Phli'),
    (108, 'Phlp'),
    (109, 'Orkh'),
    (110, 'Hung'),
    (111, 'Rohg'),
    (112, 'Gara'),
    (113, 'Yezi'),
    (114, 'Sogo'),
    (115, 'Sogd'),
    (116, 'Ougr'),
    (117, 'Chrs'),
    (118, 'Elym'),
    (119, 'Brah'),
    (120, 'Kthi'),
    (121, 'Sora'),
    (122, 'Cakm'),
    (123, 'Mahj'),
    (124, 'Shrd'),
    (125, 'Khoj'),
    (126, 'Mult'),
    (127, 'Sind'),
    (128, 'Gran'),
    (129, 'Tutg'),
    (130, 'Newa'),
    (131, 'Tirh'),
    (132, 'Sidd'),
    (133, 'Modi'),
    (134, 'Takr'),
    (135, 'Ahom'),
    (136, 'Dogr'),
    (137, 'Wara'),
    (138, 'Diak'),
    (139, 'Nand'),
    (140, 'Zanb'),
    (141, 'Soyo'),
    (142, 'Pauc'),
    (143, 'Sunu'),
    (144, 'Bhks'),
    (145, 'Marc'),
    (146, 'Gonm'),
    (147, 'Gong'),
    (148, 'Tols'),
    (149, 'Maka'),
    (150, 'Kawi'),
    (151, 'Xsux'),
    (152, 'Cpmn'),
    (153, 'Egyp'),
    (154, 'Hluw'),
    (155, 'Gukh'),
    (156, 'Mroo'),
    (157, 'Tnsa'),
    (158, 'Bass'),
    (159, 'Hmng'),
    (160, 'Krai'),
    (161, 'Medf'),
    (162, 'Berf'),
    (163, 'Plrd'),
    (164, 'Tang'),
    (165, 'Nshu'),
    (166, 'Kits'),
    (167, 'Dupl'),
    (168, 'Sgnw'),
    (169, 'Hmnp'),
    (170, 'Toto'),
    (171, 'Wcho'),
    (172, 'Nagm'),
    (173, 'Onao'),
    (174, 'Tayo'),
    (175, 'Mend'),
    (176, 'Adlm')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code;

SELECT setval('substrate.script_id_seq', (SELECT max(id) FROM substrate.script));

-- ── sql/schema/seed/block.sql ───────────────────────────────────────
-- GENERATED — Unicode Block ranges.
-- Source: ext/hartonomous_pg/src/generated/pg_ucd_inventory.c (uc_inv_blocks).
-- id = native-blob ushort code + 1; matches substrate.codepoint_property FK convention.
INSERT INTO substrate.block (id, code, range_start, range_end) VALUES
    (1, 'No_Block', 0, 0),
    (2, 'Basic Latin', 0, 127),
    (3, 'Latin-1 Supplement', 128, 255),
    (4, 'Latin Extended-A', 256, 383),
    (5, 'Latin Extended-B', 384, 591),
    (6, 'IPA Extensions', 592, 687),
    (7, 'Spacing Modifier Letters', 688, 767),
    (8, 'Combining Diacritical Marks', 768, 879),
    (9, 'Greek and Coptic', 880, 1023),
    (10, 'Cyrillic', 1024, 1279),
    (11, 'Cyrillic Supplement', 1280, 1327),
    (12, 'Armenian', 1328, 1423),
    (13, 'Hebrew', 1424, 1535),
    (14, 'Arabic', 1536, 1791),
    (15, 'Syriac', 1792, 1871),
    (16, 'Arabic Supplement', 1872, 1919),
    (17, 'Thaana', 1920, 1983),
    (18, 'NKo', 1984, 2047),
    (19, 'Samaritan', 2048, 2111),
    (20, 'Mandaic', 2112, 2143),
    (21, 'Syriac Supplement', 2144, 2159),
    (22, 'Arabic Extended-B', 2160, 2207),
    (23, 'Arabic Extended-A', 2208, 2303),
    (24, 'Devanagari', 2304, 2431),
    (25, 'Bengali', 2432, 2559),
    (26, 'Gurmukhi', 2560, 2687),
    (27, 'Gujarati', 2688, 2815),
    (28, 'Oriya', 2816, 2943),
    (29, 'Tamil', 2944, 3071),
    (30, 'Telugu', 3072, 3199),
    (31, 'Kannada', 3200, 3327),
    (32, 'Malayalam', 3328, 3455),
    (33, 'Sinhala', 3456, 3583),
    (34, 'Thai', 3584, 3711),
    (35, 'Lao', 3712, 3839),
    (36, 'Tibetan', 3840, 4095),
    (37, 'Myanmar', 4096, 4255),
    (38, 'Georgian', 4256, 4351),
    (39, 'Hangul Jamo', 4352, 4607),
    (40, 'Ethiopic', 4608, 4991),
    (41, 'Ethiopic Supplement', 4992, 5023),
    (42, 'Cherokee', 5024, 5119),
    (43, 'Unified Canadian Aboriginal Syllabics', 5120, 5759),
    (44, 'Ogham', 5760, 5791),
    (45, 'Runic', 5792, 5887),
    (46, 'Tagalog', 5888, 5919),
    (47, 'Hanunoo', 5920, 5951),
    (48, 'Buhid', 5952, 5983),
    (49, 'Tagbanwa', 5984, 6015),
    (50, 'Khmer', 6016, 6143),
    (51, 'Mongolian', 6144, 6319),
    (52, 'Unified Canadian Aboriginal Syllabics Extended', 6320, 6399),
    (53, 'Limbu', 6400, 6479),
    (54, 'Tai Le', 6480, 6527),
    (55, 'New Tai Lue', 6528, 6623),
    (56, 'Khmer Symbols', 6624, 6655),
    (57, 'Buginese', 6656, 6687),
    (58, 'Tai Tham', 6688, 6831),
    (59, 'Combining Diacritical Marks Extended', 6832, 6911),
    (60, 'Balinese', 6912, 7039),
    (61, 'Sundanese', 7040, 7103),
    (62, 'Batak', 7104, 7167),
    (63, 'Lepcha', 7168, 7247),
    (64, 'Ol Chiki', 7248, 7295),
    (65, 'Cyrillic Extended-C', 7296, 7311),
    (66, 'Georgian Extended', 7312, 7359),
    (67, 'Sundanese Supplement', 7360, 7375),
    (68, 'Vedic Extensions', 7376, 7423),
    (69, 'Phonetic Extensions', 7424, 7551),
    (70, 'Phonetic Extensions Supplement', 7552, 7615),
    (71, 'Combining Diacritical Marks Supplement', 7616, 7679),
    (72, 'Latin Extended Additional', 7680, 7935),
    (73, 'Greek Extended', 7936, 8191),
    (74, 'General Punctuation', 8192, 8303),
    (75, 'Superscripts and Subscripts', 8304, 8351),
    (76, 'Currency Symbols', 8352, 8399),
    (77, 'Combining Diacritical Marks for Symbols', 8400, 8447),
    (78, 'Letterlike Symbols', 8448, 8527),
    (79, 'Number Forms', 8528, 8591),
    (80, 'Arrows', 8592, 8703),
    (81, 'Mathematical Operators', 8704, 8959),
    (82, 'Miscellaneous Technical', 8960, 9215),
    (83, 'Control Pictures', 9216, 9279),
    (84, 'Optical Character Recognition', 9280, 9311),
    (85, 'Enclosed Alphanumerics', 9312, 9471),
    (86, 'Box Drawing', 9472, 9599),
    (87, 'Block Elements', 9600, 9631),
    (88, 'Geometric Shapes', 9632, 9727),
    (89, 'Miscellaneous Symbols', 9728, 9983),
    (90, 'Dingbats', 9984, 10175),
    (91, 'Miscellaneous Mathematical Symbols-A', 10176, 10223),
    (92, 'Supplemental Arrows-A', 10224, 10239),
    (93, 'Braille Patterns', 10240, 10495),
    (94, 'Supplemental Arrows-B', 10496, 10623),
    (95, 'Miscellaneous Mathematical Symbols-B', 10624, 10751),
    (96, 'Supplemental Mathematical Operators', 10752, 11007),
    (97, 'Miscellaneous Symbols and Arrows', 11008, 11263),
    (98, 'Glagolitic', 11264, 11359),
    (99, 'Latin Extended-C', 11360, 11391),
    (100, 'Coptic', 11392, 11519),
    (101, 'Georgian Supplement', 11520, 11567),
    (102, 'Tifinagh', 11568, 11647),
    (103, 'Ethiopic Extended', 11648, 11743),
    (104, 'Cyrillic Extended-A', 11744, 11775),
    (105, 'Supplemental Punctuation', 11776, 11903),
    (106, 'CJK Radicals Supplement', 11904, 12031),
    (107, 'Kangxi Radicals', 12032, 12255),
    (108, 'Ideographic Description Characters', 12272, 12287),
    (109, 'CJK Symbols and Punctuation', 12288, 12351),
    (110, 'Hiragana', 12352, 12447),
    (111, 'Katakana', 12448, 12543),
    (112, 'Bopomofo', 12544, 12591),
    (113, 'Hangul Compatibility Jamo', 12592, 12687),
    (114, 'Kanbun', 12688, 12703),
    (115, 'Bopomofo Extended', 12704, 12735),
    (116, 'CJK Strokes', 12736, 12783),
    (117, 'Katakana Phonetic Extensions', 12784, 12799),
    (118, 'Enclosed CJK Letters and Months', 12800, 13055),
    (119, 'CJK Compatibility', 13056, 13311),
    (120, 'CJK Unified Ideographs Extension A', 13312, 19903),
    (121, 'Yijing Hexagram Symbols', 19904, 19967),
    (122, 'CJK Unified Ideographs', 19968, 40959),
    (123, 'Yi Syllables', 40960, 42127),
    (124, 'Yi Radicals', 42128, 42191),
    (125, 'Lisu', 42192, 42239),
    (126, 'Vai', 42240, 42559),
    (127, 'Cyrillic Extended-B', 42560, 42655),
    (128, 'Bamum', 42656, 42751),
    (129, 'Modifier Tone Letters', 42752, 42783),
    (130, 'Latin Extended-D', 42784, 43007),
    (131, 'Syloti Nagri', 43008, 43055),
    (132, 'Common Indic Number Forms', 43056, 43071),
    (133, 'Phags-pa', 43072, 43135),
    (134, 'Saurashtra', 43136, 43231),
    (135, 'Devanagari Extended', 43232, 43263),
    (136, 'Kayah Li', 43264, 43311),
    (137, 'Rejang', 43312, 43359),
    (138, 'Hangul Jamo Extended-A', 43360, 43391),
    (139, 'Javanese', 43392, 43487),
    (140, 'Myanmar Extended-B', 43488, 43519),
    (141, 'Cham', 43520, 43615),
    (142, 'Myanmar Extended-A', 43616, 43647),
    (143, 'Tai Viet', 43648, 43743),
    (144, 'Meetei Mayek Extensions', 43744, 43775),
    (145, 'Ethiopic Extended-A', 43776, 43823),
    (146, 'Latin Extended-E', 43824, 43887),
    (147, 'Cherokee Supplement', 43888, 43967),
    (148, 'Meetei Mayek', 43968, 44031),
    (149, 'Hangul Syllables', 44032, 55215),
    (150, 'Hangul Jamo Extended-B', 55216, 55295),
    (151, 'High Surrogates', 55296, 56191),
    (152, 'High Private Use Surrogates', 56192, 56319),
    (153, 'Low Surrogates', 56320, 57343),
    (154, 'Private Use Area', 57344, 63743),
    (155, 'CJK Compatibility Ideographs', 63744, 64255),
    (156, 'Alphabetic Presentation Forms', 64256, 64335),
    (157, 'Arabic Presentation Forms-A', 64336, 65023),
    (158, 'Variation Selectors', 65024, 65039),
    (159, 'Vertical Forms', 65040, 65055),
    (160, 'Combining Half Marks', 65056, 65071),
    (161, 'CJK Compatibility Forms', 65072, 65103),
    (162, 'Small Form Variants', 65104, 65135),
    (163, 'Arabic Presentation Forms-B', 65136, 65279),
    (164, 'Halfwidth and Fullwidth Forms', 65280, 65519),
    (165, 'Specials', 65520, 65535),
    (166, 'Linear B Syllabary', 65536, 65663),
    (167, 'Linear B Ideograms', 65664, 65791),
    (168, 'Aegean Numbers', 65792, 65855),
    (169, 'Ancient Greek Numbers', 65856, 65935),
    (170, 'Ancient Symbols', 65936, 65999),
    (171, 'Phaistos Disc', 66000, 66047),
    (172, 'Lycian', 66176, 66207),
    (173, 'Carian', 66208, 66271),
    (174, 'Coptic Epact Numbers', 66272, 66303),
    (175, 'Old Italic', 66304, 66351),
    (176, 'Gothic', 66352, 66383),
    (177, 'Old Permic', 66384, 66431),
    (178, 'Ugaritic', 66432, 66463),
    (179, 'Old Persian', 66464, 66527),
    (180, 'Deseret', 66560, 66639),
    (181, 'Shavian', 66640, 66687),
    (182, 'Osmanya', 66688, 66735),
    (183, 'Osage', 66736, 66815),
    (184, 'Elbasan', 66816, 66863),
    (185, 'Caucasian Albanian', 66864, 66927),
    (186, 'Vithkuqi', 66928, 67007),
    (187, 'Todhri', 67008, 67071),
    (188, 'Linear A', 67072, 67455),
    (189, 'Latin Extended-F', 67456, 67519),
    (190, 'Cypriot Syllabary', 67584, 67647),
    (191, 'Imperial Aramaic', 67648, 67679),
    (192, 'Palmyrene', 67680, 67711),
    (193, 'Nabataean', 67712, 67759),
    (194, 'Hatran', 67808, 67839),
    (195, 'Phoenician', 67840, 67871),
    (196, 'Lydian', 67872, 67903),
    (197, 'Sidetic', 67904, 67935),
    (198, 'Meroitic Hieroglyphs', 67968, 67999),
    (199, 'Meroitic Cursive', 68000, 68095),
    (200, 'Kharoshthi', 68096, 68191),
    (201, 'Old South Arabian', 68192, 68223),
    (202, 'Old North Arabian', 68224, 68255),
    (203, 'Manichaean', 68288, 68351),
    (204, 'Avestan', 68352, 68415),
    (205, 'Inscriptional Parthian', 68416, 68447),
    (206, 'Inscriptional Pahlavi', 68448, 68479),
    (207, 'Psalter Pahlavi', 68480, 68527),
    (208, 'Old Turkic', 68608, 68687),
    (209, 'Old Hungarian', 68736, 68863),
    (210, 'Hanifi Rohingya', 68864, 68927),
    (211, 'Garay', 68928, 69007),
    (212, 'Rumi Numeral Symbols', 69216, 69247),
    (213, 'Yezidi', 69248, 69311),
    (214, 'Arabic Extended-C', 69312, 69375),
    (215, 'Old Sogdian', 69376, 69423),
    (216, 'Sogdian', 69424, 69487),
    (217, 'Old Uyghur', 69488, 69551),
    (218, 'Chorasmian', 69552, 69599),
    (219, 'Elymaic', 69600, 69631),
    (220, 'Brahmi', 69632, 69759),
    (221, 'Kaithi', 69760, 69839),
    (222, 'Sora Sompeng', 69840, 69887),
    (223, 'Chakma', 69888, 69967),
    (224, 'Mahajani', 69968, 70015),
    (225, 'Sharada', 70016, 70111),
    (226, 'Sinhala Archaic Numbers', 70112, 70143),
    (227, 'Khojki', 70144, 70223),
    (228, 'Multani', 70272, 70319),
    (229, 'Khudawadi', 70320, 70399),
    (230, 'Grantha', 70400, 70527),
    (231, 'Tulu-Tigalari', 70528, 70655),
    (232, 'Newa', 70656, 70783),
    (233, 'Tirhuta', 70784, 70879),
    (234, 'Siddham', 71040, 71167),
    (235, 'Modi', 71168, 71263),
    (236, 'Mongolian Supplement', 71264, 71295),
    (237, 'Takri', 71296, 71375),
    (238, 'Myanmar Extended-C', 71376, 71423),
    (239, 'Ahom', 71424, 71503),
    (240, 'Dogra', 71680, 71759),
    (241, 'Warang Citi', 71840, 71935),
    (242, 'Dives Akuru', 71936, 72031),
    (243, 'Nandinagari', 72096, 72191),
    (244, 'Zanabazar Square', 72192, 72271),
    (245, 'Soyombo', 72272, 72367),
    (246, 'Unified Canadian Aboriginal Syllabics Extended-A', 72368, 72383),
    (247, 'Pau Cin Hau', 72384, 72447),
    (248, 'Devanagari Extended-A', 72448, 72543),
    (249, 'Sharada Supplement', 72544, 72575),
    (250, 'Sunuwar', 72640, 72703),
    (251, 'Bhaiksuki', 72704, 72815),
    (252, 'Marchen', 72816, 72895),
    (253, 'Masaram Gondi', 72960, 73055),
    (254, 'Gunjala Gondi', 73056, 73135),
    (255, 'Tolong Siki', 73136, 73199),
    (256, 'Makasar', 73440, 73471),
    (257, 'Kawi', 73472, 73567),
    (258, 'Lisu Supplement', 73648, 73663),
    (259, 'Tamil Supplement', 73664, 73727),
    (260, 'Cuneiform', 73728, 74751),
    (261, 'Cuneiform Numbers and Punctuation', 74752, 74879),
    (262, 'Early Dynastic Cuneiform', 74880, 75087),
    (263, 'Cypro-Minoan', 77712, 77823),
    (264, 'Egyptian Hieroglyphs', 77824, 78895),
    (265, 'Egyptian Hieroglyph Format Controls', 78896, 78943),
    (266, 'Egyptian Hieroglyphs Extended-A', 78944, 82943),
    (267, 'Anatolian Hieroglyphs', 82944, 83583),
    (268, 'Gurung Khema', 90368, 90431),
    (269, 'Bamum Supplement', 92160, 92735),
    (270, 'Mro', 92736, 92783),
    (271, 'Tangsa', 92784, 92879),
    (272, 'Bassa Vah', 92880, 92927),
    (273, 'Pahawh Hmong', 92928, 93071),
    (274, 'Kirat Rai', 93504, 93567),
    (275, 'Medefaidrin', 93760, 93855),
    (276, 'Beria Erfe', 93856, 93919),
    (277, 'Miao', 93952, 94111),
    (278, 'Ideographic Symbols and Punctuation', 94176, 94207),
    (279, 'Tangut', 94208, 100351),
    (280, 'Tangut Components', 100352, 101119),
    (281, 'Khitan Small Script', 101120, 101631),
    (282, 'Tangut Supplement', 101632, 101759),
    (283, 'Tangut Components Supplement', 101760, 101887),
    (284, 'Kana Extended-B', 110576, 110591),
    (285, 'Kana Supplement', 110592, 110847),
    (286, 'Kana Extended-A', 110848, 110895),
    (287, 'Small Kana Extension', 110896, 110959),
    (288, 'Nushu', 110960, 111359),
    (289, 'Duployan', 113664, 113823),
    (290, 'Shorthand Format Controls', 113824, 113839),
    (291, 'Symbols for Legacy Computing Supplement', 117760, 118463),
    (292, 'Miscellaneous Symbols Supplement', 118464, 118527),
    (293, 'Znamenny Musical Notation', 118528, 118735),
    (294, 'Byzantine Musical Symbols', 118784, 119039),
    (295, 'Musical Symbols', 119040, 119295),
    (296, 'Ancient Greek Musical Notation', 119296, 119375),
    (297, 'Kaktovik Numerals', 119488, 119519),
    (298, 'Mayan Numerals', 119520, 119551),
    (299, 'Tai Xuan Jing Symbols', 119552, 119647),
    (300, 'Counting Rod Numerals', 119648, 119679),
    (301, 'Mathematical Alphanumeric Symbols', 119808, 120831),
    (302, 'Sutton SignWriting', 120832, 121519),
    (303, 'Latin Extended-G', 122624, 122879),
    (304, 'Glagolitic Supplement', 122880, 122927),
    (305, 'Cyrillic Extended-D', 122928, 123023),
    (306, 'Nyiakeng Puachue Hmong', 123136, 123215),
    (307, 'Toto', 123536, 123583),
    (308, 'Wancho', 123584, 123647),
    (309, 'Nag Mundari', 124112, 124159),
    (310, 'Ol Onal', 124368, 124415),
    (311, 'Tai Yo', 124608, 124671),
    (312, 'Ethiopic Extended-B', 124896, 124927),
    (313, 'Mende Kikakui', 124928, 125151),
    (314, 'Adlam', 125184, 125279),
    (315, 'Indic Siyaq Numbers', 126064, 126143),
    (316, 'Ottoman Siyaq Numbers', 126208, 126287),
    (317, 'Arabic Mathematical Alphabetic Symbols', 126464, 126719),
    (318, 'Mahjong Tiles', 126976, 127023),
    (319, 'Domino Tiles', 127024, 127135),
    (320, 'Playing Cards', 127136, 127231),
    (321, 'Enclosed Alphanumeric Supplement', 127232, 127487),
    (322, 'Enclosed Ideographic Supplement', 127488, 127743),
    (323, 'Miscellaneous Symbols and Pictographs', 127744, 128511),
    (324, 'Emoticons', 128512, 128591),
    (325, 'Ornamental Dingbats', 128592, 128639),
    (326, 'Transport and Map Symbols', 128640, 128767),
    (327, 'Alchemical Symbols', 128768, 128895),
    (328, 'Geometric Shapes Extended', 128896, 129023),
    (329, 'Supplemental Arrows-C', 129024, 129279),
    (330, 'Supplemental Symbols and Pictographs', 129280, 129535),
    (331, 'Chess Symbols', 129536, 129647),
    (332, 'Symbols and Pictographs Extended-A', 129648, 129791),
    (333, 'Symbols for Legacy Computing', 129792, 130047),
    (334, 'CJK Unified Ideographs Extension B', 131072, 173791),
    (335, 'CJK Unified Ideographs Extension C', 173824, 177983),
    (336, 'CJK Unified Ideographs Extension D', 177984, 178207),
    (337, 'CJK Unified Ideographs Extension E', 178208, 183983),
    (338, 'CJK Unified Ideographs Extension F', 183984, 191471),
    (339, 'CJK Unified Ideographs Extension I', 191472, 192095),
    (340, 'CJK Compatibility Ideographs Supplement', 194560, 195103),
    (341, 'CJK Unified Ideographs Extension G', 196608, 201551),
    (342, 'CJK Unified Ideographs Extension H', 201552, 205743),
    (343, 'CJK Unified Ideographs Extension J', 205744, 210047),
    (344, 'Tags', 917504, 917631),
    (345, 'Variation Selectors Supplement', 917760, 917999),
    (346, 'Supplementary Private Use Area-A', 983040, 1048575),
    (347, 'Supplementary Private Use Area-B', 1048576, 1114111)
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    range_start = EXCLUDED.range_start,
    range_end = EXCLUDED.range_end;

SELECT setval('substrate.block_id_seq', (SELECT max(id) FROM substrate.block));

-- ── sql/schema/seed/break_property.sql ───────────────────────────────────────
-- GENERATED — Unicode segmentation break properties (UAX #14 / UAX #29).
-- Source: ext/hartonomous_pg/src/generated/pg_ucd_inventory.c (uc_inv_break_props).
-- id is a serial 1-based; enum_id is the per-category native-blob byte code
-- (UC_GCB_*, UC_WB_*, UC_SB_*, UC_LB_*, UC_INCB_* — robust against ID-offset
-- drift when UCD versions add or reorder enum values per the table comment).
INSERT INTO substrate.break_property (id, code, category, enum_id) VALUES
    (1, 'Other', 'GCB', 0),
    (2, 'CR', 'GCB', 1),
    (3, 'LF', 'GCB', 2),
    (4, 'Control', 'GCB', 3),
    (5, 'Extend', 'GCB', 4),
    (6, 'ZWJ', 'GCB', 5),
    (7, 'Regional_Indicator', 'GCB', 6),
    (8, 'Prepend', 'GCB', 7),
    (9, 'SpacingMark', 'GCB', 8),
    (10, 'L', 'GCB', 9),
    (11, 'V', 'GCB', 10),
    (12, 'T', 'GCB', 11),
    (13, 'LV', 'GCB', 12),
    (14, 'LVT', 'GCB', 13),
    (15, 'Other', 'WB', 0),
    (16, 'CR', 'WB', 1),
    (17, 'LF', 'WB', 2),
    (18, 'Newline', 'WB', 3),
    (19, 'Extend', 'WB', 4),
    (20, 'ZWJ', 'WB', 5),
    (21, 'Format', 'WB', 6),
    (22, 'Katakana', 'WB', 7),
    (23, 'Hebrew_Letter', 'WB', 8),
    (24, 'ALetter', 'WB', 9),
    (25, 'Single_Quote', 'WB', 10),
    (26, 'Double_Quote', 'WB', 11),
    (27, 'MidNumLet', 'WB', 12),
    (28, 'MidLetter', 'WB', 13),
    (29, 'MidNum', 'WB', 14),
    (30, 'Numeric', 'WB', 15),
    (31, 'ExtendNumLet', 'WB', 16),
    (32, 'Regional_Indicator', 'WB', 17),
    (33, 'WSegSpace', 'WB', 18),
    (34, 'Extended_Pictographic', 'WB', 19),
    (35, 'Other', 'SB', 0),
    (36, 'CR', 'SB', 1),
    (37, 'LF', 'SB', 2),
    (38, 'Sep', 'SB', 3),
    (39, 'Format', 'SB', 4),
    (40, 'Sp', 'SB', 5),
    (41, 'Lower', 'SB', 6),
    (42, 'Upper', 'SB', 7),
    (43, 'OLetter', 'SB', 8),
    (44, 'Numeric', 'SB', 9),
    (45, 'ATerm', 'SB', 10),
    (46, 'STerm', 'SB', 11),
    (47, 'Close', 'SB', 12),
    (48, 'SContinue', 'SB', 13),
    (49, 'Extend', 'SB', 14),
    (50, 'XX', 'LB', 0),
    (51, 'BK', 'LB', 1),
    (52, 'CR', 'LB', 2),
    (53, 'LF', 'LB', 3),
    (54, 'CM', 'LB', 4),
    (55, 'NL', 'LB', 5),
    (56, 'SG', 'LB', 6),
    (57, 'WJ', 'LB', 7),
    (58, 'ZW', 'LB', 8),
    (59, 'GL', 'LB', 9),
    (60, 'SP', 'LB', 10),
    (61, 'B2', 'LB', 11),
    (62, 'BA', 'LB', 12),
    (63, 'BB', 'LB', 13),
    (64, 'HY', 'LB', 14),
    (65, 'CB', 'LB', 15),
    (66, 'CL', 'LB', 16),
    (67, 'CP', 'LB', 17),
    (68, 'EX', 'LB', 18),
    (69, 'IN', 'LB', 19),
    (70, 'NS', 'LB', 20),
    (71, 'OP', 'LB', 21),
    (72, 'QU', 'LB', 22),
    (73, 'IS', 'LB', 23),
    (74, 'NU', 'LB', 24),
    (75, 'PO', 'LB', 25),
    (76, 'PR', 'LB', 26),
    (77, 'SY', 'LB', 27),
    (78, 'AI', 'LB', 28),
    (79, 'AL', 'LB', 29),
    (80, 'CJ', 'LB', 30),
    (81, 'EB', 'LB', 31),
    (82, 'EM', 'LB', 32),
    (83, 'H2', 'LB', 33),
    (84, 'H3', 'LB', 34),
    (85, 'HL', 'LB', 35),
    (86, 'ID', 'LB', 36),
    (87, 'JL', 'LB', 37),
    (88, 'JV', 'LB', 38),
    (89, 'JT', 'LB', 39),
    (90, 'RI', 'LB', 40),
    (91, 'SA', 'LB', 41),
    (92, 'ZWJ', 'LB', 42),
    (93, 'AK', 'LB', 43),
    (94, 'AP', 'LB', 44),
    (95, 'AS', 'LB', 45),
    (96, 'VF', 'LB', 46),
    (97, 'VI', 'LB', 47),
    (98, 'None', 'InCB', 0),
    (99, 'Linker', 'InCB', 1),
    (100, 'Extend', 'InCB', 2),
    (101, 'Consonant', 'InCB', 3)
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    category = EXCLUDED.category,
    enum_id = EXCLUDED.enum_id;

SELECT setval('substrate.break_property_id_seq', (SELECT max(id) FROM substrate.break_property));

-- ── sql/schema/seed/lexname.sql ───────────────────────────────────────
-- 45 WordNet lexicographer categories.
INSERT INTO substrate.lexname (code) VALUES
    ('adj.all'), ('adj.pert'), ('adj.ppl'),
    ('adv.all'),
    ('noun.Tops'), ('noun.act'), ('noun.animal'), ('noun.artifact'), ('noun.attribute'),
    ('noun.body'), ('noun.cognition'), ('noun.communication'), ('noun.event'),
    ('noun.feeling'), ('noun.food'), ('noun.group'), ('noun.location'), ('noun.motive'),
    ('noun.object'), ('noun.person'), ('noun.phenomenon'), ('noun.plant'),
    ('noun.possession'), ('noun.process'), ('noun.quantity'), ('noun.relation'),
    ('noun.shape'), ('noun.state'), ('noun.substance'), ('noun.time'),
    ('verb.body'), ('verb.change'), ('verb.cognition'), ('verb.communication'),
    ('verb.competition'), ('verb.consumption'), ('verb.contact'), ('verb.creation'),
    ('verb.emotion'), ('verb.motion'), ('verb.perception'), ('verb.possession'),
    ('verb.social'), ('verb.stative'), ('verb.weather');

-- ── sql/schema/seed/pos.sql ───────────────────────────────────────
-- 17 universal POS tags (UPOS).
INSERT INTO substrate.pos (code, parent_id) VALUES
    ('ADJ',   NULL), ('ADP',   NULL), ('ADV',   NULL), ('AUX',   NULL),
    ('CCONJ', NULL), ('DET',   NULL), ('INTJ',  NULL), ('NOUN',  NULL),
    ('NUM',   NULL), ('PART',  NULL), ('PRON',  NULL), ('PROPN', NULL),
    ('PUNCT', NULL), ('SCONJ', NULL), ('SYM',   NULL), ('VERB',  NULL),
    ('X',     NULL);

-- ── sql/schema/seed/edge_type.sql ───────────────────────────────────────
-- Edge types. Single INSERT...SELECT pattern: tuples in a VALUES CTE,
-- resolved against substrate.entity_type via JOIN. NULL source/target codes
-- mean polymorphic.
--
-- semantic_weight is a structural prior on the relation strength used by
-- engine traversal as a tie-breaker; arena-bound Glicko mu on
-- substrate.edge_significance is the dynamic weight.
--
-- Categories:
--   structural    — within-modality structural composition (text)
--   cross_lingual — between language entities
--   cross_modal   — between content-entity-types of different modalities
--   unicode       — codepoint-level Unicode tables
--   model_derived — model-package metadata + content-entity attestations
--                   produced by safetensors decomposers (per docs/01-tensor-
--                   primitive-spec.md §IV)
--   semantic      — WordNet / Wiktionary semantic relations between synsets
--                   and lemmas
--
-- Per docs/01-tensor-primitive-spec.md: there is no has_<phantom> edge type
-- pointing to a phantom entity. Per-tuple attestations land on edges between
-- content entities; per-tensor analytics live as physicality on the tensor
-- entity. The model_derived edges below are EXACTLY:
--   * Architecture metadata (in_model, in_layer, has_dtype, has_shape,
--     has_hidden_size, has_num_layers, has_num_attention_heads, has_vocab_size,
--     has_token_id, in_vocabulary, has_tensor, has_architecture_name,
--     has_tensor_name, has_tokenizer_model, has_token_in_tokenizer)
--   * Token↔token attestation surfaces (model_concept_similarity,
--     model_attention_pattern, model_ffn_factor)
--   * Cross-content attestation surfaces (model_cross_modal_pattern,
--     model_spatial_pattern, model_detection_class)
--   * Vocab-coverage join (covers_lemma)
--   * co_occurrence (polymorphic — used by corpus-window decomposers)

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id, semantic_weight)
SELECT
    s.code,
    s.category,
    src.id,
    tgt.id,
    CASE
        WHEN s.code IN (
            'member_holonym', 'substance_holonym', 'part_holonym',
            'member_meronym', 'substance_meronym', 'part_meronym', 'has_morpheme'
        ) THEN 0.9
        WHEN s.code IN (
            'translation_of', 'aligned_to_synset', 'translation_link'
        ) THEN 0.85
        WHEN s.code IN (
            'has_etymology', 'has_pronunciation', 'has_hyphenation', 'has_wikidata'
        ) THEN 0.7
        WHEN s.code IN (
            'similar_to', 'also_see', 'verb_group', 'attribute', 'derivationally_related'
        ) THEN 0.6
        WHEN s.code IN (
            'synonym', 'related', 'coordinate_term', 'derived'
        ) THEN 0.5
        ELSE 1.0
    END AS semantic_weight
FROM (VALUES
    -- ── Structural (within text modality) ──────────────────────────────
    ('has_sense',                'structural',    'lemma',              'synset'),              --  1
    ('has_form',                 'structural',    'lemma',              'word_form'),           --  2
    ('has_lemma',                'structural',    'word_form',          'lemma'),               --  3
    ('has_morpheme',             'structural',    'word_form',          'morpheme'),            --  4
    ('has_gloss',                'structural',    'synset',             'text_composition'),    --  5
    ('has_example',              'structural',    'synset',             'text_composition'),    --  6
    ('has_name',                 'structural',    'model_architecture', 'text_composition'),    --  7
    ('inflection_of',            'structural',    'word_form',          'lemma'),               --  8
    ('has_etymology',            'structural',    'lemma',              'text_composition'),    --  9
    ('has_pronunciation',        'structural',    'lemma',              'text_composition'),    -- 10
    ('has_hyphenation',          'structural',    'lemma',              'text_composition'),    -- 11
    ('has_wikidata',             'structural',    'lemma',              'text_composition'),    -- 12
    ('lexicalized_compound',     'structural',    'word_form',          'word_form'),           -- 13
    ('has_frame',                'structural',    'lemma',              'text_composition'),    -- 14
    -- ── Cross-lingual ──────────────────────────────────────────────────
    ('aligned_to_synset',        'cross_lingual', 'lemma',              'synset'),              -- 16
    ('translation_of',           'cross_lingual', 'lemma',              'lemma'),               -- 17
    ('translation_link',         'cross_lingual', 'text_composition',   'text_composition'),    -- 18
    ('macrolanguage_contains',   'cross_lingual', 'language_name',      'language_name'),       -- 19
    ('has_alternate_name',       'cross_lingual', 'language_name',      'language_name'),       -- 20
    ('superseded_by',            'cross_lingual', 'language_name',      'language_name'),       -- 21
    ('etym_inherited_from',      'cross_lingual', 'lemma',              'lemma'),               -- 22
    ('etym_derived_from',        'cross_lingual', 'lemma',              'lemma'),               -- 23
    ('etym_borrowed_from',       'cross_lingual', 'lemma',              'lemma'),               -- 24
    ('etym_cognate_with',        'cross_lingual', 'lemma',              'lemma'),               -- 25
    ('etym_calque_of',           'cross_lingual', 'lemma',              'lemma'),               -- 26
    ('etym_mention',             'cross_lingual', 'lemma',              'lemma'),               -- 27
    ('etym_link',                'cross_lingual', 'lemma',              'text_composition'),    -- 28
    ('etym_etymon',              'cross_lingual', 'lemma',              'lemma'),               -- 29
    -- ── Cross-modal ────────────────────────────────────────────────────
    ('recording_of',             'cross_modal',   'audio_recording',    'text_composition'),    -- 30
    ('has_contributor',          'cross_modal',   'audio_recording',    'text_composition'),    -- 31
    -- ── Unicode ────────────────────────────────────────────────────────
    ('maps_to_lowercase',        'unicode',       'codepoint',          'codepoint'),           -- 32
    ('case_folds_to',            'unicode',       'codepoint',          'codepoint'),           -- 33
    ('has_collation_weight',     'unicode',       'codepoint',          'collation_element'),   -- 34
    -- ── Model-derived: architecture + tokenizer + tensor metadata ──────
    ('in_model',                 'model_derived', 'tensor',             'model_architecture'),  -- 35
    ('in_layer',                 'model_derived', 'tensor',             'model_architecture'),  -- 36
    ('has_dtype',                'model_derived', 'tensor',             'text_composition'),    -- 37
    ('has_shape',                'model_derived', 'tensor',             'text_composition'),    -- 38
    ('has_hidden_size',          'model_derived', 'model_architecture', 'text_composition'),    -- 39
    ('has_num_layers',           'model_derived', 'model_architecture', 'text_composition'),    -- 40
    ('has_num_attention_heads',  'model_derived', 'model_architecture', 'text_composition'),    -- 41
    ('has_vocab_size',           'model_derived', 'model_architecture', 'text_composition'),    -- 42
    ('has_token_id',             'model_derived', 'word_form',          'text_composition'),    -- 43
    ('in_vocabulary',            'model_derived', 'word_form',          'model_architecture'),  -- 44
    ('has_tensor',               'model_derived', 'model_architecture', 'tensor'),              -- 45
    ('has_architecture_name',    'model_derived', 'model_architecture', 'text_composition'),    -- 46
    ('has_tensor_name',          'model_derived', 'tensor',             'text_composition'),    -- 47
    ('has_package_tensor_primitive',    'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_tuple',        'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_slot',         'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_layer_index',  'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_head_index',   'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_expert_index', 'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_modality',     'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_fused_slice',  'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_linearized_shape', 'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_tokenizer_model',      'model_derived', 'model_architecture', 'text_composition'),    -- 48
    ('has_token_in_tokenizer',   'model_derived', 'model_architecture', 'word_form'),           -- 49
    ('covers_lemma',             'model_derived', 'word_form',          'lemma'),               -- 50
    ('co_occurrence',            'model_derived', NULL,                 NULL),                  -- 51
    -- Model-package text artifact bindings: model_architecture → text_composition
    -- for the artifact's content. Same artifact across model snapshots collapses
    -- to ONE document with N has_*_artifact edges via content-addressed identity.
    ('has_config_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 52
    ('has_tokenizer_artifact',          'model_derived', 'model_architecture', 'text_composition'),  -- 53
    ('has_tokenizer_config_artifact',   'model_derived', 'model_architecture', 'text_composition'),  -- 54
    ('has_special_tokens_artifact',     'model_derived', 'model_architecture', 'text_composition'),  -- 55
    ('has_merges_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 56
    ('has_chat_template_artifact',      'model_derived', 'model_architecture', 'text_composition'),  -- 57
    ('has_generation_config_artifact',  'model_derived', 'model_architecture', 'text_composition'),  -- 58
    ('has_readme_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 59
    -- ── Model-derived: content-entity attestation surfaces ─────────────
    -- These are the load-bearing token↔token / patch↔patch / frame↔frame
    -- edges that accumulate per-tuple attestation events from every
    -- ingested model. Per docs/01-tensor-primitive-spec.md §IV.
    ('model_concept_similarity', 'model_derived', 'word_form',          'word_form'),           -- 52
    ('model_attention_pattern',  'model_derived', 'word_form',          'word_form'),           -- 53
    ('model_ffn_factor',         'model_derived', 'word_form',          'word_form'),           -- 54
    ('model_spatial_pattern',    'model_derived', NULL,                 NULL),                  -- 55  (polymorphic: pixel_region↔pixel_region or audio_chunk↔audio_chunk)
    ('model_cross_modal_pattern','model_derived', NULL,                 NULL),                  -- 56  (polymorphic: word_form↔pixel_region, word_form↔audio_chunk, decoder-token↔encoder-token, etc.)
    ('model_detection_class',    'model_derived', 'object_query',       'visual_concept'),      -- 57
    -- ── Semantic: WordNet pointers (synset ↔ synset) ────────────────────
    ('hypernym',                 'semantic',      'synset', 'synset'),                          -- 58
    ('hyponym',                  'semantic',      'synset', 'synset'),                          -- 59
    ('instance_hypernym',        'semantic',      'synset', 'synset'),                          -- 60
    ('instance_hyponym',         'semantic',      'synset', 'synset'),                          -- 61
    ('member_holonym',           'semantic',      'synset', 'synset'),                          -- 62
    ('substance_holonym',        'semantic',      'synset', 'synset'),                          -- 63
    ('part_holonym',             'semantic',      'synset', 'synset'),                          -- 64
    ('member_meronym',           'semantic',      'synset', 'synset'),                          -- 65
    ('substance_meronym',        'semantic',      'synset', 'synset'),                          -- 66
    ('part_meronym',             'semantic',      'synset', 'synset'),                          -- 67
    ('attribute',                'semantic',      'synset', 'synset'),                          -- 68
    ('derivationally_related',   'semantic',      'synset', 'synset'),                          -- 69
    ('antonym',                  'semantic',      'synset', 'synset'),                          -- 70
    ('similar_to',               'semantic',      'synset', 'synset'),                          -- 71
    ('also_see',                 'semantic',      'synset', 'synset'),                          -- 72
    ('verb_group',               'semantic',      'synset', 'synset'),                          -- 73
    ('entailment',               'semantic',      'synset', 'synset'),                          -- 74
    ('cause',                    'semantic',      'synset', 'synset'),                          -- 75
    ('participle_of_verb',       'semantic',      'synset', 'synset'),                          -- 76
    ('pertainym',                'semantic',      'synset', 'synset'),                          -- 77
    ('domain_of_synset_topic',   'semantic',      'synset', 'synset'),                          -- 78
    ('member_of_domain_topic',   'semantic',      'synset', 'synset'),                          -- 79
    ('domain_of_synset_region',  'semantic',      'synset', 'synset'),                          -- 80
    ('member_of_domain_region',  'semantic',      'synset', 'synset'),                          -- 81
    ('domain_of_synset_usage',   'semantic',      'synset', 'synset'),                          -- 82
    ('member_of_domain_usage',   'semantic',      'synset', 'synset'),                          -- 83
    -- ── Semantic: Wiktionary lemma ↔ lemma ─────────────────────────────
    ('synonym',                  'semantic',      'lemma',  'lemma'),                           -- 84
    ('coordinate_term',          'semantic',      'lemma',  'lemma'),                           -- 85
    ('derived',                  'semantic',      'lemma',  'lemma'),                           -- 86
    ('related',                  'semantic',      'lemma',  'lemma'),                           -- 87
    -- ── Unicode structural extensions (appended to preserve existing IDs) ─
    ('maps_to_uppercase',        'unicode',       'codepoint',          'codepoint'),           -- 96
    ('maps_to_titlecase',        'unicode',       'codepoint',          'codepoint'),           -- 97
    ('has_canonical_decomposition',      'unicode', 'codepoint',        'text_composition'),    -- 98
    ('has_compatibility_decomposition',  'unicode', 'codepoint',        'text_composition'),    -- 99
    ('canonical_composes_to',    'unicode',       'text_composition',   'codepoint'),           -- 100
    ('has_full_case_mapping',    'unicode',       'codepoint',          'text_composition'),    -- 101
    ('has_named_sequence',       'unicode',       'text_composition',   'text_composition'),    -- 102
    ('has_standardized_variant', 'unicode',       'codepoint',          'text_composition'),    -- 103
    ('has_emoji_sequence',       'unicode',       'text_composition',   'text_composition'),    -- 104
    ('has_emoji_zwj_sequence',   'unicode',       'text_composition',   'text_composition'),    -- 105
    ('confusable_with',          'unicode',       'text_composition',   'text_composition'),    -- 106
    ('idna_maps_to',             'unicode',       'codepoint',          'text_composition'),    -- 107
    ('has_bidi_mirroring_glyph', 'unicode',       'codepoint',          'codepoint'),           -- 108
    ('unihan_variant',           'unicode',       'codepoint',          'codepoint'),           -- 109
    ('unihan_reading',           'unicode',       'codepoint',          'text_composition'),    -- 110
    ('unihan_source',            'unicode',       'codepoint',          'text_composition'),    -- 111
    ('has_radical_stroke',       'unicode',       'codepoint',          'text_composition'),    -- 112
    -- Sequence-following bigram (Build-a-bear next-token prior). Populated
    -- by substrate.populate_sequence_following_edges from content trajectory
    -- ordinals. Source role = preceding token; target role = following token.
    -- Weighted by ln(1+freq) in sequence_following arena.
    ('often_follows',            'sequence',      'word_form',          'word_form'),           -- 113
    -- ── Cross-link (Unicode ↔ ISO / encoding-standard / CLDR) ──────────
    -- Per universal-cross-source-attestation: every text-bearing source
    -- attests cross-cuttingly. These edges land the cross-link semantic
    -- facts that previously had no substrate edge_type.
    ('has_iso_639_1_code',       'cross_lingual', 'language_name',      'text_composition'),    -- 114
    ('has_iso_639_2b_code',      'cross_lingual', 'language_name',      'text_composition'),    -- 115
    ('has_iso_639_2t_code',      'cross_lingual', 'language_name',      'text_composition'),    -- 116
    ('has_script',               'cross_lingual', 'language_name',      'text_composition'),    -- 117  (target = ISO 15924 4-letter script code as text_composition)
    ('has_region',               'cross_lingual', 'language_name',      'text_composition'),    -- 118  (target = ISO 3166-1 alpha-2 region code as text_composition)
    ('has_encoding_position',    'unicode',       'codepoint',          'text_composition'),    -- 119  (target = byte sequence in encoding's space as text_composition)
    ('has_ideographic_variant_in_collection', 'unicode', 'codepoint',   'text_composition'),    -- 120  (target = collection-qualified variant glyph identifier as text_composition)
    -- ── AP-8 unified-Glicko-surface migration edges ────────────────────
    -- POS / morph / deprel / lexname / language classifications attest on
    -- the unified substrate.edge_significance surface via these typed
    -- edges. Junction tables (entity_pos, pattern_deprel, etc.) remain as
    -- denormalized analytics caches; authoritative consensus lives here.
    ('has_pos',                  'structural',    'word_form',          'text_composition'),    -- 121  (target = POS category name "NOUN"/"VERB"/etc. as text_composition)
    ('has_morph_feature',        'structural',    'word_form',          'text_composition'),    -- 122  (target = "Gender=Masc"/"Number=Sing"/etc. as text_composition)
    ('has_deprel_pattern',       'structural',    'word_form',          'text_composition'),    -- 123  (target = dep relation "nsubj"/"obj"/etc. as text_composition)
    ('has_lexname',              'structural',    'synset',             'text_composition'),    -- 124  (target = WordNet lexname "noun.animal"/etc. as text_composition)
    ('has_language',             'cross_lingual', NULL,                 'language_name'),       -- 125  (polymorphic source — any entity that asserts a language tag)
    -- ── Generic classification attestation (Gate 1 #38 refactor 2026-05-19) ─
    -- Collapses per-dimension classification edge proliferation into a single
    -- polymorphic edge. Source = any classifiable content entity (codepoint,
    -- word_form, lemma, synset, ...). Target = content-hashed classification
    -- entity whose entity_type discriminates the dimension (general_category /
    -- script / block / bidi_class / east_asian_width / break_property / pos /
    -- lexname / morph_feature / deprel / language_name / ...). Arena routing
    -- by (edge_type × target_entity_type) per AP-30/AP-38 collapse principle —
    -- discrimination via (target_type × provenance × arena), not via
    -- per-dimension edge_type proliferation.
    --
    -- Migrating existing has_pos / has_lexname / has_morph_feature /
    -- has_deprel_pattern edges onto this generic kind is staged for a
    -- follow-up; both surfaces will coexist transitionally until the
    -- migration completes.
    ('has_classification',       'structural',    NULL,                 NULL)                   -- 126
) AS s(code, category, source_code, target_code)
LEFT JOIN substrate.entity_type src ON src.code = s.source_code
LEFT JOIN substrate.entity_type tgt ON tgt.code = s.target_code;

-- ── sql/schema/seed/validate.sql ───────────────────────────────────────
-- Seed inventory check. Set-based: collects every count that diverges
-- from the canonical inventory in one pass and raises with the full list,
-- so a fresh-DB apply doesn't fail on the first count and hide the rest.
DO $$
DECLARE
    failures TEXT[] := ARRAY[]::TEXT[];
    rec      RECORD;
    actual   BIGINT;
BEGIN
    FOR rec IN
        SELECT * FROM (VALUES
            ('substrate.entity_type',           34),
            ('substrate.physicality_type',       5),
            ('substrate.edge_role',              7),
            ('substrate.significance_context',  19),
            ('substrate.provenance',            63),
            ('substrate.bidi_class',            23),
            ('substrate.east_asian_width',       6),
            ('substrate.lexname',               45),
            ('substrate.pos',                   17),
            ('substrate.edge_type',            134),
            ('substrate.attestation_type',       3)
        ) AS t(table_name, expected)
    LOOP
        EXECUTE format('SELECT count(*) FROM %s', rec.table_name) INTO actual;
        IF actual <> rec.expected THEN
            failures := array_append(failures,
                format('%s = %s (expected %s)', rec.table_name, actual, rec.expected));
        END IF;
    END LOOP;

    IF array_length(failures, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'seed inventory mismatch: %', array_to_string(failures, '; ');
    END IF;
END $$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 7: core tables + LIST partitions ───────────────────────────

-- ── sql/schema/tables/core/entity.sql ───────────────────────────────────────
-- Entity is PURELY content-addressed: same content → same BLAKE3 hash →
-- same row. Period. Identity is the hash, not (type, hash). Classifications
-- ("this content is a word_form" / "this content is a lemma") live on
-- substrate.entity_classification, not in the entity's identity.
--
-- This is the substrate's invention rule: "dog" is "dog" regardless of
-- semantic role. Whether a decomposer USES this content as a word_form,
-- lemma, codepoint, grapheme_cluster, audio_recording, pixel_region, or
-- any other classification is metadata about how the entity is consumed,
-- not about what it IS.
--
-- LIST-partitioned by partition_bucket = (hash_bits_0_51 % 8). Eight child
-- partitions: entity_p0..entity_p7. The ingestion pipeline's N C# workers
-- route bundles by the same expression so worker K writes exclusively to
-- partition K (zero cross-worker row-lock contention). Worker count is
-- independent from partition count: workers can fan in onto the 8
-- partitions in any (workerCount, 8) ratio. We use LIST(partition_bucket)
-- — not PG HASH partitioning — because PG's hashint8 internal hash
-- function is hard to replicate in C#, and the spec calls for literal
-- `hash_bits_0_51 mod N` routing so C# can address the partition child
-- table directly by index.
--
-- Per-type query patterns JOIN substrate.entity_classification — the PG
-- executor partition-prunes the entity probe via the bucket column when
-- callers carry it in their WHERE, and B-tree on the (entity_pK).hash PK
-- gives O(log N) lookup within a partition.
--
-- hash_bits_0_51 + hash_bits_52_103 expose a 104-bit BLAKE3-derived prefix
-- as two BIGINT columns. Used for two purposes:
--   1. Reverse-resolving a composition LINESTRINGZM vertex back to its
--      child entity: each vertex (X, Z) mantissa carries the child's
--      hash prefix (X = hash_bits_0_51, Z = hash_bits_52_103) via the
--      bb_pack_hash_lo / bb_pack_hash_hi encoding. Unpacking a vertex and
--      joining against the (hash_bits_0_51, hash_bits_52_103) composite
--      btree (entity_hash_prefix_idx) recovers the child hash in one
--      indexed point lookup — no junction table required.
--   2. Batched lookups via substrate.entity_by_hash_prefix(BIGINT[],
--      BIGINT[]) for any caller that has hash prefixes in hand.
--
-- The expressions are inlined here (rather than calling substrate.bb_hash_lo
-- / bb_hash_hi) because GENERATED ALWAYS AS STORED requires the expression
-- to be evaluable at CREATE TABLE time, and the bb_* function definitions
-- live in the Phase 13 functions block. The two encodings are byte-for-byte
-- equivalent: any change to bb_hash_lo / bb_hash_hi must mirror here.
--
-- entity carries the entity's own 4D centroid + Hilbert index as denormalized
-- columns. This is a deterministic projection of the entity's content — atom
-- POINTZM coords from the pre-gen blob (codepoint Super-Fibonacci S^3 by UCA
-- rank via hartonomous_ucd_cp_centroid; audio sample value; pixel intensity;
-- tensor cell) for atoms; arithmetic-mean of child centroids for compositions.
-- Law #6 guarantees byte-identical output across runs: same content → same
-- hash → same blob-lookup-or-mean → same centroid. The columns are populated
-- INLINE by C# at COPY time from PhysicalityEmitter (which delegates to the
-- pre-gen native blob exports). No trigger, no reactive maintenance, no
-- physicality-write callback path — the cache is bit-identical to the blob
-- output by construction because nothing computes the centroid twice. The
-- fireflies partition has no entity-row centroid because fireflies are
-- per-model decorations attached to existing word_form entities, not the
-- entity's own identity-bearing centroid.
--
-- Why on the entity row: the centroid is referenced everywhere a parent walks
-- its child manifest (composition LINESTRINGZM vertices are children's
-- centroids). Joining substrate.physicality on every parent-walk would be a
-- hot-path table lookup per vertex; storing on entity makes it O(1) and
-- eliminates the PG round-trip from the partition-pruned scan path.
--
-- The substrate's 4D realization itself still lives in substrate.physicality,
-- partitioned by physicality_type_id. These columns are the pre-gen perf
-- cache projected onto the identity row for partition-pruned scan locality,
-- NOT a replacement for the physicality store.
CREATE TABLE substrate.entity (
    hash substrate.hash_value NOT NULL,
    hash_bits_0_51 BIGINT GENERATED ALWAYS AS (
          (get_byte(hash, 0)::BIGINT)
        | (get_byte(hash, 1)::BIGINT << 8)
        | (get_byte(hash, 2)::BIGINT << 16)
        | (get_byte(hash, 3)::BIGINT << 24)
        | (get_byte(hash, 4)::BIGINT << 32)
        | (get_byte(hash, 5)::BIGINT << 40)
        | ((get_byte(hash, 6) & 15)::BIGINT << 48)
    ) STORED,
    hash_bits_52_103 BIGINT GENERATED ALWAYS AS (
          ((get_byte(hash, 6) >> 4) & 15)::BIGINT
        | (get_byte(hash, 7)::BIGINT << 4)
        | (get_byte(hash, 8)::BIGINT << 12)
        | (get_byte(hash, 9)::BIGINT << 20)
        | (get_byte(hash, 10)::BIGINT << 28)
        | (get_byte(hash, 11)::BIGINT << 36)
        | (get_byte(hash, 12)::BIGINT << 44)
    ) STORED,
    partition_bucket SMALLINT NOT NULL
        CHECK (partition_bucket = (get_byte(hash, 0) & 7)),
    centroid_x     DOUBLE PRECISION,
    centroid_y     DOUBLE PRECISION,
    centroid_z     DOUBLE PRECISION,
    centroid_m     DOUBLE PRECISION,
    hilbert_index  BIGINT,
    PRIMARY KEY (hash, partition_bucket)
) PARTITION BY LIST (partition_bucket);
-- partition_bucket is a regular column rather than GENERATED because PG18
-- still rejects generated columns as partition keys (`cannot use generated
-- column in partition key`). The CHECK constraint enforces consistency with
-- the hash's byte 0 lowest 3 bits — same arithmetic the C# pipeline runs to
-- choose a worker: `(int)(hash[0] & 7)`.
-- PostgreSQL requires the partition key to be part of every UNIQUE / PRIMARY
-- KEY constraint on a partitioned table. The CHECK above ensures partition_bucket
-- is a bijective function of hash, so adding partition_bucket to the PK is a
-- no-op semantically (hash alone still uniquely identifies a row) but a hard
-- requirement structurally.

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom OR composition. Identity = BLAKE3 hash of content. Classifications via substrate.entity_classification. LIST-partitioned over 8 children entity_p0..entity_p7 by partition_bucket = (get_byte(hash, 0) & 7) = (hash_bits_0_51 % 8) — N C# ingestion workers route bundles by the same expression so worker K writes only to entity_pK. hash_bits_0_51 / hash_bits_52_103 expose a 104-bit BLAKE3 prefix as BIGINT columns so composition-LINESTRINGZM vertex (X, Z) mantissas resolve to full hashes via the composite btree entity_hash_prefix_idx. centroid_x/y/z/m + hilbert_index are denormalized pre-gen cache columns populated INLINE by C# at COPY time from PhysicalityEmitter (codepoint Super-Fibonacci S^3 by UCA rank via hartonomous_ucd_cp_centroid; arithmetic-mean of child centroids for compositions); bit-identical to the blob output by construction and equivalent to the entity-tier substrate.physicality POINTZM for the row. The authoritative 4D realization remains in substrate.physicality, partitioned by physicality_type_id.';

COMMENT ON COLUMN substrate.entity.hash_bits_0_51 IS
    'Bits 0..51 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_lo(bytea). Matches the X mantissa of composition LINESTRINGZM vertices and the X mantissa of edge.geom vertices via substrate.bb_pack_hash_lo. Used for batched lookup via substrate.entity_by_hash_prefix.';

COMMENT ON COLUMN substrate.entity.hash_bits_52_103 IS
    'Bits 52..103 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_hi(bytea). Matches the Z mantissa of composition LINESTRINGZM vertices and the Z mantissa of edge.geom vertices via substrate.bb_pack_hash_hi.';

COMMENT ON COLUMN substrate.entity.partition_bucket IS
    'Worker / partition routing key = (hash byte 0 & 7) = (hash_bits_0_51 % 8). Eight buckets in [0..7]. C# pipeline computes the same expression to assign bundles to workers; worker K writes only to entity_pK.';

COMMENT ON COLUMN substrate.entity.centroid_x IS
    'Denormalized X coordinate of the entity-tier POINTZM (atom: real content-derived coord — codepoint Super-Fibonacci S^3 component 0 by UCA rank, audio sample value, pixel intensity, tensor cell. Composition: arithmetic mean of children''s centroid_x). Populated INLINE by C# at COPY time via PhysicalityEmitter; no trigger, no reactive maintenance. Bit-identical to substrate.physicality POINTZM X for the entity-tier row of this entity by Law #6.';

COMMENT ON COLUMN substrate.entity.centroid_y IS
    'Denormalized Y coordinate of the entity-tier POINTZM. Same population path as centroid_x. Bit-identical to substrate.physicality POINTZM Y for the entity-tier row.';

COMMENT ON COLUMN substrate.entity.centroid_z IS
    'Denormalized Z coordinate of the entity-tier POINTZM. Same population path as centroid_x. Bit-identical to substrate.physicality POINTZM Z for the entity-tier row.';

COMMENT ON COLUMN substrate.entity.centroid_m IS
    'Denormalized M coordinate of the entity-tier POINTZM (atom: per-partition CHECK-declared meaning — UCD bitmask in M for codepoints, salience for fireflies, etc.). Same population path as centroid_x. Bit-identical to substrate.physicality POINTZM M for the entity-tier row.';

COMMENT ON COLUMN substrate.entity.hilbert_index IS
    'Denormalized Hilbert curve index over (centroid_x, centroid_y, centroid_z, centroid_m) at 16 bits per axis. Enables range scans that combine radial (entity_tier_hint) + angular spatial locality without LATERAL ST_4D_Centroid on every parent walk. Populated INLINE by C# from Hilbert.Index(point4, 16); reproducible by Law #6.';

-- ── sql/schema/tables/core/entity_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p0
    PARTITION OF substrate.entity FOR VALUES IN (0);

-- ── sql/schema/tables/core/entity_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p1
    PARTITION OF substrate.entity FOR VALUES IN (1);

-- ── sql/schema/tables/core/entity_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p2
    PARTITION OF substrate.entity FOR VALUES IN (2);

-- ── sql/schema/tables/core/entity_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p3
    PARTITION OF substrate.entity FOR VALUES IN (3);

-- ── sql/schema/tables/core/entity_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p4
    PARTITION OF substrate.entity FOR VALUES IN (4);

-- ── sql/schema/tables/core/entity_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p5
    PARTITION OF substrate.entity FOR VALUES IN (5);

-- ── sql/schema/tables/core/entity_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p6
    PARTITION OF substrate.entity FOR VALUES IN (6);

-- ── sql/schema/tables/core/entity_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_p7
    PARTITION OF substrate.entity FOR VALUES IN (7);

-- ── sql/schema/tables/core/edge.sql ───────────────────────────────────────
-- Edge identity = BLAKE3 of (edge_type_id, ordered participant hashes).
-- No surrogate id. PK (edge_type_id, hash). Partitioned by edge_type_id.
-- geom is built inline at edge-emit by the bundled-emit pipeline from
-- participants' mantissa-packed identity-POINTZMs in role order — no
-- post-pass backfill, no NULL window. AP-37.
CREATE TABLE substrate.edge (
    edge_type_id  INT  NOT NULL REFERENCES substrate.edge_type(id),
    hash          substrate.hash_value NOT NULL,
    geom          geometry(GeometryZM),
    provenance_id INT  NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (edge_type_id, hash)
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge IS
    'Typed n-ary substrate edges with 4D geometric trajectories. Identity = (edge_type_id, BLAKE3 of participant role-ordered hashes).';

-- ── sql/schema/tables/core/edge_structural.sql ───────────────────────────────────────
-- Partition for structural edge_types (IDs 1..15 per sql/schema/seed/edge_type.sql).
-- Within-modality structural composition for the text stack.
CREATE TABLE substrate.edge_structural
    PARTITION OF substrate.edge FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

-- ── sql/schema/tables/core/edge_cross_lingual.sql ───────────────────────────────────────
-- Partition for cross_lingual edge_types (IDs 16..29 per sql/schema/seed/edge_type.sql).
-- Translation, etymology, and language-name relations across language boundaries.
CREATE TABLE substrate.edge_cross_lingual
    PARTITION OF substrate.edge FOR VALUES IN (16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29);

-- ── sql/schema/tables/core/edge_cross_modal.sql ───────────────────────────────────────
-- Partition for cross_modal edge_types (IDs 30..31 per sql/schema/seed/edge_type.sql).
-- Audio↔text bindings (recording_of, has_contributor). Cross-modal attestation
-- edges produced by safetensors decomposition (model_cross_modal_pattern) live
-- in the dedicated edge_model_cross_content partition, not here.
CREATE TABLE substrate.edge_cross_modal
    PARTITION OF substrate.edge FOR VALUES IN (30, 31);

-- ── sql/schema/tables/core/edge_unicode.sql ───────────────────────────────────────
-- Partition for unicode edge_types. IDs 32..34 are the original core UCD
-- edges; IDs 96..112 are appended structural Unicode surfaces so existing
-- model/semantic partitions keep stable IDs.
CREATE TABLE substrate.edge_unicode
    PARTITION OF substrate.edge FOR VALUES IN (
        32, 33, 34,
        96, 97, 98, 99, 100, 101, 102, 103, 104,
        105, 106, 107, 108, 109, 110, 111, 112
    );

-- ── sql/schema/tables/core/edge_model.sql ───────────────────────────────────────
-- Partition for model_derived metadata edge_types (IDs 35..59 per
-- sql/schema/seed/edge_type.sql). Architecture / tokenizer / tensor metadata
-- + per-model-package text artifact bindings. Low cardinality per ingested
-- model — bounded by model structural shape, not per-token attestation
-- volume. Hot per-instance attestation tables live in their own partitions
-- below.
CREATE TABLE substrate.edge_model
    PARTITION OF substrate.edge FOR VALUES IN (35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59);

-- ── sql/schema/tables/core/edge_model_concept_similarity.sql ───────────────────────────────────────
-- Partition for the model_concept_similarity edge_type (ID 52). Per-token-pair
-- semantic-similarity attestations from EmbeddingLookup tables (cosine of
-- embedding rows), LM heads (model_lm_head_projection attestation), MoE
-- routers (model_moe_router attestation), and LoRA adapters
-- (model_lora_adapter_evidence attestation) — all stratified by attestation_type
-- on substrate.edge_significance.
--
-- High-cardinality: ~K² per ingested model where K = vocab tokens per model.
-- Isolated partition gives index locality + fast scans for both recompose
-- (read all attestations on a target tensor's edge slice) and inference
-- (A* expansion of similarity neighbors).
CREATE TABLE substrate.edge_model_concept_similarity
    PARTITION OF substrate.edge FOR VALUES IN (60);

-- ── sql/schema/tables/core/edge_model_attention_pattern.sql ───────────────────────────────────────
-- Partition for the model_attention_pattern edge_type (ID 53). Per-token-pair
-- attention attestations from AttentionBlock tuples (Q^T·K and V·O^T) across
-- every layer × head of every ingested model — stratified by attestation_type
-- (model_attention_qk_pattern, model_attention_vo_pattern) on
-- substrate.edge_significance.
--
-- The hottest table in the substrate. Cardinality scales with
-- (ingested_models × layers × heads × top_k_token_pairs_per_attention) — easily
-- billions of rows for a heavy farm. Isolated partition for maximum index
-- locality + partition pruning during both inference traversal and recompose.
CREATE TABLE substrate.edge_model_attention_pattern
    PARTITION OF substrate.edge FOR VALUES IN (61);

-- ── sql/schema/tables/core/edge_model_ffn_factor.sql ───────────────────────────────────────
-- Partition for the model_ffn_factor edge_type (ID 54). Per-token-pair FFN
-- attestations from SwiGluFfn / BertFfn tuples (model_ffn_full_path) and MoE
-- expert FFNs (model_moe_expert_response) — stratified by attestation_type
-- on substrate.edge_significance.
--
-- High cardinality: scales with (ingested_models × layers × ffn_intermediate_dim
-- × top_k_token_pairs_per_neuron). Comparable to attention_pattern volume on
-- non-MoE models; MoE multiplies by num_experts. Isolated partition for
-- locality.
CREATE TABLE substrate.edge_model_ffn_factor
    PARTITION OF substrate.edge FOR VALUES IN (62);

-- ── sql/schema/tables/core/edge_model_cross_content.sql ───────────────────────────────────────
-- Partition for cross-content attestation edge_types (IDs 63..65 per
-- sql/schema/seed/edge_type.sql):
--   63 model_spatial_pattern    (pixel_region↔pixel_region or audio_chunk↔audio_chunk)
--   64 model_cross_modal_pattern (text↔image, text↔audio, decoder-token↔encoder-token)
--   65 model_detection_class     (object_query↔visual_concept)
--
-- High-cardinality when vision / audio / detection models are ingested.
-- Co-located in one partition because the three share the cross-modality
-- access pattern (recompose for vision tower / cross-encoder / detection
-- head reads attestations across all three edge_types together).
CREATE TABLE substrate.edge_model_cross_content
    PARTITION OF substrate.edge FOR VALUES IN (63, 64, 65);

-- ── sql/schema/tables/core/edge_default.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_default
    PARTITION OF substrate.edge DEFAULT;

-- ── sql/schema/tables/core/edge_member.sql ───────────────────────────────────────
-- Each edge has an ordered list of (entity, role) participants.
-- Edge identity stays composite: (edge_type_id, edge_hash) — edge type
-- IS structural per the architecture (it defines the relation's semantics
-- e.g. has_sense vs has_lemma vs translation_of).
-- Entity reference is hash-only (Phase C of unification refactor —
-- substrate.entity has hash-only PK).
--
-- Partitioning decision (2026-05-18, Gate 1 reopened item #34):
-- The previous PARTITION BY LIST (edge_type_id) admitted edge-type pruning
-- but made writes worker-contended — all workers' edges of a common type
-- (has_sense, translation_link, has_gloss) hit the same partition. The
-- dominant query pattern on edge_member is "find all edges referencing
-- this entity" (e.g. SubstrateAdjacencyBuilder, FfnEdgeSlotSynthesizer,
-- inference traversal from a seed entity hash outward), which hits the
-- edge_member_entity_hash_idx — orthogonal to edge_type partitioning.
-- The new shape: PARTITION BY LIST (partition_bucket) where
-- partition_bucket = (entity_hash byte 0 & 7) = (hash_bits_0_51 % 8) of
-- entity_hash, matching substrate.entity's partition bucket exactly.
-- Worker K writes only to edge_member_pK for every record whose
-- entity_hash byte 0 & 7 == K. Edge-type filter remains a planner filter
-- on the partition probe — perfectly acceptable for the LISTs we
-- previously defined (15-25 edge types per LIST partition), now collapsed
-- into the hash-partition child's btree-on-PK.
CREATE TABLE substrate.edge_member (
    edge_type_id INT  NOT NULL,
    edge_hash    substrate.hash_value NOT NULL,
    entity_hash  substrate.hash_value NOT NULL,
    edge_role_id INT  NOT NULL REFERENCES substrate.edge_role(id),
    role_position INT NOT NULL DEFAULT 0,
    partition_bucket SMALLINT NOT NULL
        CHECK (partition_bucket = (get_byte(entity_hash, 0) & 7)),
    PRIMARY KEY (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position, partition_bucket)
    -- FKs application-enforced. Streaming ingestion drains each record kind
    -- independently, so consumers must treat edge/entity/member visibility as
    -- eventually consistent within the phase until DrainPendingAsync/FlushAsync.
) PARTITION BY LIST (partition_bucket);

COMMENT ON TABLE substrate.edge_member IS
    'N-ary edge participants with roles. Edge identity: (edge_type_id, edge_hash). Entity reference: hash only (no type_id). LIST-partitioned by partition_bucket = (entity_hash byte 0 & 7) over 8 children — matches substrate.entity bucket exactly so N C# ingestion workers route bundles by the same expression and worker K writes only to edge_member_pK. Replaces the prior LIST(edge_type_id) partitioning which serialized writes of common edge types across workers. FKs application-enforced.';

COMMENT ON COLUMN substrate.edge_member.partition_bucket IS
    'Worker / partition routing key over entity_hash byte 0. Mirrors substrate.entity.partition_bucket; matched routing means worker K co-locates its entity_pK writes with its edge_member_pK writes.';

-- ── sql/schema/tables/core/edge_member_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p0
    PARTITION OF substrate.edge_member FOR VALUES IN (0);

-- ── sql/schema/tables/core/edge_member_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p1
    PARTITION OF substrate.edge_member FOR VALUES IN (1);

-- ── sql/schema/tables/core/edge_member_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p2
    PARTITION OF substrate.edge_member FOR VALUES IN (2);

-- ── sql/schema/tables/core/edge_member_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p3
    PARTITION OF substrate.edge_member FOR VALUES IN (3);

-- ── sql/schema/tables/core/edge_member_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p4
    PARTITION OF substrate.edge_member FOR VALUES IN (4);

-- ── sql/schema/tables/core/edge_member_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p5
    PARTITION OF substrate.edge_member FOR VALUES IN (5);

-- ── sql/schema/tables/core/edge_member_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p6
    PARTITION OF substrate.edge_member FOR VALUES IN (6);

-- ── sql/schema/tables/core/edge_member_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_p7
    PARTITION OF substrate.edge_member FOR VALUES IN (7);

-- ── sql/schema/tables/core/physicality.sql ───────────────────────────────────────
-- ONE physicality row per (physicality_type_id, entity_hash, content_hash).
-- PostGIS-native geometry(GeometryZM) is the universal storage; substrate.st_4d_*
-- operators extend PostGIS to use the M dimension (raw ST_Distance / ST_Centroid
-- / ST_FrechetDistance drop M and are forbidden — AP-4). Per-partition CHECK
-- constraints enforce per-type geometric shape (POINTZM for atoms, LINESTRINGZM
-- / MULTILINESTRINGZM for compositions).
--
-- Geometric expressions across the substrate:
--   * Atom physicality (codepoint S3 position, audio sample, image pixel,
--     etc.): geom = POINTZM at the atom's real content-derived centroid in
--     its modality's metric space. Codepoints: 4 real Super-Fibonacci S^3
--     unit-quaternion components by UCA collation rank
--     (`scripts/build/generate_unicode_tables.py:83,1080`). No mantissa
--     packing on atom POINTZMs — atoms have no children to encode.
--   * Composition physicality (word_form, lemma, morpheme, text_composition,
--     sentence, paragraph, document, audio_chunk, image_region, video_shot —
--     compositions at any tier): geom = LINESTRINGZM (or MULTILINESTRINGZM
--     for branching / discontinuous structure) whose vertices encode the
--     children-in-order via the mantissa packing contract from
--     substrate.bb_pack_*:
--         X mantissa = child hash bits 0..51 (bb_pack_hash_lo)
--         Y mantissa = (ordinal_position, rle_count) packed (bb_pack_ordinal_rle)
--         Z mantissa = child hash bits 52..103 (bb_pack_hash_hi)
--         M mantissa = metadata flags (bb_pack_metadata)
--     Each vertex IS a btree-indexable, R-tree-indexable, reconstruction-ready
--     child reference at its position — same vocabulary at every tier of
--     entity/content. Reverse-resolve via substrate.entity_by_hash_prefix
--     against the (hash_bits_0_51, hash_bits_52_103) composite btree.
--     substrate.get_composition_children walks the vertex stream.
--
-- ST_Frechet / Hausdorff over two composition geoms compares STRUCTURAL
-- identity patterns (which children at which positions) — analogy completion,
-- frayed-edge detection, application-fault matching, security-signature
-- matching across telemetry. Not real-coord trajectory similarity at the
-- composition tier; atom POINTZM is real coord, composition is structural.
--
-- content_hash distinguishes multiple physicalities of the same
-- (physicality_type, entity) — e.g., multiple firefly samples per token from
-- different models.
--
-- Hash-only entity reference: substrate.entity has a hash-only PK; physicality
-- references entities by hash alone. FK to substrate.entity(hash) is
-- application-enforced (pipeline batch ordering writes entities before
-- physicalities; PG18.3 partitionwise-FK SEGV pattern conservatively avoided).
--
-- NO array columns. The prior transitional child_hashes / ordinal_starts /
-- rle_counts arrays violated 1NF + FK integrity (see feedback-no-array-columns).
-- The mantissa-packed vertex stream IS the canonical encoding; no sidecar.
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry(GeometryZM) NOT NULL,
    partition_bucket    SMALLINT NOT NULL
        CHECK (partition_bucket = (get_byte(entity_hash, 0) & 7)),
    PRIMARY KEY (physicality_type_id, entity_hash, content_hash, partition_bucket)
) PARTITION BY LIST (physicality_type_id);
-- Two-level partitioning:
-- Tier 1: LIST(physicality_type_id) — keeps modality / role separation
--   (entity, content, firefly, entity_shape, ingestion_trajectory, default).
-- Tier 2: LIST(partition_bucket = entity_hash byte 0 & 7) — 8 children per
--   tier-1 partition. Same routing key as substrate.entity / edge_member.
--   Worker K writes to (physicality_type_X_pK) for every modality X.
-- PostgreSQL requires the leaf-level partition key (partition_bucket) to be
-- in the PK / UNIQUE constraint at the root level.

COMMENT ON TABLE substrate.physicality IS
    'ONE substrate-level geometric expression per (physicality_type_id, entity_hash, content_hash). PostGIS geometry(GeometryZM); substrate.st_4d_* operators extend PostGIS to honor the M dimension. Atom geom = POINTZM at real content-derived centroid (no packing — atoms have no children). Composition geom = LINESTRINGZM with mantissa-packed child refs via bb_pack_hash_lo / bb_pack_ordinal_rle / bb_pack_hash_hi / bb_pack_metadata — the geometry IS the indexed child manifest at every tier. content_hash distinguishes co-typed multi-source samples per entity.';

-- ── sql/schema/tables/core/physicality_entity.sql ───────────────────────────────────────
-- physicality_type_id = 1, code = 'entity'.
--
-- Tiered building blocks. The brick's own internal structure.
--
-- Atom POINTZM with real content-derived coords. Codepoint atoms get
-- Super-Fibonacci S^3 unit-quaternion by UCA collation rank with the
-- UCD bitmask packed into M.
--
-- Composition LINESTRINGZM (or MULTILINESTRINGZM for branching tiers)
-- through child entity hash references. Vertices mantissa-packed:
--   X = bb_pack_hash_lo(child.hash_bits_0_51)
--   Y = bb_pack_ordinal_rle(ordinal, rle_count)
--   Z = bb_pack_hash_hi(child.hash_bits_52_103)
--   M = bb_pack_metadata(0)
-- word_form `cat` = a LINESTRINGZM with 3 vertices packing the c, a, t
-- codepoint hashes in order. The geometry IS the indexed child manifest.
-- Reverse-resolve via bb_unpack_* → composite btree on
-- (hash_bits_0_51, hash_bits_52_103). Same-content children dedupe to
-- one entity referenced multiple times; rle compresses runs.
--
-- Modality lives on entity_type, NOT physicality_type.
CREATE TABLE substrate.physicality_entity
    PARTITION OF substrate.physicality FOR VALUES IN (1)
    PARTITION BY LIST (partition_bucket);
-- CHECK admits every GeometryZM subtype so future modalities (audio,
-- image regions, video frames, model-weight tensors) land in the same
-- partition without a schema change. Modality is determined by the
-- attached entity's entity_type; shape carries the within-modality
-- structural distinction PostGIS already knows about.
ALTER TABLE substrate.physicality_entity
    ADD CONSTRAINT physicality_entity_geom
    CHECK (GeometryType(geom) IN (
              'POINT', 'LINESTRING', 'MULTILINESTRING',
              'POLYGON', 'MULTIPOLYGON', 'MULTIPOINT',
              'GEOMETRYCOLLECTION')
           AND ST_NDims(geom) = 4);

-- ── sql/schema/tables/core/physicality_entity_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p0
    PARTITION OF substrate.physicality_entity FOR VALUES IN (0);

-- ── sql/schema/tables/core/physicality_entity_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p1
    PARTITION OF substrate.physicality_entity FOR VALUES IN (1);

-- ── sql/schema/tables/core/physicality_entity_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p2
    PARTITION OF substrate.physicality_entity FOR VALUES IN (2);

-- ── sql/schema/tables/core/physicality_entity_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p3
    PARTITION OF substrate.physicality_entity FOR VALUES IN (3);

-- ── sql/schema/tables/core/physicality_entity_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p4
    PARTITION OF substrate.physicality_entity FOR VALUES IN (4);

-- ── sql/schema/tables/core/physicality_entity_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p5
    PARTITION OF substrate.physicality_entity FOR VALUES IN (5);

-- ── sql/schema/tables/core/physicality_entity_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p6
    PARTITION OF substrate.physicality_entity FOR VALUES IN (6);

-- ── sql/schema/tables/core/physicality_entity_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_p7
    PARTITION OF substrate.physicality_entity FOR VALUES IN (7);

-- ── sql/schema/tables/core/physicality_firefly.sql ───────────────────────────────────────
-- physicality_type_id = 2, code = 'firefly'.
--
-- Per-model embedding-row POINTZM specimens attached to existing entities
-- (typically word_form or codepoint). Each ingested AI model contributes
-- one POINTZM per token from its embedding layer: the model's N-dimensional
-- embedding row is projected DOWN INTO the substrate's 4D space via
-- Procrustes / Kabsch alignment, and the resulting (x, y, z, magnitude)
-- POINTZM is stored here. Many models per token => many POINTZM rows on
-- the same entity_hash, distinguished by content_hash.
--
-- MULTIPOINTZM also allowed for aggregated cross-model surfaces written
-- as one row per entity (cross-model consensus reads / shape comparisons).
CREATE TABLE substrate.physicality_firefly
    PARTITION OF substrate.physicality FOR VALUES IN (2)
    PARTITION BY LIST (partition_bucket);
ALTER TABLE substrate.physicality_firefly
    ADD CONSTRAINT physicality_firefly_geom
    CHECK (GeometryType(geom) IN ('POINT', 'MULTIPOINT')
           AND ST_NDims(geom) = 4);

-- ── sql/schema/tables/core/physicality_firefly_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p0
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (0);

-- ── sql/schema/tables/core/physicality_firefly_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p1
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (1);

-- ── sql/schema/tables/core/physicality_firefly_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p2
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (2);

-- ── sql/schema/tables/core/physicality_firefly_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p3
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (3);

-- ── sql/schema/tables/core/physicality_firefly_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p4
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (4);

-- ── sql/schema/tables/core/physicality_firefly_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p5
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (5);

-- ── sql/schema/tables/core/physicality_firefly_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p6
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (6);

-- ── sql/schema/tables/core/physicality_firefly_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_firefly_p7
    PARTITION OF substrate.physicality_firefly FOR VALUES IN (7);

-- ── sql/schema/tables/core/physicality_content.sql ───────────────────────────────────────
-- physicality_type_id = 3, code = 'content'.
--
-- Content trajectories — sequences of entity bricks. A text_composition
-- "the cat sat on the mat" is a LINESTRINGZM with 6 vertices:
--   V1 = pack(hash(the), ord=1, rle=1, meta=0)
--   V2 = pack(hash(cat), ord=2, rle=1, meta=0)
--   V3 = pack(hash(sat), ord=3, rle=1, meta=0)
--   V4 = pack(hash(on),  ord=4, rle=1, meta=0)
--   V5 = pack(hash(the), ord=5, rle=1, meta=0)   -- same hash as V1, distinct ordinal
--   V6 = pack(hash(mat), ord=6, rle=1, meta=0)
-- Same content dedupes to one entity referenced at multiple ordinals.
-- rle compresses runs.
--
-- The geometry IS the indexed child manifest at the content tier. The
-- walk stops at the first entity-tier brick — the brick's internal
-- structure lives in its own entity-partition physicality.
--
-- Reverse-resolve a vertex to its child brick by unpacking (X, Z) into
-- (hash_bits_0_51, hash_bits_52_103) and JOINing against the composite
-- btree on substrate.entity_by_hash_prefix — one bulk lookup recovers
-- the full child slice.
--
-- MULTILINESTRINGZM for discontinuous / branching / multi-stream
-- trajectories (footnote bodies interleaved with main text, bilingual
-- interlinear, etc.).
--
CREATE TABLE substrate.physicality_content
    PARTITION OF substrate.physicality FOR VALUES IN (3)
    PARTITION BY LIST (partition_bucket);
-- LINESTRING / MULTILINESTRING for ordered trajectories (text, audio,
-- code). POLYGON / MULTIPOLYGON for closed-region content (image
-- regions, video shots whose spatial extent matters more than order).
-- GEOMETRYCOLLECTION for mixed-tier content packages. All 4D.
ALTER TABLE substrate.physicality_content
    ADD CONSTRAINT physicality_content_geom
    CHECK (GeometryType(geom) IN (
              'LINESTRING', 'MULTILINESTRING',
              'POLYGON', 'MULTIPOLYGON',
              'GEOMETRYCOLLECTION')
           AND ST_NDims(geom) = 4);

-- ── sql/schema/tables/core/physicality_content_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p0
    PARTITION OF substrate.physicality_content FOR VALUES IN (0);

-- ── sql/schema/tables/core/physicality_content_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p1
    PARTITION OF substrate.physicality_content FOR VALUES IN (1);

-- ── sql/schema/tables/core/physicality_content_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p2
    PARTITION OF substrate.physicality_content FOR VALUES IN (2);

-- ── sql/schema/tables/core/physicality_content_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p3
    PARTITION OF substrate.physicality_content FOR VALUES IN (3);

-- ── sql/schema/tables/core/physicality_content_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p4
    PARTITION OF substrate.physicality_content FOR VALUES IN (4);

-- ── sql/schema/tables/core/physicality_content_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p5
    PARTITION OF substrate.physicality_content FOR VALUES IN (5);

-- ── sql/schema/tables/core/physicality_content_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p6
    PARTITION OF substrate.physicality_content FOR VALUES IN (6);

-- ── sql/schema/tables/core/physicality_content_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_content_p7
    PARTITION OF substrate.physicality_content FOR VALUES IN (7);

-- ── sql/schema/tables/core/physicality_entity_shape.sql ───────────────────────────────────────
-- physicality_type_id = 15, code = 'entity_shape'.
--
-- Real-coord canonical-shape geometry. Answers the question: "what does
-- this entity look like in 4D as a structural fingerprint?"
--
-- For atoms (no children): POINTZM at the modality's anchor coord —
-- codepoint Super-Fibonacci S^3 unit-quaternion by UCA collation rank
-- from the pre-gen UCD blob; audio sample at signal coord; pixel channel
-- at intensity coord; tensor cell at value coord. All four (X, Y, Z, M)
-- are real metric coords. No mantissa packing. No bitmask payload.
--
-- For compositions (any tier of any modality): LINESTRINGZM (or
-- MULTILINESTRINGZM for branching shapes) whose vertices ARE the
-- children's identity POINTZM centroids in canonical order. Each vertex
-- is a real metric coord in the parent's 4D frame. Fréchet / Hausdorff
-- matchable; gist_geometry_ops_nd R-tree-indexed.
--
-- Modality lives on the attached entity's entity_type (recovered via
-- substrate.entity_classification join). The partition itself is
-- modality-agnostic; per-axis meaning derives from the modality of the
-- entity it attaches to.
--
-- Companion partition: physicality_ingestion_trajectory (id 16) holds
-- the mantissa-packed recomposition recipe for the same composition
-- entity. A composition typically has both rows present — one in each
-- partition — answering different queries.
CREATE TABLE substrate.physicality_entity_shape
    PARTITION OF substrate.physicality FOR VALUES IN (15)
    PARTITION BY LIST (partition_bucket);

ALTER TABLE substrate.physicality_entity_shape
    ADD CONSTRAINT physicality_entity_shape_geom
    CHECK (
        GeometryType(geom) IN (
            'POINT', 'LINESTRING', 'MULTILINESTRING',
            'POLYGON', 'MULTIPOLYGON', 'MULTIPOINT',
            'GEOMETRYCOLLECTION'
        )
        AND ST_NDims(geom) = 4
    );

COMMENT ON TABLE substrate.physicality_entity_shape IS
    'Real-coord canonical-shape geometry. POINTZM for atoms at modality anchor coords; LINESTRINGZM through children identity POINTZM centroids for compositions. Modality recovered from entity_classification. Fréchet / Hausdorff matchable.';

-- ── sql/schema/tables/core/physicality_entity_shape_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p0
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (0);

-- ── sql/schema/tables/core/physicality_entity_shape_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p1
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (1);

-- ── sql/schema/tables/core/physicality_entity_shape_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p2
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (2);

-- ── sql/schema/tables/core/physicality_entity_shape_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p3
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (3);

-- ── sql/schema/tables/core/physicality_entity_shape_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p4
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (4);

-- ── sql/schema/tables/core/physicality_entity_shape_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p5
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (5);

-- ── sql/schema/tables/core/physicality_entity_shape_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p6
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (6);

-- ── sql/schema/tables/core/physicality_entity_shape_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_entity_shape_p7
    PARTITION OF substrate.physicality_entity_shape FOR VALUES IN (7);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory.sql ───────────────────────────────────────
-- physicality_type_id = 16, code = 'ingestion_trajectory'.
--
-- Mantissa-packed identity geometry. Answers the question: "what children
-- did this composition reference, in canonical order, so the substrate
-- can recompose them?"
--
-- LINESTRINGZM (or MULTILINESTRINGZM for branching / parallel / multi-tier
-- content; POLYGONZM / MULTIPOLYGONZM / GEOMETRYCOLLECTIONZM for
-- closed-region or heterogeneous bundle content) whose vertices encode
-- child entity hash refs via the bb_pack_* contract:
--
--   X mantissa = bb_pack_hash_lo(child.hash_bits_0_51)    -- 52 bits
--   Y mantissa = bb_pack_ordinal_rle(ordinal, rle_count)  -- 32-bit ordinal | 20-bit RLE
--   Z mantissa = bb_pack_hash_hi(child.hash_bits_52_103)  -- 52 bits
--   M mantissa = bb_pack_metadata(flags)                  -- 52 bits
--
-- Each vertex IS a btree-indexable, R-tree-indexable, reconstruction-ready
-- child reference at its position. Reverse-resolve via
-- substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]) over the composite
-- btree on substrate.entity(hash_bits_0_51, hash_bits_52_103) — one bulk
-- lookup recovers the full child slice. substrate.get_composition_children
-- walks the vertex stream.
--
-- The bb_pack_* contract puts packed payload in the integer-exact range
-- [2^52, 2^53). Real-coord canonical shapes (whose ST_X falls outside that
-- range for typical modality anchors) belong in physicality_entity_shape
-- (id 15) instead. Per-row CHECK enforces only geometry shape and
-- dimensionality; partition routing (physicality_type_id = 16) carries
-- the packed-vs-real discrimination.
--
-- Companion partition: physicality_entity_shape (id 15) holds the
-- real-coord canonical-shape geometry for the same composition entity.
CREATE TABLE substrate.physicality_ingestion_trajectory
    PARTITION OF substrate.physicality FOR VALUES IN (16)
    PARTITION BY LIST (partition_bucket);

ALTER TABLE substrate.physicality_ingestion_trajectory
    ADD CONSTRAINT physicality_ingestion_trajectory_geom
    CHECK (
        GeometryType(geom) IN (
            'LINESTRING', 'MULTILINESTRING',
            'POLYGON', 'MULTIPOLYGON',
            'GEOMETRYCOLLECTION'
        )
        AND ST_NDims(geom) = 4
    );

COMMENT ON TABLE substrate.physicality_ingestion_trajectory IS
    'Mantissa-packed identity geometry. LINESTRINGZM (or MULTI* / POLYGON* / COLLECTION) vertices encode child entity hash refs via bb_pack_hash_lo / bb_pack_ordinal_rle / bb_pack_hash_hi / bb_pack_metadata. Reverse-resolve via substrate.entity_by_hash_prefix composite-btree. Companion to physicality_entity_shape (id 15).';

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p0
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (0);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p1
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (1);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p2
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (2);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p3
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (3);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p4
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (4);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p5
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (5);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p6
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (6);

-- ── sql/schema/tables/core/physicality_ingestion_trajectory_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_ingestion_trajectory_p7
    PARTITION OF substrate.physicality_ingestion_trajectory FOR VALUES IN (7);

-- ── sql/schema/tables/core/physicality_default.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default
    PARTITION OF substrate.physicality DEFAULT
    PARTITION BY LIST (partition_bucket);

-- ── sql/schema/tables/core/physicality_default_p0.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p0
    PARTITION OF substrate.physicality_default FOR VALUES IN (0);

-- ── sql/schema/tables/core/physicality_default_p1.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p1
    PARTITION OF substrate.physicality_default FOR VALUES IN (1);

-- ── sql/schema/tables/core/physicality_default_p2.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p2
    PARTITION OF substrate.physicality_default FOR VALUES IN (2);

-- ── sql/schema/tables/core/physicality_default_p3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p3
    PARTITION OF substrate.physicality_default FOR VALUES IN (3);

-- ── sql/schema/tables/core/physicality_default_p4.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p4
    PARTITION OF substrate.physicality_default FOR VALUES IN (4);

-- ── sql/schema/tables/core/physicality_default_p5.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p5
    PARTITION OF substrate.physicality_default FOR VALUES IN (5);

-- ── sql/schema/tables/core/physicality_default_p6.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p6
    PARTITION OF substrate.physicality_default FOR VALUES IN (6);

-- ── sql/schema/tables/core/physicality_default_p7.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default_p7
    PARTITION OF substrate.physicality_default FOR VALUES IN (7);

-- ── sql/schema/tables/core/entity_significance.sql ───────────────────────────────────────
-- Glicko-2 ratings on entities, per arena, per attestation_type. Hash-only
-- entity reference (Phase C of unification refactor — substrate.entity has
-- hash-only PK, no entity_type_id).
--
-- attestation_type_id partitions the rating surface so corpus-derived,
-- model-derived, lexicon-curated, and inference-outcome evidence stay
-- distinguishable in their contribution to the same (arena, entity) rating.
-- Same content from corpus_co_occurrence_window AND lexical_curated_relation
-- gets two separate rows; the inference engine and recomposer can blend
-- them at query time per AttestationTypeBlend.
CREATE TABLE substrate.entity_significance (
    context_type_id     INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_hash         substrate.hash_value NOT NULL,
    attestation_type_id INT NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma               substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility          substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, entity_hash, attestation_type_id)
    -- FK to substrate.entity(hash) application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.entity_significance IS
    'Glicko-2 trust per (entity, arena, attestation_type). Hash-only entity reference. Partitioned by context_type_id. Stratified by attestation_type so kinds of evidence remain distinguishable; query-time blend collapses them when desired.';

-- ── sql/schema/tables/core/entity_significance_lexical.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_lexical
    PARTITION OF substrate.entity_significance FOR VALUES IN (1);

-- ── sql/schema/tables/core/entity_significance_syntactic.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_syntactic
    PARTITION OF substrate.entity_significance FOR VALUES IN (2);

-- ── sql/schema/tables/core/entity_significance_translation.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_translation
    PARTITION OF substrate.entity_significance FOR VALUES IN (3);

-- ── sql/schema/tables/core/entity_significance_model.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_model
    PARTITION OF substrate.entity_significance FOR VALUES IN (4);

-- ── sql/schema/tables/core/entity_significance_authority.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_authority
    PARTITION OF substrate.entity_significance FOR VALUES IN (5);

-- ── sql/schema/tables/core/entity_significance_relevance.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_relevance
    PARTITION OF substrate.entity_significance FOR VALUES IN (6);

-- ── sql/schema/tables/core/entity_significance_corroboration.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_corroboration
    PARTITION OF substrate.entity_significance FOR VALUES IN (7);

-- ── sql/schema/tables/core/entity_significance_frequency.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_frequency
    PARTITION OF substrate.entity_significance FOR VALUES IN (8);

-- ── sql/schema/tables/core/entity_significance_attention.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_attention
    PARTITION OF substrate.entity_significance FOR VALUES IN (9);

-- ── sql/schema/tables/core/entity_significance_morphological.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_morphological
    PARTITION OF substrate.entity_significance FOR VALUES IN (10);

-- ── sql/schema/tables/core/entity_significance_default.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_default
    PARTITION OF substrate.entity_significance DEFAULT;

-- ── sql/schema/tables/core/edge_significance.sql ───────────────────────────────────────
-- Glicko-2 ratings on edges, per arena, per attestation_type. Edge cost
-- during A* traversal = 1 / blended_mu where blended_mu is computed at
-- query time from per-attestation_type rows under an AttestationTypeBlend
-- recipe (default: equal weight across attestation_types within arena).
--
-- New arenas (open vocabulary) get inline priors at edge-emit for every
-- edge inserted after the arena is created; the pipeline reloads the
-- (provenance × edge_type × arena) primer table at its next startup.
-- AP-37: no end-of-phase post-pass.
--
-- attestation_type_id stratifies the rating: same edge gets separate rows
-- per (arena, attestation_type) so corpus-window evidence, model-circuit
-- evidence, lexicon-curated evidence, and inference-outcome evidence remain
-- distinguishable. The recomposer's WHERE clause and the inference engine's
-- traversal blend can both filter by attestation_type to pull
-- circuit-only-students, lexicon-only-students, etc.
CREATE TABLE substrate.edge_significance (
    context_type_id     INT NOT NULL REFERENCES substrate.significance_context(id),
    edge_type_id        INT NOT NULL,
    edge_hash           substrate.hash_value NOT NULL,
    attestation_type_id INT NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma               substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility          substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash, attestation_type_id)
    -- FK to substrate.edge application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.edge_significance IS
    'Glicko-2 trust per (edge, arena, attestation_type). Hash-addressable via (edge_type_id, edge_hash). Partitioned by context_type_id. Stratified by attestation_type so kinds of evidence (corpus, model, lexicon, outcome) remain distinguishable; query-time AttestationTypeBlend collapses them.';

-- ── sql/schema/tables/core/edge_significance_lexical.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_lexical
    PARTITION OF substrate.edge_significance FOR VALUES IN (1);

-- ── sql/schema/tables/core/edge_significance_syntactic.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_syntactic
    PARTITION OF substrate.edge_significance FOR VALUES IN (2);

-- ── sql/schema/tables/core/edge_significance_translation.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_translation
    PARTITION OF substrate.edge_significance FOR VALUES IN (3);

-- ── sql/schema/tables/core/edge_significance_model.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_model
    PARTITION OF substrate.edge_significance FOR VALUES IN (4);

-- ── sql/schema/tables/core/edge_significance_authority.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_authority
    PARTITION OF substrate.edge_significance FOR VALUES IN (5);

-- ── sql/schema/tables/core/edge_significance_relevance.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_relevance
    PARTITION OF substrate.edge_significance FOR VALUES IN (6);

-- ── sql/schema/tables/core/edge_significance_corroboration.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_corroboration
    PARTITION OF substrate.edge_significance FOR VALUES IN (7);

-- ── sql/schema/tables/core/edge_significance_frequency.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_frequency
    PARTITION OF substrate.edge_significance FOR VALUES IN (8);

-- ── sql/schema/tables/core/edge_significance_attention.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_attention
    PARTITION OF substrate.edge_significance FOR VALUES IN (9);

-- ── sql/schema/tables/core/edge_significance_morphological.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_morphological
    PARTITION OF substrate.edge_significance FOR VALUES IN (10);

-- ── sql/schema/tables/core/edge_significance_default.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_default
    PARTITION OF substrate.edge_significance DEFAULT;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (Removed 2026-05-09 per architectural correction: per-decomposition-event log was
-- over-engineered. The Glicko-2 aggregation in edge_significance IS the consensus
-- — same edge across N models = same edge hash = ONE row, with cross-source
-- corroboration accumulating as Glicko updates on that row, not new rows.
-- Per-event provenance/history/audit is out of scope for substrate-as-AI; if
-- ever needed for IP attribution it becomes a per-(source, edge) aggregate
-- counter, not a per-event log. See AP-22 for the row-vs-rating-event dedup
-- distinction that makes this work.)

-- ── Phase 8: junction tables ─────────────────────────────────────────

-- ── sql/schema/tables/junctions/entity_pos.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_pos (
    entity_hash         substrate.hash_value NOT NULL,
    pos_id              INT  NOT NULL REFERENCES substrate.pos(id),
    attestation_type_id INT  NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  FLOAT8 NOT NULL DEFAULT 1500,
    sigma               FLOAT8 NOT NULL DEFAULT 350,
    volatility          FLOAT8 NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, pos_id, attestation_type_id)
);

COMMENT ON TABLE substrate.entity_pos IS
    'Entity → POS classification with Glicko-2 confidence, stratified by attestation_type (e.g., lexical_curated_relation from POS lexicons vs. model_attention_pattern when a model''s heads attend with POS-aligned patterns). Hash-only entity reference. Multiple POS per entity supported.';

-- ── sql/schema/tables/junctions/entity_lexname.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_lexname (
    entity_hash substrate.hash_value NOT NULL,
    lexname_id  INT  NOT NULL REFERENCES substrate.lexname(id),
    PRIMARY KEY (entity_hash, lexname_id)
);

COMMENT ON TABLE substrate.entity_lexname IS
    'Entity → lexname. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/entity_language.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_language (
    entity_hash substrate.hash_value NOT NULL,
    language_id INT  NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_hash, language_id)
);

COMMENT ON TABLE substrate.entity_language IS
    'Entity → language. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/entity_morph_feature.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_morph_feature (
    entity_hash      substrate.hash_value NOT NULL,
    morph_feature_id INT  NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_hash, morph_feature_id)
);

COMMENT ON TABLE substrate.entity_morph_feature IS
    'Entity → morphological feature. Hash-only entity reference.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Per-codepoint UCD property analytics caches (Gate 1 #38 refactor 2026-05-18).
-- Replaces the deleted wide flat substrate.codepoint_property (25-column junction
-- with 7 indexes + 9 FKs — wrong substrate shape; substrate truth lives on the
-- has_cp_* typed edges in substrate.edge). These narrow per-property tables are
-- denormalized for index-locality on "all codepoints of property X" queries.

-- ── sql/schema/tables/junctions/cp_general_category.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_general_category (
    entity_hash         substrate.hash_value NOT NULL,
    general_category_id INT NOT NULL REFERENCES substrate.general_category(id),
    PRIMARY KEY (entity_hash, general_category_id)
);

COMMENT ON TABLE substrate.cp_general_category IS
    'Codepoint → UAX #44 General_Category narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_general_category typed edge on substrate.edge; this junction is the index-locality denormalization for fast "all codepoints of GC X" queries.';

-- ── sql/schema/tables/junctions/cp_script.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_script (
    entity_hash substrate.hash_value NOT NULL,
    script_id   INT NOT NULL REFERENCES substrate.script(id),
    PRIMARY KEY (entity_hash, script_id)
);

COMMENT ON TABLE substrate.cp_script IS
    'Codepoint → UAX #24 / ISO 15924 Script narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_script typed edge.';

-- ── sql/schema/tables/junctions/cp_block.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_block (
    entity_hash substrate.hash_value NOT NULL,
    block_id    INT NOT NULL REFERENCES substrate.block(id),
    PRIMARY KEY (entity_hash, block_id)
);

COMMENT ON TABLE substrate.cp_block IS
    'Codepoint → UAX #44 Block narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_block typed edge.';

-- ── sql/schema/tables/junctions/cp_bidi_class.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_bidi_class (
    entity_hash   substrate.hash_value NOT NULL,
    bidi_class_id INT NOT NULL REFERENCES substrate.bidi_class(id),
    PRIMARY KEY (entity_hash, bidi_class_id)
);

COMMENT ON TABLE substrate.cp_bidi_class IS
    'Codepoint → UAX #9 Bidi_Class narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_bidi_class typed edge.';

-- ── sql/schema/tables/junctions/cp_east_asian_width.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_east_asian_width (
    entity_hash         substrate.hash_value NOT NULL,
    east_asian_width_id INT NOT NULL REFERENCES substrate.east_asian_width(id),
    PRIMARY KEY (entity_hash, east_asian_width_id)
);

COMMENT ON TABLE substrate.cp_east_asian_width IS
    'Codepoint → UAX #11 East_Asian_Width narrow per-property analytics cache. AP-8 corrected: substrate truth is the has_cp_east_asian_width typed edge.';

-- ── sql/schema/tables/junctions/cp_grapheme_break.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_grapheme_break (
    entity_hash       substrate.hash_value NOT NULL,
    break_property_id INT NOT NULL REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_hash, break_property_id)
);

COMMENT ON TABLE substrate.cp_grapheme_break IS
    'Codepoint → UAX #29 Grapheme_Cluster_Break (GCB) narrow per-property analytics cache. break_property_id must reference a substrate.break_property row whose category = "GCB". AP-8 corrected: substrate truth is the has_cp_grapheme_break typed edge.';

-- ── sql/schema/tables/junctions/cp_word_break.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_word_break (
    entity_hash       substrate.hash_value NOT NULL,
    break_property_id INT NOT NULL REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_hash, break_property_id)
);

COMMENT ON TABLE substrate.cp_word_break IS
    'Codepoint → UAX #29 Word_Break (WB) narrow per-property analytics cache. break_property_id must reference a substrate.break_property row whose category = "WB". AP-8 corrected: substrate truth is the has_cp_word_break typed edge.';

-- ── sql/schema/tables/junctions/cp_sentence_break.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_sentence_break (
    entity_hash       substrate.hash_value NOT NULL,
    break_property_id INT NOT NULL REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_hash, break_property_id)
);

COMMENT ON TABLE substrate.cp_sentence_break IS
    'Codepoint → UAX #29 Sentence_Break (SB) narrow per-property analytics cache. break_property_id must reference a substrate.break_property row whose category = "SB". AP-8 corrected: substrate truth is the has_cp_sentence_break typed edge.';

-- ── sql/schema/tables/junctions/cp_line_break.sql ───────────────────────────────────────
CREATE TABLE substrate.cp_line_break (
    entity_hash       substrate.hash_value NOT NULL,
    break_property_id INT NOT NULL REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_hash, break_property_id)
);

COMMENT ON TABLE substrate.cp_line_break IS
    'Codepoint → UAX #14 Line_Break (LB) narrow per-property analytics cache. break_property_id must reference a substrate.break_property row whose category = "LB". AP-8 corrected: substrate truth is the has_cp_line_break typed edge.';

-- ── sql/schema/tables/junctions/model_architecture_class.sql ───────────────────────────────────────
CREATE TABLE substrate.model_architecture_class (
    entity_hash           substrate.hash_value NOT NULL,
    architecture_class_id INT  NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_hash, architecture_class_id)
);

COMMENT ON TABLE substrate.model_architecture_class IS
    'Model entity → architecture class. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/tensor_tensor_role.sql ───────────────────────────────────────
CREATE TABLE substrate.tensor_tensor_role (
    entity_hash    substrate.hash_value NOT NULL,
    tensor_role_id INT  NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_hash, tensor_role_id)
);

COMMENT ON TABLE substrate.tensor_tensor_role IS
    'Tensor entity → role. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/pattern_deprel.sql ───────────────────────────────────────
CREATE TABLE substrate.pattern_deprel (
    entity_hash         substrate.hash_value NOT NULL,
    deprel_id           INT  NOT NULL REFERENCES substrate.deprel(id),
    attestation_type_id INT  NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  FLOAT8 NOT NULL DEFAULT 1200,
    sigma               FLOAT8 NOT NULL DEFAULT 350,
    volatility          FLOAT8 NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, deprel_id, attestation_type_id)
);

COMMENT ON TABLE substrate.pattern_deprel IS
    'Attention pattern → deprel binding with Glicko-2 confidence, stratified by attestation_type. Most events arrive as model_attention_pattern (decomposed model heads aligned with UD deprels) and lexical_curated_relation (UD treebank labels). Hash-only entity reference.';

-- ── sql/schema/tables/junctions/provenance_edge_authority.sql ───────────────────────────────────────
-- substrate.provenance_edge_authority — explicit overrides for (source, edge_type) μ.
--
-- The default initial_μ for an edge is computed:
--   p.initial_mu × et.semantic_weight × p.derivation_decay
--
-- That's right for most cases — a source's per-modality authority times the
-- structural value of the edge-kind it's emitting, with optional lineage
-- decay. But some sources have specialty authority that breaks the default
-- product: Wiktionary's etymology is much stronger than the default would
-- give (Wiktionary.initial_mu × has_etymology.semantic_weight); WordNet's
-- etymology is much weaker than the default would give (WordNet's general
-- authority is high but it's not curating etymology).
--
-- Explicit rows in this table override the default for those specialty
-- combinations. PK = (provenance_id, edge_type_id).
CREATE TABLE substrate.provenance_edge_authority (
    provenance_id INT    NOT NULL REFERENCES substrate.provenance(id),
    edge_type_id  INT    NOT NULL REFERENCES substrate.edge_type(id),
    initial_mu    FLOAT8 NOT NULL,
    initial_sigma FLOAT8 NOT NULL DEFAULT 350.0,
    PRIMARY KEY (provenance_id, edge_type_id)
);

COMMENT ON TABLE substrate.provenance_edge_authority IS
    'Explicit (source × edge_type) μ/σ overrides. Powers the COALESCE in prime_edge_significance_for_staging — used when a source has specialty authority that doesn''t match the default p.initial_mu × et.semantic_weight × p.derivation_decay product.';

-- ── sql/schema/tables/junctions/entity_classification.sql ───────────────────────────────────────
-- Per-entity classification metadata. Content (entity_hash) is identity;
-- classification (entity_type_id) is metadata. Multiple decomposers can
-- independently assert classifications on the same content; provenance
-- distinguishes them. ("dog" attested as word_form by Tatoeba and as lemma
-- by WordNet → two classification rows, one entity row.)
CREATE TABLE IF NOT EXISTS substrate.entity_classification (
    entity_hash    substrate.hash_value NOT NULL,
    entity_type_id INT  NOT NULL REFERENCES substrate.entity_type(id),
    provenance_id  INT  NOT NULL REFERENCES substrate.provenance(id),
    asserted_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (entity_hash, entity_type_id, provenance_id)
);

COMMENT ON TABLE substrate.entity_classification IS
    'Per-entity classification metadata. Content (entity_hash) is identity; classification (entity_type_id) is metadata. Multiple decomposers can independently assert classifications on the same content; provenance distinguishes them.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- provenance_modality.sql moved to Phase 5b above (seed-time forward reference)

-- ── Phase 8b: post-junction seed (depends on junction tables existing) ─

-- ── sql/schema/seed/provenance_edge_authority.sql ───────────────────────────────────────
-- substrate.provenance_edge_authority seed — specialty (source × edge_type) μ overrides.
--
-- One INSERT...SELECT against a VALUES CTE; codes resolve to ids via JOIN once.
-- The default prior μ = p.initial_mu × et.semantic_weight × p.derivation_decay
-- is right for most cases. Rows here override for combinations where source
-- authority on a specific edge-kind diverges from the multiplicative product.

INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, o.initial_mu, o.initial_sigma
  FROM (VALUES
    -- Wiktionary IS the etymology / pronunciation / hyphenation authority.
    ('wiktextract',       'has_etymology',     95000.0,  80.0),
    ('wiktextract',       'has_pronunciation', 95000.0,  80.0),
    ('wiktextract',       'has_hyphenation',   90000.0, 100.0),
    -- WordNet has etymology / pronunciation but they're weak, not its specialty.
    ('princeton_wordnet', 'has_etymology',     20000.0, 500.0),
    ('princeton_wordnet', 'has_pronunciation', 15000.0, 600.0),
    -- Tatoeba IS the bilingual sentence-pair and audio authority.
    ('tatoeba',           'translation_link',  85000.0, 100.0),
    ('tatoeba',           'recording_of',      85000.0, 100.0)
  ) AS o(provenance_code, edge_type_code, initial_mu, initial_sigma)
  JOIN substrate.provenance p  ON p.code  = o.provenance_code
  JOIN substrate.edge_type  et ON et.code = o.edge_type_code
ON CONFLICT (provenance_id, edge_type_id) DO NOTHING;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 9: model tables ────────────────────────────────────────────

-- ── sql/schema/tables/models/model_registry.sql ───────────────────────────────────────
CREATE TABLE substrate.model_registry (
    id            SERIAL PRIMARY KEY,
    name          VARCHAR(256) NOT NULL UNIQUE,
    architecture  VARCHAR(64),
    parameters    BIGINT,
    license       VARCHAR(128),
    description   TEXT,
    homepage_url  TEXT,
    paper_url     TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE substrate.model_registry IS
    'Catalog of model families. Metadata about ingestible models — not substrate identity.';

-- ── sql/schema/tables/models/model_publisher.sql ───────────────────────────────────────
CREATE TABLE substrate.model_publisher (
    id           SERIAL PRIMARY KEY,
    name         VARCHAR(256) NOT NULL UNIQUE,
    organization VARCHAR(256),
    homepage_url TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE substrate.model_publisher IS
    'Publishers of model artifacts (Meta, Mistral, Anthropic, OpenAI, etc.).';

-- ── sql/schema/tables/models/model_source.sql ───────────────────────────────────────
CREATE TABLE substrate.model_source (
    id              SERIAL PRIMARY KEY,
    model_id        INT NOT NULL REFERENCES substrate.model_registry(id),
    publisher_id    INT NOT NULL REFERENCES substrate.model_publisher(id),
    source_path     TEXT NOT NULL,
    source_format   VARCHAR(32) NOT NULL,
    revision_label  VARCHAR(64),
    -- Plain bytea: HuggingFace revisions are SHA-1 git hashes (20 bytes), not BLAKE3,
    -- so we can't constrain to substrate.hash_value's 32-byte length.
    revision_hash   BYTEA,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (model_id, source_path, revision_label)
);

COMMENT ON TABLE substrate.model_source IS
    'Specific ingestion sources: model + publisher + revision. Multiple revisions of one model produce multiple model_source rows.';

-- ── sql/schema/tables/models/model_pass_checkpoint.sql ───────────────────────────────────────
CREATE TABLE substrate.model_pass_checkpoint (
    id              SERIAL PRIMARY KEY,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    pass_name       VARCHAR(64) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at    TIMESTAMPTZ,
    rows_emitted    BIGINT NOT NULL DEFAULT 0,
    error_message   TEXT,
    UNIQUE (model_source_id, pass_name)
);

COMMENT ON TABLE substrate.model_pass_checkpoint IS
    'Per-pass progress for safetensors decomposition. Lets a multi-pass ingestion resume after interruption.';

-- ── sql/schema/tables/models/entity_model_source.sql ───────────────────────────────────────
-- substrate.entity is HASH-partitioned by hash_bits_0_51; PG does not
-- accept FKs to a non-unique single-column key. entity_hash FK is
-- application-enforced (decomposers emit the entity row in the same
-- bundle/transaction as the junction). Same pattern as substrate.physicality
-- and substrate.edge_member.
CREATE TABLE substrate.entity_model_source (
    entity_hash     substrate.hash_value NOT NULL,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    PRIMARY KEY (entity_hash, model_source_id)
);

COMMENT ON TABLE substrate.entity_model_source IS
    'Entity → model_source provenance. Hash-only entity reference (FK to substrate.entity application-enforced — entity is HASH-partitioned). Same tensor in N model revisions has 1 entity row + N entity_model_source rows.';

-- ── sql/schema/tables/models/safetensor_observation.sql ───────────────────────────────────────
CREATE TABLE substrate.safetensor_observation (
    id                     BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    observed_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    model_source_id         INT REFERENCES substrate.model_source(id),
    context_type_id         INT NOT NULL REFERENCES substrate.significance_context(id),
    attestation_type_id    INT NOT NULL REFERENCES substrate.attestation_type(id),
    edge_type_id            INT NOT NULL REFERENCES substrate.edge_type(id),
    edge_hash               substrate.hash_value NOT NULL,
    score                  DOUBLE PRECISION NOT NULL,
    weight                 DOUBLE PRECISION NOT NULL,
    -- Entity FK is application-enforced — substrate.entity is HASH-partitioned
    -- by hash_bits_0_51 and PG does not accept FKs to a non-unique single column.
    tensor_hash             substrate.hash_value,
    package_tensor_hash     substrate.hash_value,
    source_tensor_name      TEXT,
    primitive_code          TEXT,
    tuple_code              TEXT,
    slot_code               TEXT,
    modality_code           TEXT,
    layer_index             INT,
    head_index              INT,
    expert_index            INT,
    adapter_name            TEXT,
    fused_slice             TEXT
);

COMMENT ON TABLE substrate.safetensor_observation IS
    'Durable source/placement-aware safetensor evidence events. edge_significance remains the aggregate consensus; this ledger preserves which model package/tensor placement produced each observation for recomposition filters.';

-- ── sql/schema/tables/reference/embedding_alignment_anchor.sql ───────────────────────────────────────
-- substrate.embedding_alignment_anchor
--
-- Phase C2 cross-model embedding alignment via orthogonal Procrustes
-- (EmbeddingAlignmentPass). Per-model Laplacian eigenmaps produce firefly
-- coordinates that are arbitrary up to rotation+reflection. Without
-- alignment, two models' fireflies for the same shared bpe_token sit in
-- independent eigenspaces and never converge — Voronoi consensus over the
-- shared entity is ill-defined.
--
-- This table tracks the canonical anchor: the first ingested model with
-- sufficient vocab becomes the anchor; every subsequent model is rotated
-- into the anchor's frame via Kabsch/Procrustes. First-write-wins via
-- ON CONFLICT DO NOTHING in substrate.claim_or_get_embedding_anchor.

CREATE TABLE IF NOT EXISTS substrate.embedding_alignment_anchor (
    model_source_id INT PRIMARY KEY REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    vocab_intersection_token_count INT NOT NULL,
    set_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE substrate.embedding_alignment_anchor IS
    'The single canonical model whose firefly frame all other models align to via Procrustes. First-write-wins: the first model with sufficient vocab intersection becomes the anchor; every subsequent EmbeddingAlignmentPass run rotates against this anchor. Phase C2.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 10: monitor tables ─────────────────────────────────────────

-- ── sql/schema/tables/monitor/ingestion_progress.sql ───────────────────────────────────────
CREATE TABLE monitor.ingestion_progress (
    id              BIGSERIAL PRIMARY KEY,
    provenance_code VARCHAR(64) NOT NULL,
    pass_name       VARCHAR(64) NOT NULL,
    batch_number    INT NOT NULL,
    entities_total  BIGINT NOT NULL DEFAULT 0,
    edges_total     BIGINT NOT NULL DEFAULT 0,
    current_file    TEXT,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.ingestion_progress IS
    'Per-batch ingestion telemetry. Operational, not part of substrate identity.';

-- ── sql/schema/tables/monitor/phase_status.sql ───────────────────────────────────────
CREATE TABLE monitor.phase_status (
    phase_code    VARCHAR(64) PRIMARY KEY,
    status        VARCHAR(32) NOT NULL,
    started_at    TIMESTAMPTZ,
    completed_at  TIMESTAMPTZ,
    error_message TEXT
);
COMMENT ON TABLE monitor.phase_status IS
    'Last known status per phase code (UcdUca, Iso639, WordNetOmw, ...). Updated by SequentialPhaseRunner.';

-- ── sql/schema/tables/monitor/error_log.sql ───────────────────────────────────────
CREATE TABLE monitor.error_log (
    id             BIGSERIAL PRIMARY KEY,
    phase_code     VARCHAR(64),
    decomposer     VARCHAR(128),
    error_class    VARCHAR(128),
    error_message  TEXT NOT NULL,
    stack_trace    TEXT,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.error_log IS
    'Decomposer + pipeline errors with phase context for post-mortem.';

-- ── sql/schema/tables/monitor/substrate_health.sql ───────────────────────────────────────
CREATE TABLE monitor.substrate_health (
    id          BIGSERIAL PRIMARY KEY,
    metric_code VARCHAR(64) NOT NULL,
    metric_value FLOAT8,
    notes       TEXT,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.substrate_health IS
    'Periodic substrate-state metrics: entity count, edge count, geometry coverage, frayed edge count, etc.';

-- ── sql/schema/tables/monitor/inference_metrics.sql ───────────────────────────────────────
CREATE TABLE monitor.inference_metrics (
    id              BIGSERIAL PRIMARY KEY,
    session_id      UUID,
    arena_code      VARCHAR(64),
    seed_count      INT,
    nodes_visited   INT,
    paths_returned  INT,
    elapsed_ms      INT,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.inference_metrics IS
    'Per-traversal latency + path-count telemetry.';

-- ── sql/schema/tables/monitor/session.sql ───────────────────────────────────────
CREATE TABLE monitor.session (
    id              UUID PRIMARY KEY,
    user_label      VARCHAR(256),
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at        TIMESTAMPTZ,
    notes           TEXT
);

COMMENT ON TABLE monitor.session IS
    'Inference / interactive sessions. session_id is the FK target for comparison_event and inference_metrics.';

-- ── sql/schema/tables/monitor/comparison_event.sql ───────────────────────────────────────
-- A Glicko-2 comparison event between two paths/edges/entities. Outcome is
-- the input to the per-arena rating update. winner_kind / loser_kind:
-- 'N' = entity (node), 'E' = edge.
CREATE TABLE monitor.comparison_event (
    id              BIGSERIAL PRIMARY KEY,
    session_id      UUID REFERENCES monitor.session(id) ON DELETE SET NULL,
    arena_code      VARCHAR(64) NOT NULL,
    winner_kind     CHAR(1) NOT NULL CHECK (winner_kind IN ('N', 'E')),
    winner_type_id  INT NOT NULL,
    winner_hash     substrate.hash_value NOT NULL,
    loser_kind      CHAR(1) NOT NULL CHECK (loser_kind IN ('N', 'E')),
    loser_type_id   INT NOT NULL,
    loser_hash      substrate.hash_value NOT NULL,
    outcome_score   FLOAT8 NOT NULL DEFAULT 1.0,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.comparison_event IS
    'Glicko-2 comparison events between substrate items. Drives entity_significance / edge_significance updates.';

-- ── sql/schema/tables/monitor/significance_snapshot.sql ───────────────────────────────────────
CREATE TABLE monitor.significance_snapshot (
    id              BIGSERIAL PRIMARY KEY,
    arena_code      VARCHAR(64) NOT NULL,
    target_kind     CHAR(1) NOT NULL CHECK (target_kind IN ('N', 'E')),
    target_type_id  INT NOT NULL,
    target_hash     substrate.hash_value NOT NULL,
    mu              FLOAT8 NOT NULL,
    sigma           FLOAT8 NOT NULL,
    volatility      FLOAT8 NOT NULL,
    games           INT NOT NULL,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.significance_snapshot IS
    'Periodic snapshots of significance state for time-series analysis.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 11: meta tables ────────────────────────────────────────────
--  (no meta tables — drain-completion post-passes were removed; the
--  arena_priming_state watermark table is gone with them.)

-- ── Phase 12: indexes ─────────────────────────────────────────────────

-- ── sql/schema/indexes/idx_block_range.sql ───────────────────────────────────────
CREATE INDEX idx_block_range ON substrate.block(range_start, range_end);

-- ── sql/schema/indexes/idx_break_property_category.sql ───────────────────────────────────────
CREATE INDEX idx_break_property_category ON substrate.break_property(category);

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Per-codepoint UCD property analytics-cache reverse indexes (Gate 1 #38).

-- ── sql/schema/indexes/idx_cp_general_category_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_general_category_by_id ON substrate.cp_general_category(general_category_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_script_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_script_by_id ON substrate.cp_script(script_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_block_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_block_by_id ON substrate.cp_block(block_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_bidi_class_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_bidi_class_by_id ON substrate.cp_bidi_class(bidi_class_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_east_asian_width_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_east_asian_width_by_id ON substrate.cp_east_asian_width(east_asian_width_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_grapheme_break_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_grapheme_break_by_id ON substrate.cp_grapheme_break(break_property_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_word_break_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_word_break_by_id ON substrate.cp_word_break(break_property_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_sentence_break_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_sentence_break_by_id ON substrate.cp_sentence_break(break_property_id, entity_hash);

-- ── sql/schema/indexes/idx_cp_line_break_by_id.sql ───────────────────────────────────────
CREATE INDEX idx_cp_line_break_by_id ON substrate.cp_line_break(break_property_id, entity_hash);

-- ── sql/schema/indexes/idx_comparison_event_arena.sql ───────────────────────────────────────
CREATE INDEX idx_comparison_event_arena   ON monitor.comparison_event(arena_code, recorded_at DESC);

-- ── sql/schema/indexes/idx_comparison_event_session.sql ───────────────────────────────────────
CREATE INDEX idx_comparison_event_session ON monitor.comparison_event(session_id, recorded_at DESC);

-- ── sql/schema/indexes/idx_edge_type_category.sql ───────────────────────────────────────
CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);

-- ── sql/schema/indexes/idx_entity_classification_provenance.sql ───────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_entity_classification_provenance
    ON substrate.entity_classification(provenance_id);

-- ── sql/schema/indexes/idx_entity_classification_type.sql ───────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_entity_classification_type
    ON substrate.entity_classification(entity_type_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_language_lang.sql ───────────────────────────────────────
CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_lexname_lexname.sql ───────────────────────────────────────
CREATE INDEX idx_entity_lexname_lexname ON substrate.entity_lexname(lexname_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_model_source_source.sql ───────────────────────────────────────
CREATE INDEX idx_entity_model_source_source ON substrate.entity_model_source(model_source_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_morph_feature_feat.sql ───────────────────────────────────────
CREATE INDEX idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_pos_pos.sql ───────────────────────────────────────
CREATE INDEX idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_type_modality.sql ───────────────────────────────────────
CREATE INDEX idx_entity_type_modality ON substrate.entity_type(modality);

-- ── sql/schema/indexes/idx_error_log_recent.sql ───────────────────────────────────────
CREATE INDEX idx_error_log_recent ON monitor.error_log(occurred_at DESC);

-- ── sql/schema/indexes/idx_general_category_group.sql ───────────────────────────────────────
CREATE INDEX idx_general_category_group ON substrate.general_category(group_code);

-- ── sql/schema/indexes/idx_inference_metrics_recent.sql ───────────────────────────────────────
CREATE INDEX idx_inference_metrics_recent  ON monitor.inference_metrics(recorded_at DESC);

-- ── sql/schema/indexes/idx_inference_metrics_session.sql ───────────────────────────────────────
CREATE INDEX idx_inference_metrics_session ON monitor.inference_metrics(session_id, recorded_at DESC);

-- ── sql/schema/indexes/idx_ingestion_progress_recent.sql ───────────────────────────────────────
CREATE INDEX idx_ingestion_progress_recent ON monitor.ingestion_progress(recorded_at DESC);

-- ── sql/schema/indexes/idx_language_scope.sql ───────────────────────────────────────
CREATE INDEX idx_language_scope ON substrate.language(scope);

-- ── sql/schema/indexes/idx_language_type.sql ───────────────────────────────────────
CREATE INDEX idx_language_type ON substrate.language(type);

-- ── sql/schema/indexes/idx_language_part1.sql ───────────────────────────────────────
CREATE UNIQUE INDEX idx_language_part1 ON substrate.language(part1) WHERE part1 IS NOT NULL;

-- ── sql/schema/indexes/idx_language_part2b.sql ───────────────────────────────────────
CREATE INDEX idx_language_part2b ON substrate.language(part2b) WHERE part2b IS NOT NULL;

-- ── sql/schema/indexes/idx_language_part2t.sql ───────────────────────────────────────
CREATE INDEX idx_language_part2t ON substrate.language(part2t) WHERE part2t IS NOT NULL;

-- ── sql/schema/indexes/idx_model_arch_class.sql ───────────────────────────────────────
CREATE INDEX idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_hash);

-- ── sql/schema/indexes/idx_model_pass_checkpoint_source.sql ───────────────────────────────────────
CREATE INDEX idx_model_pass_checkpoint_source ON substrate.model_pass_checkpoint(model_source_id);

-- ── sql/schema/indexes/idx_model_source_model.sql ───────────────────────────────────────
CREATE INDEX idx_model_source_model     ON substrate.model_source(model_id);

-- ── sql/schema/indexes/idx_model_source_publisher.sql ───────────────────────────────────────
CREATE INDEX idx_model_source_publisher ON substrate.model_source(publisher_id);

-- ── sql/schema/indexes/idx_morph_feature_key.sql ───────────────────────────────────────
CREATE INDEX idx_morph_feature_key ON substrate.morph_feature(key);

-- ── sql/schema/indexes/idx_pattern_deprel_deprel.sql ───────────────────────────────────────
CREATE INDEX idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_hash);

-- ── sql/schema/indexes/idx_safetensor_observation_edge.sql ───────────────────────────────────────
CREATE INDEX idx_safetensor_observation_edge
    ON substrate.safetensor_observation (edge_type_id, edge_hash, context_type_id, attestation_type_id);

-- ── sql/schema/indexes/idx_safetensor_observation_source.sql ───────────────────────────────────────
CREATE INDEX idx_safetensor_observation_source
    ON substrate.safetensor_observation (model_source_id, tuple_code, slot_code, layer_index, head_index, expert_index);

-- ── sql/schema/indexes/idx_safetensor_observation_tensor.sql ───────────────────────────────────────
CREATE INDEX idx_safetensor_observation_tensor
    ON substrate.safetensor_observation (package_tensor_hash, tensor_hash);

-- ── sql/schema/indexes/idx_session_started.sql ───────────────────────────────────────
CREATE INDEX idx_session_started ON monitor.session(started_at DESC);

-- ── sql/schema/indexes/idx_significance_snapshot_target.sql ───────────────────────────────────────
CREATE INDEX idx_significance_snapshot_target ON monitor.significance_snapshot(target_kind, target_type_id, target_hash, recorded_at DESC);

-- ── sql/schema/indexes/idx_substrate_health_code.sql ───────────────────────────────────────
CREATE INDEX idx_substrate_health_code   ON monitor.substrate_health(metric_code, recorded_at DESC);

-- ── sql/schema/indexes/idx_substrate_health_recent.sql ───────────────────────────────────────
CREATE INDEX idx_substrate_health_recent ON monitor.substrate_health(recorded_at DESC);

-- ── sql/schema/indexes/idx_tensor_role.sql ───────────────────────────────────────
CREATE INDEX idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_hash);

-- ── sql/schema/indexes/entity_hash_prefix_idx.sql ───────────────────────────────────────
-- Composite btree on (hash_bits_0_51, hash_bits_52_103). The read-side kernel
-- of SubstrateTierWalker: substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])
-- resolves trajectory-vertex (X, Z) mantissa slices to full BLAKE3 hashes in
-- one batched point lookup per tier. Without this index the lookup falls
-- back to a sequential scan over substrate.entity, defeating the whole
-- O(D)-tier-walks contract.
CREATE INDEX IF NOT EXISTS entity_hash_prefix_idx
    ON substrate.entity USING btree (hash_bits_0_51, hash_bits_52_103);

-- ── sql/schema/indexes/provenance_modality_modality_idx.sql ───────────────────────────────────────
-- Reverse-lookup index on substrate.provenance_modality: given a modality_code,
-- which provenance sources are authoritative? The composite PK
-- (provenance_id, modality_code) already serves forward lookup; this gives the
-- inverse without scanning the junction.
CREATE INDEX provenance_modality_modality_idx
    ON substrate.provenance_modality (modality_code);

-- ── sql/schema/indexes/edge_member_entity_hash_idx.sql ───────────────────────────────────────
-- Load-bearing index for the reverse-lookup pattern:
-- "find all edges in which entity X participates."
--
-- Used by SubstrateAdjacencyBuilder's self-join (the synth's vocab × vocab
-- adjacency query), VocabSelector's cross-WF degree count, FfnEdgeSlotSynthesizer's
-- edge selection, and any inference traversal that starts from an entity hash
-- and walks outward through its incident edges.
--
-- WITHOUT this index, those queries fall back to scanning the full edge_member
-- table (~10M+ rows per substrate state) per source entity. Synth adjacency
-- build measured at 30s for 256 vocab tokens via 134M-row scan — the
-- bottleneck the index removes.
--
-- The PK (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
-- supports forward lookup (given an edge, find its members) but cannot
-- service entity-first queries without this standalone index.
CREATE INDEX IF NOT EXISTS edge_member_entity_hash_idx
    ON substrate.edge_member (entity_hash);

-- ── sql/schema/indexes/entity_hilbert_idx.sql ───────────────────────────────────────
-- BTREE on substrate.entity.hilbert_index for log-N range scans by 4D
-- spatial locality. The Hilbert curve preserves locality: adjacent
-- hilbert values correspond to spatially-adjacent 4D points. Range
-- queries `WHERE hilbert_index BETWEEN $a AND $b` scan a 4D-spatial
-- box-like region.
--
-- Combined with the entity's radial tier (sqrt(x²+y²+z²+m²) — atoms ≈ 1,
-- documents ≈ 0), Hilbert ordering gives both ANGULAR (semantic direction)
-- and RADIAL (abstraction depth) locality in one B-tree scan.
CREATE INDEX IF NOT EXISTS entity_hilbert_idx ON substrate.entity (hilbert_index);

COMMENT ON INDEX substrate.entity_hilbert_idx IS
    '4D Hilbert-curve ordering of substrate.entity centroids. Range scans cluster entities by 4D spatial proximity, which combines angular direction (semantic similarity at atom tier) AND radial tier (Merkle DAG depth — atoms on glome, documents at origin). Enables log-N spatial-locality queries without per-row geometry computation.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (Persistent staging deleted post-W2E refactor: substrate.staging_* tables and the
--  drain_staging_*_chunk / drain_all_staging functions are gone. The
--  StreamingIngestionPipeline writes DIRECTLY into substrate core tables
--  via session-local pg_temp.X_inflight tables created per drain-task
--  connection. ON CONFLICT DO NOTHING guards within-session and cross-
--  session duplicates. Edge geometry + per-arena significance priors are
--  built inline at edge-emit inside the bundled-emit pipeline — no
--  populate_edge_trajectories, no prime_unprimed_edges_chunk, no
--  arena_priming_state, no drain-completion post-passes.)

-- ── Phase 13: functions ──────────────────────────────────────────────
-- Reference / utility helpers

-- ── sql/schema/functions/reference_code_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_code_map(p_table TEXT)
RETURNS TABLE(id INT, code TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    -- Validate the table identifier — only schema-qualified substrate.* names allowed.
    IF p_table !~ '^substrate\.[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference table: %', p_table;
    END IF;
    RETURN QUERY EXECUTE format('SELECT id, code::text FROM %s', p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_map(TEXT) IS
    'Generic loader: returns (id, code) for any reference table with id INT + code column.';

-- ── sql/schema/functions/reference_key_value_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_key_value_map(
    p_table       TEXT,
    p_key_column  TEXT,
    p_value_column TEXT
) RETURNS TABLE(id INT, key_text TEXT, value_text TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_key_column !~ '^[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference args: table=%, key=%, value=%', p_table, p_key_column, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT id, %I::text, %I::text FROM %s',
        p_key_column, p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_key_value_map(TEXT, TEXT, TEXT) IS
    'Generic loader: returns (id, key, value) for tables like morph_feature(key, value) or break_property(code, category).';

-- ── sql/schema/functions/reference_code_text_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_code_text_map(
    p_table        TEXT,
    p_value_column TEXT
) RETURNS TABLE(code TEXT, value_text TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, value=%', p_table, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT code::text, %I::text FROM %s',
        p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_text_map(TEXT, TEXT) IS
    'Generic loader: returns (code, some-other-text-column) for reference tables.';

-- ── sql/schema/functions/reference_code_double_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_code_double_map(
    p_table         TEXT,
    p_value_column  TEXT
) RETURNS TABLE(code TEXT, value_float FLOAT8)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, value=%', p_table, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT code::text, %I::float8 FROM %s',
        p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_double_map(TEXT, TEXT) IS
    'Generic loader: returns (code, float8-column) for reference tables. Used by '
    'CodeResolver to load provenance.initial_mu for inline edge significance emission.';

-- ── sql/schema/functions/reference_int64_set.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_int64_set(
    p_table  TEXT,
    p_column TEXT
) RETURNS TABLE(value BIGINT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, column=%', p_table, p_column;
    END IF;
    RETURN QUERY EXECUTE format('SELECT %I::bigint FROM %s', p_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_int64_set(TEXT, TEXT) IS
    'Generic loader: returns the BIGINT values of one column from a reference/junction table.';

-- ── sql/schema/functions/reference_id_by_code.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_id_by_code(
    p_table TEXT,
    p_code  TEXT
) RETURNS INT
LANGUAGE plpgsql STABLE
AS $$
DECLARE v_id INT;
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference table: %', p_table;
    END IF;
    EXECUTE format('SELECT id FROM %s WHERE code = $1', p_table)
        INTO v_id USING p_code;
    RETURN v_id;
END $$;
COMMENT ON FUNCTION substrate.reference_id_by_code(TEXT, TEXT) IS
    'Generic loader: return the SERIAL id for a single (code) lookup against any reference table.';

-- ── sql/schema/functions/reference_language_alias_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_language_alias_map()
RETURNS TABLE(id INT, code TEXT, part1 TEXT, part2b TEXT, part2t TEXT)
LANGUAGE sql STABLE
AS $$
    SELECT id, code::text, part1::text, part2b::text, part2t::text
    FROM substrate.language;
$$;
COMMENT ON FUNCTION substrate.reference_language_alias_map() IS
    'Returns the four ISO-form alias columns from substrate.language for building the canonical-id alias map (code, part1, part2b, part2t). ~8k rows.';

-- ── sql/schema/functions/resolve_context_id.sql ───────────────────────────────────────
-- substrate.resolve_context_id(p_code TEXT)
--
-- Translate a significance_context code (e.g. 'lexical_disambiguation',
-- 'semantic_relevance') to its INT id. Single-row lookup used by C# call
-- sites that translate arena codes to ids before invoking
-- substrate.record_comparison / record_corroboration / prune_significance.
--
-- Arenas are open-vocabulary (.claude/rules/15 § "Arenas are open-
-- vocabulary"). Code that hard-codes the 10 starter codes is wrong (AP-1);
-- this resolver works for any code present in substrate.significance_context.
--
-- Returns NULL when the code does not exist. Callers MUST handle NULL
-- (the C# updater raises InvalidOperationException with the unknown code).
CREATE OR REPLACE FUNCTION substrate.resolve_context_id(p_code TEXT)
RETURNS INT
LANGUAGE sql STABLE
AS $$
    SELECT id
      FROM substrate.significance_context
     WHERE code = p_code;
$$;

COMMENT ON FUNCTION substrate.resolve_context_id(TEXT) IS
    'Resolve a significance_context.code to its INT id. Returns NULL if unknown. STABLE — safe to inline in larger queries.';

-- ── sql/schema/functions/resolve_attestation_type_id.sql ───────────────────────────────────────
-- substrate.resolve_attestation_type_id(p_code TEXT)
--
-- Translate an attestation_type code to its INT id. After the P1d collapse
-- the vocabulary is exactly three rows — positive_evidence,
-- negative_evidence, neutral_evidence. Unknown codes raise. The SQL +
-- C# emission sites have been migrated to use the canonical three-row
-- vocabulary; a hard-fail here surfaces any regression instead of silently
-- recoding evidence under a default sign.
--
-- Returns the resolved id; raises EXCEPTION on unknown code.
CREATE OR REPLACE FUNCTION substrate.resolve_attestation_type_id(p_code TEXT)
RETURNS INT
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_id INT;
BEGIN
    SELECT id INTO v_id FROM substrate.attestation_type WHERE code = p_code;
    IF v_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type code: % (expected positive_evidence / negative_evidence / neutral_evidence per P1d)', p_code;
    END IF;
    RETURN v_id;
END;
$$;

COMMENT ON FUNCTION substrate.resolve_attestation_type_id(TEXT) IS
    'Resolve an attestation_type.code to its INT id. Raises EXCEPTION on unknown code — the substrate''s 3-row vocabulary (positive_evidence / negative_evidence / neutral_evidence per P1d) is the only valid input. No graceful fallback (anti-band-aid).';

-- ── sql/schema/functions/resolve_entity_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[], TEXT[]);
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.resolve_entity_handles(
    p_hashes BYTEA[], p_type_codes TEXT[]
) RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT et.code, e.hash
      FROM unnest(p_hashes) AS in_(h)
      JOIN substrate.entity e ON e.hash = in_.h
      JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
      JOIN unnest(p_type_codes) AS requested(code) ON requested.code = et.code
     GROUP BY et.code, e.hash
     ORDER BY et.code, e.hash;
$f$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Mantissa packing helpers — used by trajectory write/read and by the
-- entity_by_hash_prefix batched composite-btree lookup.

-- ── sql/schema/functions/bb_hash_lo.sql ───────────────────────────────────────
-- Mantissa packing helper: extract the lower 52 bits of a BLAKE3 hash as
-- BIGINT, little-endian byte order.
--
-- Layout (matches Hartonomous.Core.Compute.Common.MantissaPacking byte-for-byte):
--   bits 0..7   from byte 0
--   bits 8..15  from byte 1
--   bits 16..23 from byte 2
--   bits 24..31 from byte 3
--   bits 32..39 from byte 4
--   bits 40..47 from byte 5
--   bits 48..51 from low nibble of byte 6
-- Total: 52 bits.
--
-- Combined with bb_hash_hi this yields a 104-bit hash prefix per entity —
-- birthday collision at ~2^52 ≈ 5×10^15 entities, vastly safe at any
-- substrate scale.
CREATE OR REPLACE FUNCTION substrate.bb_hash_lo(p_hash substrate.hash_value)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT
          (get_byte(p_hash, 0)::BIGINT)
        | (get_byte(p_hash, 1)::BIGINT << 8)
        | (get_byte(p_hash, 2)::BIGINT << 16)
        | (get_byte(p_hash, 3)::BIGINT << 24)
        | (get_byte(p_hash, 4)::BIGINT << 32)
        | (get_byte(p_hash, 5)::BIGINT << 40)
        | ((get_byte(p_hash, 6) & 15)::BIGINT << 48)
$$;

COMMENT ON FUNCTION substrate.bb_hash_lo(substrate.hash_value) IS
    'Extract bits 0..51 of a BLAKE3 hash as BIGINT (LE byte order). Mirrors C# MantissaPacking byte-for-byte. Used to derive the substrate.entity.hash_bits_0_51 generated column and to seed substrate.entity_by_hash_prefix() lookup keys.';

-- ── sql/schema/functions/bb_hash_hi.sql ───────────────────────────────────────
-- Mantissa packing helper: extract bits 52..103 of a BLAKE3 hash as BIGINT.
--
-- Layout (matches Hartonomous.Core.Compute.Common.MantissaPacking byte-for-byte):
--   bits 0..3  from high nibble of byte 6
--   bits 4..11 from byte 7
--   bits 12..19 from byte 8
--   bits 20..27 from byte 9
--   bits 28..35 from byte 10
--   bits 36..43 from byte 11
--   bits 44..51 from byte 12
-- Total: 52 bits, packed into BIGINT in LE bit order.
CREATE OR REPLACE FUNCTION substrate.bb_hash_hi(p_hash substrate.hash_value)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT
          ((get_byte(p_hash, 6) >> 4) & 15)::BIGINT
        | (get_byte(p_hash, 7)::BIGINT << 4)
        | (get_byte(p_hash, 8)::BIGINT << 12)
        | (get_byte(p_hash, 9)::BIGINT << 20)
        | (get_byte(p_hash, 10)::BIGINT << 28)
        | (get_byte(p_hash, 11)::BIGINT << 36)
        | (get_byte(p_hash, 12)::BIGINT << 44)
$$;

COMMENT ON FUNCTION substrate.bb_hash_hi(substrate.hash_value) IS
    'Extract bits 52..103 of a BLAKE3 hash as BIGINT (LE byte order). Combined with bb_hash_lo this is a 104-bit hash prefix; collision-free at substrate scale. Used to derive substrate.entity.hash_bits_52_103 and to seed substrate.entity_by_hash_prefix() lookup keys.';

-- ── sql/schema/functions/bb_pack_hash_lo.sql ───────────────────────────────────────
-- Pack a 52-bit BIGINT into an IEEE-754 double's mantissa for use as a
-- LINESTRING4D / MULTILINESTRING4D vertex coordinate in
-- substrate.physicality 'content' rows.
--
-- Encoding: double = 2^52 + (value & 0x000FFFFFFFFFFFFF). The result is
-- exactly representable in IEEE-754 (the integer range [2^52, 2^53) sits
-- entirely in normal-double precision with no rounding); inversion is
-- exact via bb_unpack_hash_lo. Mirrors C# MantissaPacking.PackHashLo
-- byte-for-byte for cross-language determinism (Law #6).
CREATE OR REPLACE FUNCTION substrate.bb_pack_hash_lo(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_hash_lo(BIGINT) IS
    'Pack 52-bit hash-lo BIGINT into a double mantissa via 2^52 + value. Inverse: bb_unpack_hash_lo. Used for the X dimension of ingestion_trajectory vertices.';

-- ── sql/schema/functions/bb_pack_hash_hi.sql ───────────────────────────────────────
-- Pack a 52-bit BIGINT into an IEEE-754 double's mantissa, same encoding as
-- bb_pack_hash_lo. Used for the Z dimension of ingestion_trajectory vertices
-- (the upper half of the 104-bit child-hash prefix).
CREATE OR REPLACE FUNCTION substrate.bb_pack_hash_hi(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_hash_hi(BIGINT) IS
    'Pack 52-bit hash-hi BIGINT into a double mantissa via 2^52 + value. Inverse: bb_unpack_hash_hi. Used for the Z dimension of ingestion_trajectory vertices.';

-- ── sql/schema/functions/bb_unpack_hash_lo.sql ───────────────────────────────────────
-- Inverse of bb_pack_hash_lo. Subtract 2^52, cast to BIGINT — exact for any
-- value produced by the packer (no rounding because both 2^52 and 2^52 + v
-- are exactly representable IEEE-754 integers).
CREATE OR REPLACE FUNCTION substrate.bb_unpack_hash_lo(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_hash_lo(double precision) IS
    'Recover the 52-bit hash-lo BIGINT packed into a double by bb_pack_hash_lo. Used by ingestion_trajectory readers (composition_at, composition_range, recompose_text, etc.) to extract child-hash slices from LINESTRING4D vertex X mantissas.';

-- ── sql/schema/functions/bb_unpack_hash_hi.sql ───────────────────────────────────────
-- Inverse of bb_pack_hash_hi.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_hash_hi(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_hash_hi(double precision) IS
    'Recover the 52-bit hash-hi BIGINT packed into a double by bb_pack_hash_hi. Used by ingestion_trajectory readers to extract the upper half of the 104-bit child-hash prefix from LINESTRING4D vertex Z mantissas.';

-- ── sql/schema/functions/bb_pack_ordinal_rle.sql ───────────────────────────────────────
-- Pack (ordinal: int32, rle: int20) into a 52-bit BIGINT then into a double
-- mantissa. Bit layout:
--   bits 0..31  = ordinal (32 bits, 1-based vertex position)
--   bits 32..51 = rle     (20 bits, run-length encoding count)
--
-- Ordinal limit: 2^32 ≈ 4.3 billion vertices per trajectory.
-- RLE limit: 2^20 ≈ 1 million repeats per run.
-- Both fit comfortably in any practical substrate workload.
CREATE OR REPLACE FUNCTION substrate.bb_pack_ordinal_rle(p_ordinal INT, p_rle INT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (
               (p_ordinal::BIGINT & 4294967295)            -- low 32 bits
             | ((p_rle::BIGINT & 1048575) << 32)            -- next 20 bits
           )::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_ordinal_rle(INT, INT) IS
    'Pack (ordinal, rle) into the Y mantissa of an ingestion_trajectory vertex. Inverse: bb_unpack_ordinal + bb_unpack_rle. Used for vertex ordinal + RLE bookkeeping in LINESTRING4D / MULTILINESTRING4D recorded trajectories.';

-- ── sql/schema/functions/bb_unpack_ordinal.sql ───────────────────────────────────────
-- Recover the ordinal (low 32 bits) from a packed (ordinal, rle) Y mantissa.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_ordinal(p_double double precision)
RETURNS INT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (
        ((p_double - 4503599627370496.0::double precision)::BIGINT) & 4294967295
    )::INT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_ordinal(double precision) IS
    'Extract the 32-bit ordinal from an ingestion_trajectory vertex Y mantissa packed by bb_pack_ordinal_rle. Inverse companion: bb_unpack_rle.';

-- ── sql/schema/functions/bb_unpack_rle.sql ───────────────────────────────────────
-- Recover the RLE run-length (bits 32..51) from a packed (ordinal, rle) Y mantissa.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_rle(p_double double precision)
RETURNS INT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (
        (((p_double - 4503599627370496.0::double precision)::BIGINT) >> 32) & 1048575
    )::INT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_rle(double precision) IS
    'Extract the 20-bit RLE run-length from an ingestion_trajectory vertex Y mantissa packed by bb_pack_ordinal_rle.';

-- ── sql/schema/functions/bb_pack_metadata.sql ───────────────────────────────────────
-- Pack a 52-bit metadata BIGINT into a double mantissa. The 52 bits are
-- free-form per caller: attestation type, role flag, edge type discriminator,
-- sub-tier flag, etc. Same encoding (2^52 + value) as bb_pack_hash_lo.
CREATE OR REPLACE FUNCTION substrate.bb_pack_metadata(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_metadata(BIGINT) IS
    'Pack 52 bits of free-form metadata into the M mantissa of an ingestion_trajectory vertex. Inverse: bb_unpack_metadata.';

-- ── sql/schema/functions/bb_unpack_metadata.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.bb_unpack_metadata(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_metadata(double precision) IS
    'Recover the 52-bit metadata BIGINT packed by bb_pack_metadata from an ingestion_trajectory vertex M mantissa.';

-- ── sql/schema/functions/entity_by_hash_prefix.sql ───────────────────────────────────────
-- Batched composite btree lookup: given parallel arrays of 52-bit hash-lo
-- and 52-bit hash-hi prefixes (one per child to resolve), return matching
-- (hash_bits_0_51, hash_bits_52_103, hash) tuples from substrate.entity in
-- a single round trip.
--
-- The lookup is the read-side kernel of SubstrateTierWalker: per tier,
-- unpack each ingestion_trajectory vertex's X + Z mantissas into (lo, hi),
-- pass the arrays to this function, recover full hashes via the composite
-- btree on (hash_bits_0_51, hash_bits_52_103). One round trip per tier walk
-- regardless of fanout. No GiST k-NN, no reverse-spatial lookup.
--
-- Result preserves caller order: row[i] corresponds to (p_lo[i], p_hi[i])
-- when a match exists. Missing pairs are simply absent from the result.
-- Callers that need a NULL fill for missing pairs should LEFT JOIN this
-- result back to their input arrays in SQL.
CREATE OR REPLACE FUNCTION substrate.entity_by_hash_prefix(
    p_lo BIGINT[],
    p_hi BIGINT[]
)
RETURNS TABLE(
    hash_bits_0_51 BIGINT,
    hash_bits_52_103 BIGINT,
    hash substrate.hash_value
)
LANGUAGE SQL STABLE PARALLEL SAFE
AS $$
    SELECT e.hash_bits_0_51, e.hash_bits_52_103, e.hash
    FROM substrate.entity e
    JOIN unnest(p_lo, p_hi) AS probe(lo, hi)
      ON e.hash_bits_0_51   = probe.lo
     AND e.hash_bits_52_103 = probe.hi;
$$;

COMMENT ON FUNCTION substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]) IS
    'Batched composite-btree point lookup of substrate.entity rows by 104-bit hash prefix. The read-side kernel of SubstrateTierWalker: one call per tier returns all child hashes in that tier. Backed by the (hash_bits_0_51, hash_bits_52_103) btree composite index.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Cast bridges from PostGIS-native geometry(GeometryZM) to the internal
-- native compute ABI types (public.point4d / public.linestring4d). Used
-- internally by substrate.st_4d_* dispatch; not substrate-level surfaces.

-- ── sql/schema/functions/point4d_from_geometry.sql ───────────────────────────────────────
-- Bridge from PostGIS-native POINTZM geometry to the internal native compute
-- ABI type public.point4d (zero-marshalling flat (x,y,z,m) for libhartonomous
-- C kernels). Used internally by substrate.st_4d_* operator dispatch — every
-- substrate-level function takes geometry (the storage type) and converts at
-- the kernel boundary via this cast. public.point4d is NOT a substrate-level
-- user-visible type (per .claude/rules/25-physicality-4d.md); it's the
-- internal flat-array I/O ABI for the native kernels.
CREATE OR REPLACE FUNCTION public.point4d_from_geometry(g geometry)
RETURNS public.point4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.point4d(ST_X(g), ST_Y(g), ST_Z(g), ST_M(g))
$$;

COMMENT ON FUNCTION public.point4d_from_geometry(geometry) IS
    'Extract (X, Y, Z, M) from a POINTZM and construct the internal native point4d. Used by substrate.st_4d_* operator dispatch to bridge PostGIS storage to libhartonomous kernel I/O.';

CREATE CAST (geometry AS public.point4d)
    WITH FUNCTION public.point4d_from_geometry(geometry)
    AS ASSIGNMENT;

-- ── sql/schema/functions/linestring4d_from_geometry.sql ───────────────────────────────────────
-- Bridge from PostGIS-native LINESTRINGZM geometry to the internal native
-- compute ABI type public.linestring4d. Used internally by substrate.st_4d_*
-- operator dispatch — every substrate-level function takes geometry (the
-- storage type) and converts at the kernel boundary via this cast.
-- public.linestring4d is NOT a substrate-level user-visible type (per
-- .claude/rules/25-physicality-4d.md); it's the internal flat-array I/O ABI
-- for the native kernels.
CREATE OR REPLACE FUNCTION public.linestring4d_from_geometry(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.array_to_linestring4d(
        ARRAY(
            SELECT coord
              FROM generate_series(1, ST_NumPoints(g)) AS idx(i)
              CROSS JOIN LATERAL (
                  SELECT ST_PointN(g, idx.i) AS p
              ) pt
              CROSS JOIN LATERAL (
                  SELECT unnest(ARRAY[ST_X(pt.p), ST_Y(pt.p), ST_Z(pt.p), ST_M(pt.p)])
              ) AS axes(coord)
        )
    )
$$;

COMMENT ON FUNCTION public.linestring4d_from_geometry(geometry) IS
    'Walk a LINESTRINGZM''s vertices via ST_PointN, build a flat (x,y,z,m,x,y,z,m,...) array, and construct the internal native linestring4d. Used by substrate.st_4d_* operator dispatch to bridge PostGIS storage to libhartonomous kernel I/O.';

CREATE CAST (geometry AS public.linestring4d)
    WITH FUNCTION public.linestring4d_from_geometry(geometry)
    AS ASSIGNMENT;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Reference-data populators

-- ── sql/schema/functions/populate_general_categories.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_general_categories(
    p_codes        TEXT[],
    p_group_codes  TEXT[],
    p_descriptions TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.general_category (code, group_code, description)
    SELECT * FROM unnest(p_codes, p_group_codes, p_descriptions)
    ON CONFLICT (code) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_scripts.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_scripts(p_codes TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.script (code)
    SELECT DISTINCT c FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_blocks.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_blocks(
    p_codes        TEXT[],
    p_range_starts INT[],
    p_range_ends   INT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.block (code, range_start, range_end)
    SELECT * FROM unnest(p_codes, p_range_starts, p_range_ends)
    ON CONFLICT (code) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_break_properties.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_break_properties(
    p_codes      TEXT[],
    p_categories TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.break_property (code, category)
    SELECT * FROM unnest(p_codes, p_categories)
    ON CONFLICT (code, category) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_languages.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_languages(
    p_codes  TEXT[],
    p_names  TEXT[],
    p_scopes TEXT[],
    p_types  TEXT[],
    p_part1s TEXT[],
    p_part2bs TEXT[],
    p_part2ts TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.language (code, name, scope, type, part1, part2b, part2t)
    SELECT
        code,
        name,
        scope,
        type,
        NULLIF(part1,  ''),
        NULLIF(part2b, ''),
        NULLIF(part2t, '')
    FROM unnest(p_codes, p_names, p_scopes, p_types, p_part1s, p_part2bs, p_part2ts)
        AS t(code, name, scope, type, part1, part2b, part2t)
    ON CONFLICT (code) DO UPDATE
        SET name   = EXCLUDED.name,
            scope  = EXCLUDED.scope,
            type   = EXCLUDED.type,
            part1  = EXCLUDED.part1,
            part2b = EXCLUDED.part2b,
            part2t = EXCLUDED.part2t;
END $$;

-- ── sql/schema/functions/populate_morph_features.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_morph_features(
    p_keys   TEXT[],
    p_values TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.morph_feature (key, value)
    SELECT * FROM unnest(p_keys, p_values)
    ON CONFLICT (key, value) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_deprels.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_deprels(p_codes TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.deprel (code)
    SELECT DISTINCT c FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO NOTHING;

    -- Resolve subtyped deprels' parent_id (e.g. 'acl:relcl' → parent 'acl').
    UPDATE substrate.deprel d
       SET parent_id = parent.id
      FROM substrate.deprel parent
     WHERE d.parent_id IS NULL
       AND position(':' IN d.code) > 0
       AND parent.code = split_part(d.code, ':', 1);
END $$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Upserters

-- ── sql/schema/functions/upsert_reference_edge_type.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_reference_edge_type(
    p_code               TEXT,
    p_category           TEXT,
    p_source_entity_type TEXT,
    p_target_entity_type TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_source_id INT := NULLIF((SELECT id FROM substrate.entity_type WHERE code = p_source_entity_type), 0);
    v_target_id INT := NULLIF((SELECT id FROM substrate.entity_type WHERE code = p_target_entity_type), 0);
    v_id INT;
BEGIN
    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    VALUES (p_code, p_category, v_source_id, v_target_id)
    ON CONFLICT (code) DO UPDATE
        SET category       = EXCLUDED.category,
            source_type_id = EXCLUDED.source_type_id,
            target_type_id = EXCLUDED.target_type_id
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_homogeneous_edge_types.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_homogeneous_edge_types(
    p_codes            TEXT[],
    p_category         TEXT,
    p_entity_type_code TEXT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_type_id INT := (SELECT id FROM substrate.entity_type WHERE code = p_entity_type_code);
BEGIN
    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    SELECT c, p_category, v_type_id, v_type_id FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO UPDATE
        SET category       = EXCLUDED.category,
            source_type_id = EXCLUDED.source_type_id,
            target_type_id = EXCLUDED.target_type_id;
END $$;

-- ── sql/schema/functions/upsert_architecture_class.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_architecture_class(p_code TEXT)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    INSERT INTO substrate.architecture_class (code) VALUES (p_code)
    ON CONFLICT (code) DO UPDATE SET code = EXCLUDED.code
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_model_registry.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_model_registry(
    p_name         TEXT,
    p_display_name TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    INSERT INTO substrate.model_registry (name)
    VALUES (p_name)
    ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_model_publisher.sql ───────────────────────────────────────
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

-- ── sql/schema/functions/upsert_model_source.sql ───────────────────────────────────────
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

-- ── sql/schema/functions/upsert_model_pass_checkpoint.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_model_pass_checkpoint(
    p_model_source_id INT,
    p_pass_name       TEXT,
    p_status          TEXT,        -- "in_flight" | "completed" | "failed"
    p_rows_emitted    BIGINT,
    p_error_message   TEXT,
    p_extra           JSONB DEFAULT NULL
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    -- p_extra reserved for future per-pass payload; current schema doesn't use it.
    PERFORM p_extra;
    -- INSERT branch only fires when there is no existing row for this
    -- (model_source_id, pass_name) — i.e., the pass is being observed for
    -- the first time. By definition that IS the start, so started_at is
    -- always NOW(). The previous CASE-on-status form gated started_at on
    -- a 'started' status the producer (NpgsqlCheckpointStore) never sends,
    -- which violated the NOT NULL constraint on first-batch upserts.
    INSERT INTO substrate.model_pass_checkpoint
        (model_source_id, pass_name, started_at, completed_at, rows_emitted, error_message)
    VALUES (
        p_model_source_id,
        p_pass_name,
        NOW(),
        CASE WHEN p_status = 'completed' THEN NOW() ELSE NULL END,
        COALESCE(p_rows_emitted, 0),
        p_error_message
    )
    ON CONFLICT (model_source_id, pass_name) DO UPDATE
        SET started_at    = COALESCE(substrate.model_pass_checkpoint.started_at, EXCLUDED.started_at),
            completed_at  = EXCLUDED.completed_at,
            rows_emitted  = EXCLUDED.rows_emitted,
            error_message = EXCLUDED.error_message
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/get_completed_model_passes.sql ───────────────────────────────────────
-- Returns the pass names that have completed for a given model_source. Used
-- by the IModelAnalysisPass orchestrator (Hartonomous.Decomposers.Safetensors)
-- to skip already-done work on resume.
--
-- Returns column is named pass_id for caller compatibility (the C# orchestrator
-- column-binds to "pass_id"); selected from the table's pass_name column.
CREATE OR REPLACE FUNCTION substrate.get_completed_model_passes(
    p_model_source_id BIGINT
) RETURNS TABLE (pass_id VARCHAR(64))
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT pass_name
      FROM substrate.model_pass_checkpoint
     WHERE model_source_id = p_model_source_id
       AND completed_at IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.get_completed_model_passes(BIGINT) IS
    'Returns the pass names that have completed for a given model_source. Used by the Safetensors pass orchestrator to skip already-done work on resume.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Geometry / 4D operators

-- ── sql/schema/functions/dist_4d.sql ───────────────────────────────────────
-- Subtype-dispatching 4D distance over geometry4d.
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 INT := ST_TypeTag4D(g1);
    t2 INT := ST_TypeTag4D(g2);
    p1 point4d;
    p2 point4d;
BEGIN
    IF t1 = 1 AND t2 = 1 THEN
        RETURN public.distance_4d(g1::point4d, g2::point4d);
    END IF;

    IF t1 = 2 AND t2 = 2 THEN
        RETURN public.frechet_4d(g1::linestring4d, g2::linestring4d);
    END IF;

    IF t1 = 1 AND t2 = 2 THEN
        p1 := g1::point4d;
        RETURN (
            SELECT MIN(public.distance_4d(p1, point_n(g2::linestring4d, i)))
              FROM generate_series(1, npoints(g2::linestring4d)) AS i
        );
    END IF;

    IF t1 = 2 AND t2 = 1 THEN
        p2 := g2::point4d;
        RETURN (
            SELECT MIN(public.distance_4d(point_n(g1::linestring4d, i), p2))
              FROM generate_series(1, npoints(g1::linestring4d)) AS i
        );
    END IF;

    RAISE EXCEPTION 'dist_4d: unsupported geometry4d tag pair %, %', t1, t2;
END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry4d, geometry4d) IS
    'Subtype-dispatching 4D distance over native geometry4d. POINT4D/LINESTRING4D pairs route to native 4D primitives.';

-- ── sql/schema/functions/frechet_4d_geom.sql ───────────────────────────────────────
-- Discrete Frechet over native geometry4d trajectories.
CREATE OR REPLACE FUNCTION substrate.frechet_4d_geom(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
BEGIN
    IF ST_TypeTag4D(g1) <> 2 OR ST_TypeTag4D(g2) <> 2 THEN
        RAISE EXCEPTION 'frechet_4d_geom: both arguments must be LINESTRING4D';
    END IF;

    RETURN public.frechet_4d(g1::linestring4d, g2::linestring4d);
END;
$$;

COMMENT ON FUNCTION substrate.frechet_4d_geom(geometry4d, geometry4d) IS
    'Discrete Frechet over native LINESTRING4D geometry4d trajectories.';

-- ── sql/schema/functions/hausdorff_4d_geom.sql ───────────────────────────────────────
-- Symmetric Hausdorff over native geometry4d trajectories.
CREATE OR REPLACE FUNCTION substrate.hausdorff_4d_geom(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
BEGIN
    IF ST_TypeTag4D(g1) <> 2 OR ST_TypeTag4D(g2) <> 2 THEN
        RAISE EXCEPTION 'hausdorff_4d_geom: both arguments must be LINESTRING4D';
    END IF;

    RETURN public.hausdorff_4d(g1::linestring4d, g2::linestring4d);
END;
$$;

COMMENT ON FUNCTION substrate.hausdorff_4d_geom(geometry4d, geometry4d) IS
    'Symmetric Hausdorff over native LINESTRING4D geometry4d trajectories.';

-- ── sql/schema/functions/geometry4d_centroid.sql ───────────────────────────────────────
-- Centroid over a real-coord PostGIS GeometryZM. For POINTZM returns the
-- point itself; for LINESTRINGZM returns the mean of vertex coordinates.
--
-- NOT INTENDED for composition LINESTRINGZM geometries — those have
-- mantissa-packed identity vertices, not metric coordinates, so a
-- coordinate mean is meaningless. Composition entities do not have a
-- stored representative-POINTZM; if one is needed (e.g. for edge.geom
-- construction) it is derived inline from the entity's hash bits via
-- substrate.bb_pack_hash_lo / bb_pack_hash_hi by the bundled-emit
-- pipeline at edge-emit time.
CREATE OR REPLACE FUNCTION substrate.geometry4d_centroid(g geometry)
RETURNS public.point4d
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t TEXT := GeometryType(g);
    n INT;
    sx DOUBLE PRECISION := 0.0;
    sy DOUBLE PRECISION := 0.0;
    sz DOUBLE PRECISION := 0.0;
    sm DOUBLE PRECISION := 0.0;
BEGIN
    IF ST_NDims(g) <> 4 THEN
        RAISE EXCEPTION 'geometry4d_centroid: requires 4D geometry (got ndims=%)', ST_NDims(g);
    END IF;

    IF t = 'POINT' THEN
        RETURN g::public.point4d;
    END IF;

    IF t <> 'LINESTRING' THEN
        RAISE EXCEPTION 'geometry4d_centroid: unsupported GeometryType %', t;
    END IF;

    n := ST_NumPoints(g);
    IF n <= 0 THEN
        RAISE EXCEPTION 'geometry4d_centroid: empty LINESTRINGZM';
    END IF;

    SELECT sum(ST_X(p)), sum(ST_Y(p)), sum(ST_Z(p)), sum(ST_M(p))
      INTO sx, sy, sz, sm
      FROM generate_series(1, n) AS vertex(i)
      CROSS JOIN LATERAL (SELECT ST_PointN(g, vertex.i) AS p) pt;

    RETURN public.point4d(
        sx / n::DOUBLE PRECISION,
        sy / n::DOUBLE PRECISION,
        sz / n::DOUBLE PRECISION,
        sm / n::DOUBLE PRECISION
    );
END;
$$;

COMMENT ON FUNCTION substrate.geometry4d_centroid(geometry) IS
    'Vertex-mean centroid of a real-coord 4D GeometryZM. NOT for composition LINESTRINGZM (those carry mantissa-packed identity bits, not metric coords). Composition representative POINTZMs are derived inline from entity.hash_bits_* via bb_pack_hash_lo/hi when needed; not stored.';

-- ── sql/schema/functions/geometry4d_to_geometryzm.sql ───────────────────────────────────────
-- substrate.geometry4d_to_geometryzm(geometry4d)
--
-- Convert a legacy custom-type geometry4d value to a PostGIS-native
-- geometry(GeometryZM). The native physicality column (geometry(GeometryZM))
-- migration moved BACK to native PostGIS storage; the C# emitter still
-- produces the custom bytea payload that decodes to geometry4d via
-- bytea_to_geometry4d. This function bridges the encoded payload to the
-- native column type so physicality.drain.sql can INSERT into the
-- post-migration column.
--
-- Dispatch on ST_TypeTag4D — 1 = POINT4D, 2 = LINESTRING4D. Other tags
-- (POLYGON/MULTI*/COLLECTION) are not currently produced by the C#
-- payload builder; they raise.
--
-- Extends naturally as the C# payload-builder gains more subtype support.
CREATE OR REPLACE FUNCTION substrate.geometry4d_to_geometryzm(g geometry4d)
RETURNS geometry
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    tag INT;
    p   point4d;
    ls  linestring4d;
    n   INT;
    i   INT;
    coords DOUBLE PRECISION[];
    pts    geometry[];
BEGIN
    tag := ST_TypeTag4D(g);
    IF tag = 1 THEN
        p := g::point4d;
        coords := point4d_to_array(p);
        RETURN ST_MakePoint(coords[1], coords[2], coords[3], coords[4]);
    ELSIF tag = 2 THEN
        ls := g::linestring4d;
        n  := npoints(ls);
        pts := ARRAY[]::geometry[];
        FOR i IN 1..n LOOP
            coords := point4d_to_array(point_n(ls, i));
            pts := array_append(
                pts,
                ST_MakePoint(coords[1], coords[2], coords[3], coords[4])
            );
        END LOOP;
        RETURN ST_MakeLine(pts);
    ELSE
        RAISE EXCEPTION 'geometry4d_to_geometryzm: unsupported geometry4d type tag % (only POINT4D=1 and LINESTRING4D=2 are produced by the C# payload builder)', tag;
    END IF;
END;
$$;

COMMENT ON FUNCTION substrate.geometry4d_to_geometryzm(geometry4d) IS
    'Convert legacy custom-type geometry4d (POINT4D or LINESTRING4D produced by the C# Geometry4dPayloadBuilder) to PostGIS-native geometry(GeometryZM). Bridges the C# emitter''s payload format to the post-migration substrate.physicality.geom column type.';

-- ── sql/schema/functions/geometryzm_to_geometry4d.sql ───────────────────────────────────────
-- substrate.geometryzm_to_geometry4d(geometry)
--
-- Convert a PostGIS-native geometry(GeometryZM) value into a custom
-- geometry4d, so substrate.dist_4d / frechet_4d_geom / hausdorff_4d_geom
-- (which take geometry4d) can be invoked on rows that store geometry in
-- the post-migration PostGIS-native shape. This is the inverse of
-- substrate.geometry4d_to_geometryzm.
--
-- Dispatch on PostGIS GeometryType — 'POINT' / 'POINTZM' → POINT4D,
-- 'LINESTRING' / 'LINESTRINGZM' → LINESTRING4D. Subtypes outside this
-- pair (POLYGON / MULTI* / COLLECTION) are not currently consumed by
-- the substrate-side 4D operators and raise.
CREATE OR REPLACE FUNCTION substrate.geometryzm_to_geometry4d(g geometry)
RETURNS geometry4d
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    kind    TEXT;
    n       INT;
    i       INT;
    pts     point4d[];
BEGIN
    kind := upper(GeometryType(g));
    IF kind IN ('POINT', 'POINTZM', 'POINTZ', 'POINTM') THEN
        RETURN cast_point4d_to_geometry4d(
            point4d(
                ST_X(g),
                ST_Y(g),
                COALESCE(ST_Z(g), 0::double precision),
                COALESCE(ST_M(g), 0::double precision)
            )
        );
    ELSIF kind IN ('LINESTRING', 'LINESTRINGZM', 'LINESTRINGZ', 'LINESTRINGM') THEN
        n := ST_NPoints(g);
        pts := ARRAY[]::point4d[];
        FOR i IN 1..n LOOP
            pts := array_append(
                pts,
                point4d(
                    ST_X(ST_PointN(g, i)),
                    ST_Y(ST_PointN(g, i)),
                    COALESCE(ST_Z(ST_PointN(g, i)), 0::double precision),
                    COALESCE(ST_M(ST_PointN(g, i)), 0::double precision)
                )
            );
        END LOOP;
        RETURN ST_MakeLine4D(pts);
    ELSE
        RAISE EXCEPTION 'geometryzm_to_geometry4d: unsupported PostGIS subtype % (only POINT and LINESTRING variants supported)', kind;
    END IF;
END;
$$;

COMMENT ON FUNCTION substrate.geometryzm_to_geometry4d(geometry) IS
    'Convert a PostGIS-native geometry(GeometryZM) (POINT or LINESTRING subtype) into the custom geometry4d type so substrate.dist_4d / frechet_4d_geom / hausdorff_4d_geom can operate on substrate.physicality.geom rows in the post-migration shape.';

-- ── sql/schema/functions/geometryzm_centroid_point.sql ───────────────────────────────────────
-- substrate.geometryzm_centroid_point(geometry) RETURNS geometry
--
-- Return the centroid of a geometry(GeometryZM) as a POINTZM in the same
-- 4-coordinate space (X, Y, Z, M). Uses the existing
-- substrate.geometry4d_centroid which dispatches on subtype and returns a
-- point4d, then projects back to PostGIS-native POINTZM. Used by edge.geom
-- builders that need a POINTZM-per-participant for ST_MakeLine.
CREATE OR REPLACE FUNCTION substrate.geometryzm_centroid_point(g geometry)
RETURNS geometry
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    p point4d;
    coords DOUBLE PRECISION[];
BEGIN
    p := substrate.geometry4d_centroid(g);
    coords := point4d_to_array(p);
    RETURN ST_MakePoint(coords[1], coords[2], coords[3], coords[4]);
END;
$$;

COMMENT ON FUNCTION substrate.geometryzm_centroid_point(geometry) IS
    'Centroid of a geometry(GeometryZM) as a POINTZM (native PostGIS). Wraps substrate.geometry4d_centroid + ST_MakePoint to keep edge.geom builders inside the native geometry type system after the geometry4d → geometry(GeometryZM) migration on substrate.edge.geom.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (populate_edge_trajectories + count_missing_edge_trajectories removed —
--  edge geometry is built inline at edge-emit by the bundled-emit pipeline.)

-- ── sql/schema/functions/physicality_linestring4d.sql ───────────────────────────────────────
-- Return a flat (x1, x2, x3, x4, x1, x2, x3, x4, ...) coordinate array for
-- the first deterministic LINESTRINGZM physicality on an entity. For
-- composition physicality this returns the mantissa-packed vertex
-- coordinates — callers iterating this should unpack via bb_unpack_*
-- helpers (X = child hash bits 0..51, Y = ordinal+RLE, Z = child hash bits
-- 52..103, M = metadata) rather than treating the values as metric coords.
CREATE OR REPLACE FUNCTION substrate.physicality_linestring4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS DOUBLE PRECISION[]
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ARRAY(
        SELECT unnest(ARRAY[ST_X(v), ST_Y(v), ST_Z(v), ST_M(v)])
          FROM generate_series(1, ST_NumPoints(p.geom)) AS idx(i)
          CROSS JOIN LATERAL (SELECT ST_PointN(p.geom, idx.i) AS v) pt
         ORDER BY idx.i
    )
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND GeometryType(p.geom) = 'LINESTRING'
       AND ST_NDims(p.geom) = 4
       AND EXISTS (
           SELECT 1
             FROM substrate.entity_classification ec
             JOIN substrate.entity_type et ON et.id = ec.entity_type_id
            WHERE ec.entity_hash = p.entity_hash
              AND et.code = p_entity_type_code
       )
     ORDER BY p.content_hash
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.physicality_linestring4d(substrate.hash_value, TEXT, TEXT) IS
    'Flat coordinate array for the first deterministic LINESTRINGZM physicality. For composition physicality this returns mantissa-packed vertices — callers unpack via bb_unpack_* (X = hash_lo, Y = ordinal+RLE, Z = hash_hi, M = metadata).';

-- ── sql/schema/functions/physicality_point4d.sql ───────────────────────────────────────
-- Return the (x, y, z, m) coordinates of the first POINTZM physicality
-- attached to an entity classified as the requested type. Used by the
-- entity-info / inventory readers that want to extract the entity's atomic
-- real-coord centroid from its physicality row (for atoms — codepoint
-- S^3, audio sample, image pixel, etc.).
--
-- For composition entities, this function returns no row — their
-- physicality geom is LINESTRINGZM with mantissa-packed child refs, not
-- POINTZM. Composition representative POINTZMs are derived inline from
-- substrate.entity.hash_bits_0_51 / hash_bits_52_103 via
-- substrate.bb_pack_hash_lo / bb_pack_hash_hi when needed for edge.geom
-- construction by the bundled-emit pipeline — they are not stored
-- anywhere.
CREATE OR REPLACE FUNCTION substrate.physicality_point4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS TABLE (x1 DOUBLE PRECISION, x2 DOUBLE PRECISION, x3 DOUBLE PRECISION, x4 DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ST_X(p.geom), ST_Y(p.geom), ST_Z(p.geom), ST_M(p.geom)
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND GeometryType(p.geom) = 'POINT'
       AND ST_NDims(p.geom) = 4
       AND EXISTS (
           SELECT 1
             FROM substrate.entity_classification ec
             JOIN substrate.entity_type et ON et.id = ec.entity_type_id
            WHERE ec.entity_hash = p.entity_hash
              AND et.code = p_entity_type_code
       )
     ORDER BY p.content_hash
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.physicality_point4d(substrate.hash_value, TEXT, TEXT) IS
    'Return x/y/z/m for the first deterministic POINTZM physicality on a hash classified as the requested entity type. For atom physicality only — compositions have ID-encoded LINESTRINGZM.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Read helpers

-- ── sql/schema/functions/health_summary.sql ───────────────────────────────────────
-- Substrate health summary — row counts on the four content surfaces.
DROP FUNCTION IF EXISTS substrate.health_summary();
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS TABLE (metric TEXT, value BIGINT)
LANGUAGE plpgsql STABLE AS $f$
BEGIN
    RETURN QUERY
        SELECT 'entities'::TEXT, count(*)::BIGINT FROM substrate.entity
      UNION ALL SELECT 'edges',
                       count(*) FROM substrate.edge
      UNION ALL SELECT 'compositions',
                       count(*) FROM substrate.physicality p
                                JOIN substrate.physicality_type pt
                                  ON pt.id = p.physicality_type_id
                                WHERE pt.code = 'contour'
      UNION ALL SELECT 'physicalities',
                       count(*) FROM substrate.physicality
      UNION ALL SELECT 'classifications',
                       count(*) FROM substrate.entity_classification;
END
$f$;

COMMENT ON FUNCTION substrate.health_summary() IS
    'Substrate row-count summary across entity / edge / physicality (with composition_contour subcount) / classification surfaces. Used by the health check + monitoring views.';

-- ── sql/schema/functions/entity_outbound_edges.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_outbound_edges(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_outbound_edges(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em
      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = 'source'
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash
       AND es.context_type_id = sc.id
     WHERE em.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/entity_inbound_edges.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_inbound_edges(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_inbound_edges(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em
      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = 'target'
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash
       AND es.context_type_id = sc.id
     WHERE em.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/entity_neighbors.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_neighbors(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_neighbors(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (neighbor_hash BYTEA, edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em2.entity_hash, em1.edge_type_id, em1.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em1
      JOIN substrate.edge_member em2
        ON em2.edge_type_id = em1.edge_type_id AND em2.edge_hash = em1.edge_hash
       AND em2.entity_hash <> em1.entity_hash
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em1.edge_type_id AND es.edge_hash = em1.edge_hash
       AND es.context_type_id = sc.id
     WHERE em1.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/traversal_neighbors.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.traversal_neighbors(
    p_entity_hash BYTEA,
    p_arena_code  TEXT DEFAULT NULL
)
RETURNS TABLE (
    edge_type_code           TEXT,
    edge_hash                BYTEA,
    neighbor_entity_type_code TEXT,
    neighbor_entity_hash      BYTEA,
    edge_mu                  DOUBLE PRECISION
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT edge_type.code,
           neighbors.edge_hash,
           neighbor_type.code,
           neighbors.neighbor_hash,
           neighbors.mu
      FROM substrate.entity_neighbors(p_entity_hash, p_arena_code) neighbors
      JOIN substrate.edge_type edge_type
        ON edge_type.id = neighbors.edge_type_id
      JOIN substrate.entity_classification neighbor_class
        ON neighbor_class.entity_hash = neighbors.neighbor_hash
      JOIN substrate.entity_type neighbor_type
        ON neighbor_type.id = neighbor_class.entity_type_id
     ORDER BY edge_type.code,
              neighbors.edge_hash,
              neighbor_type.code,
              neighbors.neighbor_hash;
$f$;

COMMENT ON FUNCTION substrate.traversal_neighbors(BYTEA, TEXT) IS
    'Projection wrapper for traversal. Expands substrate.entity_neighbors hash/id output into edge type codes and neighbor entity handles for C# A* traversal.';

-- ── sql/schema/functions/get_entity_info_by_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_type_codes TEXT[], p_hashes BYTEA[]
) RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT requested.type_code, e.hash
      FROM unnest(p_type_codes, p_hashes) AS requested(type_code, h)
      JOIN substrate.entity e ON e.hash = requested.h
      JOIN substrate.entity_type et ON et.code = requested.type_code
      JOIN substrate.entity_classification ec
        ON ec.entity_hash = e.hash
       AND ec.entity_type_id = et.id
     GROUP BY requested.type_code, e.hash
     ORDER BY requested.type_code, e.hash;
$f$;

-- ── sql/schema/functions/get_edge_info_by_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
        p_edge_type_codes TEXT[], p_hashes BYTEA[]
) RETURNS TABLE (
        edge_type_code TEXT,
        edge_hash BYTEA,
        source_type_code TEXT,
        source_hash BYTEA,
        target_type_code TEXT,
        target_hash BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
        SELECT
                et.code,
                e.hash,
                COALESCE(src_decl.code, src_cls.code),
                src.entity_hash,
                COALESCE(tgt_decl.code, tgt_cls.code),
                tgt.entity_hash
            FROM unnest(p_edge_type_codes, p_hashes) AS requested(type_code, h)
            JOIN substrate.edge_type et ON et.code = requested.type_code
            JOIN substrate.edge e ON e.edge_type_id = et.id AND e.hash = requested.h
            LEFT JOIN substrate.entity_type src_decl ON src_decl.id = et.source_type_id
            LEFT JOIN substrate.entity_type tgt_decl ON tgt_decl.id = et.target_type_id
            LEFT JOIN LATERAL (
                    SELECT em.entity_hash
                        FROM substrate.edge_member em
                        JOIN substrate.edge_role er ON er.id = em.edge_role_id
                     WHERE em.edge_type_id = e.edge_type_id
                         AND em.edge_hash = e.hash
                         AND er.code = 'source'
                     ORDER BY em.role_position, em.entity_hash
                     LIMIT 1
            ) src ON true
            LEFT JOIN LATERAL (
                    SELECT em.entity_hash
                        FROM substrate.edge_member em
                        JOIN substrate.edge_role er ON er.id = em.edge_role_id
                     WHERE em.edge_type_id = e.edge_type_id
                         AND em.edge_hash = e.hash
                         AND er.code = 'target'
                     ORDER BY em.role_position, em.entity_hash
                     LIMIT 1
            ) tgt ON true
            LEFT JOIN LATERAL (
                    SELECT child_et.code
                        FROM substrate.entity_classification ec
                        JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
                     WHERE ec.entity_hash = src.entity_hash
                     ORDER BY child_et.code
                     LIMIT 1
            ) src_cls ON true
            LEFT JOIN LATERAL (
                    SELECT child_et.code
                        FROM substrate.entity_classification ec
                        JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
                     WHERE ec.entity_hash = tgt.entity_hash
                     ORDER BY child_et.code
                     LIMIT 1
            ) tgt_cls ON true;
$f$;

-- ── sql/schema/functions/get_outbound_edge_targets.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(
    p_src_hash BYTEA, p_edge_type_code TEXT
) RETURNS TABLE (target_type_code TEXT, target_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT COALESCE(tgt_decl.code, tgt_cls.code), em_t.entity_hash
      FROM substrate.edge_type et
      LEFT JOIN substrate.entity_type tgt_decl ON tgt_decl.id = et.target_type_id
      JOIN substrate.edge_member em_s
        ON em_s.edge_type_id = et.id AND em_s.entity_hash = p_src_hash
      JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
      JOIN substrate.edge_member em_t
        ON em_t.edge_type_id = em_s.edge_type_id AND em_t.edge_hash = em_s.edge_hash
      JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
       LEFT JOIN LATERAL (
        SELECT child_et.code
          FROM substrate.entity_classification ec
          JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
         WHERE ec.entity_hash = em_t.entity_hash
         ORDER BY child_et.code
         LIMIT 1
       ) tgt_cls ON true
     WHERE et.code = p_edge_type_code;
$f$;

-- ── sql/schema/functions/get_composition_children.sql ───────────────────────────────────────
-- Walk a composition entity's children in canonical order.
--
-- The composition's physicality.geom is a LINESTRINGZM (or
-- MULTILINESTRINGZM) in either the 'entity' partition (entity-tier
-- compositions: word_form, grapheme_cluster, lemma, morpheme, ...) or
-- the 'content' partition (content-tier trajectories: text_composition,
-- paragraph, document, audio_chunk, pixel_region, video_frame). Both
-- partitions encode child identities via the substrate mantissa packing
-- contract:
--   X mantissa = child hash bits 0..51 (bb_pack_hash_lo)
--   Y mantissa = ordinal + RLE bit-banged (bb_pack_ordinal_rle)
--   Z mantissa = child hash bits 52..103 (bb_pack_hash_hi)
--   M mantissa = metadata (bb_pack_metadata; currently unused, reserved)
-- Reading the trajectory's vertices in order, unpacking via bb_unpack_*,
-- and joining against substrate.entity's composite btree on
-- (hash_bits_0_51, hash_bits_52_103) recovers the full child hash
-- sequence in one round trip — no junction table required.
--
-- A composition entity typically carries exactly one structural manifest
-- (in its tier's partition). If multiple physicality rows exist (e.g. an
-- atom-equivalent POINTZM stored alongside a structural LINESTRINGZM via
-- legacy decomposers), the manifest is selected by:
--   * mantissa-range filter: X > 2^51 retains mantissa-packed vertices and
--     excludes any real-coord POINTZM/LINESTRING dressed as composition
--   * vertex-count desc: pick the longest manifest (singletons
--     stored as doubled-vertex LINESTRINGs satisfy this too).
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash substrate.hash_value
) RETURNS TABLE (ordinal INT, child_hash substrate.hash_value, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    -- Resolve the parent's expected child tier from its classification.
    -- For text: word_form → grapheme_cluster → codepoint;
    -- text_composition → word_form; paragraph → text_composition;
    -- document → paragraph. NULL = atom (no children).
    -- Disambiguates the singleton case where codepoint and grapheme_cluster
    -- share the same centroid (singleton grapheme = its single codepoint's
    -- coord) — without the tier filter, both match and the walk explodes.
    WITH parent_tier AS (
        SELECT et.code AS parent_code,
               CASE et.code
                   WHEN 'word_form'        THEN 'grapheme_cluster'
                   WHEN 'grapheme_cluster' THEN 'codepoint'
                   WHEN 'morpheme'         THEN 'grapheme_cluster'
                   WHEN 'lemma'            THEN 'word_form'
                   WHEN 'synset'           THEN 'lemma'
                   WHEN 'text_composition' THEN 'word_form'
                   WHEN 'paragraph'        THEN 'text_composition'
                   WHEN 'document'         THEN 'paragraph'
                   ELSE NULL
               END AS expected_child_code
          FROM substrate.entity_classification ec
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
         WHERE ec.entity_hash = p_parent_hash
         LIMIT 1
    ),
    composition_geom AS (
        SELECT p.geom, pt.code AS phys_code
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_hash = p_parent_hash
           AND pt.code IN ('entity', 'content')
           AND GeometryType(p.geom) IN ('LINESTRING', 'MULTILINESTRING')
           AND ST_NumPoints(p.geom) >= 1
         ORDER BY ST_NumPoints(p.geom) DESC, p.content_hash
         LIMIT 1
    ),
    -- Singleton-doubled detection: PostGIS rejects single-vertex
    -- LINESTRINGs, so emitters pad k==1 by repeating the only vertex.
    -- When the geometry is exactly 2 vertices with identical coords,
    -- it represents ONE logical child. Cap the vertex iteration.
    geom_info AS (
        SELECT g.geom,
               g.phys_code,
               ST_NumPoints(g.geom) AS n,
               (
                   ST_NumPoints(g.geom) = 2 AND
                   ST_X(ST_PointN(g.geom, 1)) = ST_X(ST_PointN(g.geom, 2)) AND
                   ST_Y(ST_PointN(g.geom, 1)) = ST_Y(ST_PointN(g.geom, 2)) AND
                   ST_Z(ST_PointN(g.geom, 1)) = ST_Z(ST_PointN(g.geom, 2)) AND
                   ST_M(ST_PointN(g.geom, 1)) = ST_M(ST_PointN(g.geom, 2))
               ) AS is_singleton_doubled
          FROM composition_geom g
    ),
    vertices AS (
        SELECT idx.i AS vertex_idx, ST_PointN(g.geom, idx.i) AS v
          FROM geom_info g
          CROSS JOIN LATERAL generate_series(
              1,
              CASE WHEN g.is_singleton_doubled THEN 1 ELSE g.n END
          ) AS idx(i)
    ),
    classified AS (
        SELECT v.vertex_idx,
               ST_X(v.v) AS x, ST_Y(v.v) AS y, ST_Z(v.v) AS z, ST_M(v.v) AS m,
               (ST_X(v.v) > 2.0^51) AS is_mantissa
          FROM vertices v
    ),
    mantissa_resolved AS (
        SELECT substrate.bb_unpack_ordinal(c.y) AS ordinal,
               substrate.bb_unpack_rle(c.y)     AS rle_count,
               e.hash AS child_hash,
               c.vertex_idx
          FROM classified c
          JOIN substrate.entity e
            ON e.hash_bits_0_51   = substrate.bb_unpack_hash_lo(c.x)
           AND e.hash_bits_52_103 = substrate.bb_unpack_hash_hi(c.z)
         WHERE c.is_mantissa
           AND EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                 JOIN parent_tier pt ON pt.expected_child_code = et.code
                WHERE ec.entity_hash = e.hash
           )
    ),
    realcoord_resolved AS (
        SELECT c.vertex_idx AS ordinal,
               1            AS rle_count,
               e.hash       AS child_hash,
               c.vertex_idx
          FROM classified c
          JOIN substrate.entity e
            ON e.centroid_x = c.x
           AND e.centroid_y = c.y
           AND e.centroid_z = c.z
           AND e.centroid_m = c.m
         WHERE NOT c.is_mantissa
           AND EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                 JOIN parent_tier pt ON pt.expected_child_code = et.code
                WHERE ec.entity_hash = e.hash
           )
    )
    SELECT ordinal, child_hash, rle_count
      FROM (
        SELECT ordinal, child_hash, rle_count, vertex_idx FROM mantissa_resolved
        UNION ALL
        SELECT ordinal, child_hash, rle_count, vertex_idx FROM realcoord_resolved
      ) all_resolved
     ORDER BY ordinal, vertex_idx;
$f$;

COMMENT ON FUNCTION substrate.get_composition_children(substrate.hash_value) IS
    'Walk a composition entity''s children in canonical order by reading the LINESTRINGZM mantissa-packed vertices in physicality.geom (entity or content partition), unpacking child hash slices via bb_unpack_hash_lo/hi, and joining against substrate.entity''s composite btree on (hash_bits_0_51, hash_bits_52_103). No junction table — the geometry IS the relational structure.';

-- ── sql/schema/functions/api_entity_classifications.sql ───────────────────────────────────────
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

-- ── sql/schema/functions/api_entity_by_hash.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_by_hash(
    p_entity_hash BYTEA
) RETURNS TABLE (entity_hash BYTEA, classifications JSONB)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash, substrate.api_entity_classifications(e.hash)
      FROM substrate.entity e
     WHERE e.hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/api_list_entities.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_list_entities(
    p_entity_type_code TEXT DEFAULT NULL,
    p_after_hash BYTEA DEFAULT NULL,
    p_limit INT DEFAULT 100
) RETURNS TABLE (entity_hash BYTEA, classifications JSONB)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash, substrate.api_entity_classifications(e.hash)
      FROM substrate.entity e
     WHERE (p_after_hash IS NULL OR e.hash > p_after_hash)
       AND (
           p_entity_type_code IS NULL
           OR EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                WHERE ec.entity_hash = e.hash
                  AND et.code = p_entity_type_code
           )
       )
     ORDER BY e.hash
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 100), 1), 1000);
$f$;

-- ── sql/schema/functions/api_entity_edges.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_edges(
    p_entity_hash BYTEA,
    p_direction TEXT DEFAULT 'both',
    p_edge_type_code TEXT DEFAULT NULL,
    p_limit INT DEFAULT 100
) RETURNS TABLE (
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    role_code TEXT,
    role_position INT,
    provenance_code TEXT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id,
           et.code::TEXT,
           e.hash,
           er.code::TEXT,
           em.role_position,
           p.code::TEXT
      FROM substrate.edge_member em
      JOIN substrate.edge e ON e.edge_type_id = em.edge_type_id AND e.hash = em.edge_hash
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN substrate.edge_role er ON er.id = em.edge_role_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
     WHERE em.entity_hash = p_entity_hash
       AND (p_edge_type_code IS NULL OR et.code = p_edge_type_code)
       AND (
           COALESCE(p_direction, 'both') = 'both'
           OR (p_direction = 'out' AND er.code = 'source')
           OR (p_direction = 'in' AND er.code = 'target')
       )
     ORDER BY et.code, e.hash, em.role_position
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 100), 1), 1000);
$f$;

-- ── sql/schema/functions/api_edge_by_hash.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_edge_by_hash(
    p_edge_type_code TEXT,
    p_edge_hash BYTEA
) RETURNS TABLE (
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    provenance_code TEXT,
    members JSONB
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id,
           et.code::TEXT,
           e.hash,
           p.code::TEXT,
           COALESCE(
               jsonb_agg(
                   jsonb_build_object(
                       'roleCode', er.code,
                       'rolePosition', em.role_position,
                       'entityHash', encode(em.entity_hash, 'hex'),
                       'classifications', substrate.api_entity_classifications(em.entity_hash)
                   )
                   ORDER BY em.role_position, er.code, em.entity_hash
               ),
               '[]'::jsonb
           )
      FROM substrate.edge e
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
      LEFT JOIN substrate.edge_member em ON em.edge_type_id = e.edge_type_id AND em.edge_hash = e.hash
      LEFT JOIN substrate.edge_role er ON er.id = em.edge_role_id
     WHERE et.code = p_edge_type_code
       AND e.hash = p_edge_hash
     GROUP BY e.edge_type_id, et.code, e.hash, p.code;
$f$;

-- ── sql/schema/functions/api_entity_significance.sql ───────────────────────────────────────
-- API helper: per-entity significance, optionally filtered by arena and/or
-- attestation_type. Returns one row per (arena, attestation_type) so callers
-- can blend stratified evidence at the edge of the API.
CREATE OR REPLACE FUNCTION substrate.api_entity_significance(
    p_entity_hash       BYTEA,
    p_arena_code        TEXT DEFAULT NULL,
    p_attestation_code  TEXT DEFAULT NULL
) RETURNS TABLE (
    arena_code        TEXT,
    attestation_code  TEXT,
    mu                DOUBLE PRECISION,
    sigma             DOUBLE PRECISION,
    volatility        DOUBLE PRECISION,
    games             INT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.code::TEXT, at.code::TEXT, es.mu, es.sigma, es.volatility, es.games
      FROM substrate.entity_significance es
      JOIN substrate.significance_context sc ON sc.id = es.context_type_id
      JOIN substrate.attestation_type     at ON at.id = es.attestation_type_id
     WHERE es.entity_hash = p_entity_hash
       AND (p_arena_code IS NULL OR sc.code = p_arena_code)
       AND (p_attestation_code IS NULL OR at.code = p_attestation_code)
     ORDER BY sc.code, at.code;
$f$;

COMMENT ON FUNCTION substrate.api_entity_significance(BYTEA, TEXT, TEXT) IS
    'Per-entity significance rows, optionally filtered by arena_code and/or attestation_code. Returns the stratified rating surface — one row per (arena, attestation_type). Callers blend at the edge of the API.';

-- ── sql/schema/functions/api_entity_neighbors.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_neighbors(
    p_entity_hash BYTEA,
    p_arena_code TEXT,
    p_limit INT DEFAULT 20
) RETURNS TABLE (
    neighbor_hash BYTEA,
    classifications JSONB,
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    mu DOUBLE PRECISION
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT n.neighbor_hash,
           substrate.api_entity_classifications(n.neighbor_hash),
           n.edge_type_id,
           et.code::TEXT,
           n.edge_hash,
           n.mu
      FROM substrate.entity_neighbors(p_entity_hash, p_arena_code) n
      JOIN substrate.edge_type et ON et.id = n.edge_type_id
     ORDER BY n.mu DESC, et.code, n.neighbor_hash
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 20), 1), 200);
$f$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Composition helpers

-- ── sql/schema/functions/composition_at.sql ───────────────────────────────────────
-- composition_at(parent_hash, ordinal) — return the child at the requested
-- ordinal position within the parent composition's trajectory (RLE-aware).
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
DROP FUNCTION IF EXISTS substrate.composition_at(BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash substrate.hash_value,
    p_ordinal     INT
) RETURNS TABLE (child_hash substrate.hash_value, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.child_hash, c.rle_count
      FROM substrate.get_composition_children(p_parent_hash) c
     WHERE p_ordinal >= c.ordinal
       AND p_ordinal <  c.ordinal + c.rle_count
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.composition_at(substrate.hash_value, INT) IS
    'Return the child at ordinal p_ordinal within the parent composition (RLE-aware). Reads the LINESTRINGZM mantissa-packed vertices via substrate.get_composition_children.';

-- ── sql/schema/functions/composition_before.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_before(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_before(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal - p_distance);
$f$;

-- ── sql/schema/functions/composition_after.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_after(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_after(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal + p_distance);
$f$;

-- ── sql/schema/functions/composition_range.sql ───────────────────────────────────────
-- composition_range(parent_hash, start, end) — return all children whose
-- ordinal positions intersect [p_start, p_end], expanded per-position (RLE
-- expansions emit one row per logical ordinal).
DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_range(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash substrate.hash_value, p_start INT, p_end INT
) RETURNS TABLE (child_type_code TEXT, child_hash substrate.hash_value, ordinal INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT child_cls.code, c.child_hash, expanded.ordinal
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(
         GREATEST(c.ordinal, p_start),
         LEAST(c.ordinal + c.rle_count - 1, p_end)
      ) AS expanded(ordinal)
      CROSS JOIN LATERAL (
         SELECT et.code
           FROM substrate.entity_classification ec
           JOIN substrate.entity_type et ON et.id = ec.entity_type_id
          WHERE ec.entity_hash = c.child_hash
          ORDER BY et.code
          LIMIT 1
      ) child_cls
     WHERE c.ordinal + c.rle_count > p_start
      AND c.ordinal <= p_end
     ORDER BY expanded.ordinal;
$f$;

COMMENT ON FUNCTION substrate.composition_range(substrate.hash_value, INT, INT) IS
    'Expand a composition''s children over the ordinal range [p_start, p_end], one row per logical ordinal. RLE-aware; reads the LINESTRINGZM mantissa-packed vertices via substrate.get_composition_children.';

-- ── sql/schema/functions/composition_subtrajectory.sql ───────────────────────────────────────
-- composition_subtrajectory(parent_hash, start, end) — return (ordinal,
-- child_hash) pairs for ordinals in [p_start, p_end], ordered, RLE-expanded.
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_hash substrate.hash_value, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash substrate.hash_value)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT g.n AS ordinal, c.child_hash
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(c.ordinal, c.ordinal + c.rle_count - 1) AS g(n)
     WHERE g.n BETWEEN p_start AND p_end
     ORDER BY g.n;
$f$;

COMMENT ON FUNCTION substrate.composition_subtrajectory(substrate.hash_value, INT, INT) IS
    'Sub-trajectory of a composition over ordinal range [p_start, p_end], one row per logical ordinal with the child at that position.';

-- ── sql/schema/functions/composition_parents.sql ───────────────────────────────────────
-- composition_parents(child_hash) — reverse lookup: find every composition
-- whose trajectory contains p_child_hash as a child at some position.
--
-- Implementation: extract the child's 104-bit hash prefix (hash_bits_0_51,
-- hash_bits_52_103), then for every composition physicality (type 'contour')
-- iterate its LINESTRINGZM vertices via ST_PointN, unpacking vertex X and Z
-- mantissas via bb_unpack_hash_lo / bb_unpack_hash_hi; report parent rows
-- where any vertex's (lo, hi) matches the child's (lo, hi).
--
-- NOTE: this implementation walks every composition's geometry sequentially
-- for the linear-scan version of S3.D. The follow-up native fast path
-- (libhartonomous lh_trajectory_unpack + pg_trajectory_walk SRFs) replaces
-- this with a C-kernel-driven extraction + spatial index. Until then this
-- correctly answers reverse-parent queries but does not scale to huge
-- physicality tables; use sparingly until the native fast path lands.
DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
DROP FUNCTION IF EXISTS substrate.composition_parents(BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash substrate.hash_value
) RETURNS TABLE (parent_hash substrate.hash_value, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH child_prefix AS (
        SELECT substrate.bb_hash_lo(p_child_hash) AS lo,
               substrate.bb_hash_hi(p_child_hash) AS hi
    ),
    composition_geoms AS (
        SELECT p.entity_hash, p.geom
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE pt.code = 'contour'
    ),
    vertices AS (
        SELECT g.entity_hash,
               ST_PointN(g.geom, idx.i) AS v
          FROM composition_geoms g
          CROSS JOIN LATERAL generate_series(1, ST_NumPoints(g.geom)) AS idx(i)
    ),
    unpacked AS (
        SELECT v.entity_hash AS parent_hash,
               substrate.bb_unpack_ordinal(ST_Y(v.v))  AS ordinal,
               substrate.bb_unpack_rle(ST_Y(v.v))      AS rle_count,
               substrate.bb_unpack_hash_lo(ST_X(v.v))  AS hash_lo,
               substrate.bb_unpack_hash_hi(ST_Z(v.v))  AS hash_hi
          FROM vertices v
    )
    SELECT u.parent_hash, u.ordinal, u.rle_count
      FROM unpacked u
      CROSS JOIN child_prefix cp
     WHERE u.hash_lo = cp.lo
       AND u.hash_hi = cp.hi
     ORDER BY u.parent_hash, u.ordinal;
$f$;

COMMENT ON FUNCTION substrate.composition_parents(substrate.hash_value) IS
    'Reverse lookup: every composition whose LINESTRINGZM trajectory contains p_child_hash as a child. Sequential scan version (linear-scan); native fast-path SRF replaces this in the follow-up S3 work.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- substrate.recompose_text / recompose_text_bulk removed 2026-05-18 (Gate 1
-- reopened item #36 in modular-wishing-koala plan). Document-scale
-- recomposition is now the C# bulk-tier walker
-- (Hartonomous.Core.Recomposition.BulkTierContentWalk; thin wrapper at
-- Hartonomous.Recomposers.ContentRecomposer; Engine fast-path callers route
-- via NpgsqlEntityReader.RecomposeTextAsync). PG-side recursive-CTE walkers
-- were wrong-shape — single-query recursive CTE over physicality.geom forced
-- the executor to materialize intermediate state at every recursion depth,
-- multi-minute for documents. See plan §"Gate 1 Reopening" #36 + AP-29.

-- ── sql/schema/functions/populate_sequence_following_edges.sql ───────────────────────────────────────
-- Bigram extraction from content trajectories → sequence_following arena
-- edges. Walks substrate.text_composition / paragraph / document content
-- entities, decodes their LINESTRINGZM child manifest via
-- substrate.get_composition_children, and emits often_follows(A, B) edges
-- weighted by global frequency.
--
-- Idempotent: ON CONFLICT DO NOTHING on edge insertion; edge_significance
-- updated in place via record_attestations_bulk-equivalent INSERT-SELECT
-- with sum aggregation.
--
-- Build-a-bear's next-token prior comes from this. Without it the
-- synthesizer's per-layer adjacency captures classification + semantic +
-- syntactic structure but not sequence-following — model knows "Hello"
-- clusters with greetings but doesn't know "Hello" is followed by
-- "world" / "how" / "," in real sentences.
CREATE OR REPLACE FUNCTION substrate.populate_sequence_following_edges(
    p_provenance_code TEXT DEFAULT 'tatoeba',
    p_min_frequency   INT DEFAULT 2
)
RETURNS TABLE(edges_emitted BIGINT, pairs_observed BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_provenance_id    INT;
    v_edge_type_id     INT;
    v_arena_id         INT;
    v_pos_evidence_id  INT;
    v_text_comp_type_id INT;
    v_paragraph_type_id INT;
    v_document_type_id  INT;
    v_source_role_id   INT;
    v_target_role_id   INT;
    v_edges_emitted    BIGINT := 0;
    v_pairs_observed   BIGINT := 0;
BEGIN
    SELECT id INTO v_provenance_id FROM substrate.provenance WHERE code = p_provenance_code;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'unknown provenance: %', p_provenance_code;
    END IF;

    SELECT id INTO v_edge_type_id FROM substrate.edge_type WHERE code = 'often_follows';
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'edge_type "often_follows" not seeded; add to seed/edge_type.sql';
    END IF;

    SELECT id INTO v_arena_id FROM substrate.significance_context WHERE code = 'sequence_following';
    IF v_arena_id IS NULL THEN
        RAISE EXCEPTION 'significance_context "sequence_following" not seeded';
    END IF;

    SELECT id INTO v_pos_evidence_id FROM substrate.attestation_type WHERE code = 'positive_evidence';

    SELECT id INTO v_text_comp_type_id FROM substrate.entity_type WHERE code = 'text_composition';
    SELECT id INTO v_paragraph_type_id FROM substrate.entity_type WHERE code = 'paragraph';
    SELECT id INTO v_document_type_id  FROM substrate.entity_type WHERE code = 'document';

    SELECT id INTO v_source_role_id FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO v_target_role_id FROM substrate.edge_role WHERE code = 'target';

    -- Pairs: aggregate (A, B) bigram frequency across all content
    -- trajectories. The trajectory IS the ordered child manifest; consecutive
    -- children at ordinal n and n+1 form a bigram.
    DROP TABLE IF EXISTS pg_temp.bigram_freq;
    CREATE TEMP TABLE pg_temp.bigram_freq AS
    WITH content_entities AS (
        SELECT DISTINCT ec.entity_hash
          FROM substrate.entity_classification ec
         WHERE ec.entity_type_id IN (v_text_comp_type_id, v_paragraph_type_id, v_document_type_id)
    ),
    ordered_children AS (
        SELECT
            ce.entity_hash AS parent_hash,
            ch.ordinal,
            ch.child_hash,
            ROW_NUMBER() OVER (PARTITION BY ce.entity_hash ORDER BY ch.ordinal) AS rn
          FROM content_entities ce,
               LATERAL substrate.get_composition_children(ce.entity_hash) ch
    ),
    bigrams AS (
        SELECT
            a.child_hash AS source_hash,
            b.child_hash AS target_hash
          FROM ordered_children a
          JOIN ordered_children b
            ON b.parent_hash = a.parent_hash
           AND b.rn = a.rn + 1
         WHERE a.child_hash <> b.child_hash
    )
    SELECT
        source_hash,
        target_hash,
        count(*)::BIGINT AS freq
      FROM bigrams
     GROUP BY source_hash, target_hash
    HAVING count(*) >= p_min_frequency;

    SELECT count(*) INTO v_pairs_observed FROM pg_temp.bigram_freq;

    -- Compute edge hash per pair (BLAKE3 of edge_type_id + role-ordered
    -- participant hashes). We use the substrate helper if present;
    -- otherwise fall back to per-row hashing via the C extension.
    DROP TABLE IF EXISTS pg_temp.bigram_edge;
    CREATE TEMP TABLE pg_temp.bigram_edge AS
    SELECT
        bf.source_hash,
        bf.target_hash,
        hartonomous.blake3_edge_hash(v_edge_type_id::INT,
            ARRAY[bf.source_hash, bf.target_hash]::BYTEA[]) AS edge_hash,
        bf.freq
      FROM pg_temp.bigram_freq bf;

    -- Insert edges. ON CONFLICT skips already-existing identities.
    INSERT INTO substrate.edge (edge_type_id, hash, provenance_id, geom)
    SELECT v_edge_type_id, be.edge_hash, v_provenance_id, NULL
      FROM pg_temp.bigram_edge be
    ON CONFLICT (edge_type_id, hash) DO NOTHING;

    -- Insert edge_members (source + target roles).
    INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    SELECT v_edge_type_id, be.edge_hash, be.source_hash, v_source_role_id, 0
      FROM pg_temp.bigram_edge be
    ON CONFLICT (edge_type_id, edge_hash, role_position) DO NOTHING;

    INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    SELECT v_edge_type_id, be.edge_hash, be.target_hash, v_target_role_id, 1
      FROM pg_temp.bigram_edge be
    ON CONFLICT (edge_type_id, edge_hash, role_position) DO NOTHING;

    -- Edge significance: mu calibrated to log(1 + freq) so high-frequency
    -- bigrams dominate but no single super-frequent pair saturates.
    -- Baseline 1500 + 100 × log(1 + freq) puts freq=1 at mu=1500, freq=10
    -- at mu=1739, freq=1000 at mu=2191, freq=100000 at mu=2651.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id, mu, sigma, volatility, games)
    SELECT
        v_arena_id,
        v_edge_type_id,
        be.edge_hash,
        v_pos_evidence_id,
        1500.0 + 100.0 * ln(1 + be.freq),
        350.0,
        0.06,
        be.freq::INT
      FROM pg_temp.bigram_edge be
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO UPDATE
       SET mu = EXCLUDED.mu,
           games = substrate.edge_significance.games + EXCLUDED.games;

    GET DIAGNOSTICS v_edges_emitted = ROW_COUNT;

    edges_emitted := v_edges_emitted;
    pairs_observed := v_pairs_observed;
    RETURN NEXT;
END;
$$;

COMMENT ON FUNCTION substrate.populate_sequence_following_edges(TEXT, INT) IS
    'Walks substrate text_composition / paragraph / document content trajectories, extracts adjacent (A, B) bigrams, aggregates frequency, emits often_follows edges in the sequence_following arena weighted by ln(1+freq). Build-a-bear next-token prior source.';

-- ── sql/schema/tables/derived/position_embedding_aggregate.sql ───────────────────────────────────────
-- Drain-time derived aggregate: per-ordinal-position word_form frequency
-- across ALL content trajectories ingested into the substrate.
--
-- Maintained incrementally by substrate.update_position_embedding_aggregate_from_drain
-- after each StreamingIngestionPipeline drain that emits new content-tier
-- physicality (text_composition / paragraph / document trajectories). Per
-- the AP-37 drain-as-state-change pattern: prompt ingestion IS a state
-- change, so derived state must update incrementally — NOT a static view.
--
-- Consumed by Build-a-bear PositionEmbeddingSynthesizer to derive
-- substrate-native positional embeddings. Replaces the per-synth
-- substrate.position_embedding_stats() LATERAL walk (which was 4.3M
-- get_composition_children calls = ~71 min single-threaded).
--
-- Query pattern at synth time:
--   SELECT ordinal, child_hash, occurrences
--     FROM substrate.position_embedding_aggregate
--    WHERE ordinal < $max_position
--    ORDER BY ordinal, occurrences DESC;
-- → <100ms on indexed read vs 71 min per-row LATERAL walk.
CREATE TABLE IF NOT EXISTS substrate.position_embedding_aggregate (
    ordinal     INT     NOT NULL,
    child_hash  BYTEA   NOT NULL,
    occurrences BIGINT  NOT NULL DEFAULT 0,
    PRIMARY KEY (ordinal, child_hash)
);
-- Centroid + Hilbert index belong on substrate.entity itself (one row per
-- entity, used everywhere) — not denormalized into every derived aggregate.
-- Per-entity centroid+hilbert is task #17; until that lands the synth's
-- PositionEmbeddingSynthesizer mean-pools the substrate-derived hidden-dim
-- embedding rows (which it already has) instead of reading 4D centroid
-- coordinates from this aggregate.

COMMENT ON TABLE substrate.position_embedding_aggregate IS
    'Drain-maintained per-ordinal word_form frequency. Maintained incrementally by substrate.update_position_embedding_aggregate_from_drain per new content trajectory. Build-a-bear PositionEmbeddingSynthesizer reads from here in <100ms instead of the previous 71-min LATERAL walk.';

-- ── sql/schema/indexes/position_embedding_aggregate_ordinal_idx.sql ───────────────────────────────────────
-- Range-scan index on position_embedding_aggregate for synth queries that
-- filter by ordinal < @max_position. The PK (ordinal, child_hash) already
-- supports this via prefix, but a standalone ordinal index helps when
-- queries also ORDER BY occurrences DESC within a position bucket.
CREATE INDEX IF NOT EXISTS position_embedding_aggregate_ordinal_idx
    ON substrate.position_embedding_aggregate (ordinal, occurrences DESC);

-- ── sql/schema/functions/update_position_embedding_aggregate_from_drain.sql ───────────────────────────────────────
-- Incremental drain-time update of the position_embedding_aggregate table.
-- Called by StreamingIngestionPipeline's drain-completion post-pass with
-- the array of parent-entity hashes that landed in this drain (filtered to
-- content-tier types: text_composition / paragraph / document).
--
-- UPSERTs counts; new trajectories add to existing per-(ordinal, child_hash)
-- buckets; same content seen N times across drains adds N occurrences.
-- Per AP-37: idempotent at the row level (since content is content-addressed,
-- same trajectory hash re-ingested is identical — but adding to count is
-- correct semantically: each ingestion event IS a frequency observation).
CREATE OR REPLACE FUNCTION substrate.update_position_embedding_aggregate_from_drain(
    p_parent_hashes BYTEA[]
)
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows_upserted BIGINT := 0;
BEGIN
    IF p_parent_hashes IS NULL OR array_length(p_parent_hashes, 1) IS NULL THEN
        RETURN 0;
    END IF;

    -- Restrict to content-tier parents (text_composition / paragraph / document).
    -- Word_form / lemma compositions ARE entity-tier (the brick's internal
    -- structure) and should not contribute to position embedding statistics;
    -- only content-tier trajectories carry meaningful positional ordering.
    WITH eligible_parents AS (
        SELECT DISTINCT ec.entity_hash
          FROM unnest(p_parent_hashes) AS h(hash)
          JOIN substrate.entity_classification ec
            ON ec.entity_hash = h.hash
         WHERE ec.entity_type_id IN (
             SELECT id FROM substrate.entity_type
              WHERE code IN ('text_composition', 'paragraph', 'document')
         )
    ),
    new_observations AS (
        SELECT
            (ch.ordinal - 1)::INT AS ordinal,
            ch.child_hash,
            count(*)::BIGINT AS occurrences
          FROM eligible_parents ep,
               LATERAL substrate.get_composition_children(ep.entity_hash) ch
         WHERE ch.ordinal >= 1
           AND ch.ordinal <= 65535
         GROUP BY ch.ordinal, ch.child_hash
    )
    INSERT INTO substrate.position_embedding_aggregate (ordinal, child_hash, occurrences)
    SELECT ordinal, child_hash, occurrences
      FROM new_observations
    ON CONFLICT (ordinal, child_hash) DO UPDATE
       SET occurrences = substrate.position_embedding_aggregate.occurrences + EXCLUDED.occurrences;

    GET DIAGNOSTICS v_rows_upserted = ROW_COUNT;
    RETURN v_rows_upserted;
END;
$$;

COMMENT ON FUNCTION substrate.update_position_embedding_aggregate_from_drain(BYTEA[]) IS
    'Incremental drain-time update of substrate.position_embedding_aggregate. Called per drain by StreamingIngestionPipeline with new content-trajectory parent hashes. UPSERTs per-(ordinal, child_hash) counts. AP-37 drain-as-state-change pattern.';

-- ── sql/schema/functions/backfill_position_embedding_aggregate.sql ───────────────────────────────────────
-- One-shot backfill: aggregate every existing content trajectory into
-- substrate.position_embedding_aggregate. Used to bootstrap the aggregate
-- on existing substrate state (or rebuild it after schema changes that
-- alter the aggregate definition). After backfill, all future updates
-- flow through substrate.update_position_embedding_aggregate_from_drain.
--
-- Idempotent via the UPSERT clause — running twice doubles counts, but
-- with TRUNCATE first the effect is "reset + rebuild from scratch."
CREATE OR REPLACE FUNCTION substrate.backfill_position_embedding_aggregate(
    p_truncate_first BOOLEAN DEFAULT TRUE
)
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows_inserted BIGINT := 0;
BEGIN
    IF p_truncate_first THEN
        TRUNCATE substrate.position_embedding_aggregate;
    END IF;

    -- Read every content trajectory's vertices directly from
    -- substrate.physicality (ingestion_trajectory partition). Uses
    -- ST_DumpPoints to unroll the LINESTRINGZM in one pass instead of
    -- per-row LATERAL get_composition_children. Per-vertex mantissa
    -- unpack is inline; child hash resolution via substrate.entity's
    -- composite btree on the GENERATED hash_bits_0_51/52_103 columns
    -- (entity_hash_prefix_idx). Single bulk aggregate over the partition.
    --
    -- Centroid coords + Hilbert index are baked into the aggregate at
    -- write time (pre-gen pattern: don't recompute at read time). Synth
    -- reads (ordinal, child_hash, occurrences, x, y, z, m, hilbert) in
    -- one row vs needing a second substrate.physicality lookup per child.
    WITH walked AS (
        SELECT
            (substrate.bb_unpack_ordinal(ST_Y(pt.geom)) - 1)::INT AS ordinal,
            substrate.bb_unpack_hash_lo(ST_X(pt.geom)) AS hb_lo,
            substrate.bb_unpack_hash_hi(ST_Z(pt.geom)) AS hb_hi
          FROM substrate.physicality p
          CROSS JOIN LATERAL ST_DumpPoints(p.geom) gd
          CROSS JOIN LATERAL (SELECT gd.geom) pt
          JOIN substrate.entity_classification ec ON ec.entity_hash = p.entity_hash
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
         WHERE p.physicality_type_id = (
             SELECT id FROM substrate.physicality_type WHERE code = 'content'
         )
           AND et.code IN ('text_composition', 'paragraph', 'document')
           AND substrate.bb_unpack_ordinal(ST_Y(pt.geom)) >= 1
           AND substrate.bb_unpack_ordinal(ST_Y(pt.geom)) <= 65535
    ),
    resolved AS (
        SELECT
            w.ordinal,
            e.hash AS child_hash,
            count(*)::BIGINT AS occurrences
          FROM walked w
          JOIN substrate.entity e
            ON e.hash_bits_0_51 = w.hb_lo
           AND e.hash_bits_52_103 = w.hb_hi
         GROUP BY w.ordinal, e.hash
    )
    INSERT INTO substrate.position_embedding_aggregate (ordinal, child_hash, occurrences)
    SELECT r.ordinal, r.child_hash, r.occurrences
      FROM resolved r
    ON CONFLICT (ordinal, child_hash) DO UPDATE
       SET occurrences = substrate.position_embedding_aggregate.occurrences + EXCLUDED.occurrences;

    GET DIAGNOSTICS v_rows_inserted = ROW_COUNT;
    RETURN v_rows_inserted;
END;
$$;

COMMENT ON FUNCTION substrate.backfill_position_embedding_aggregate(BOOLEAN) IS
    'One-shot bulk rebuild of substrate.position_embedding_aggregate via direct ST_DumpPoints walk over the ingestion_trajectory partition. Uses entity_by_hash_prefix composite btree for child hash resolution. Replaces per-row LATERAL get_composition_children path (which was 71 min single-threaded for 4.3M trajectories).';

-- ── sql/schema/functions/position_embedding_stats.sql ───────────────────────────────────────
-- Per-position word_form frequency reader. Reads from the drain-maintained
-- substrate.position_embedding_aggregate table — NOT a live aggregate over
-- substrate.physicality. The aggregate is maintained incrementally by
-- substrate.update_position_embedding_aggregate_from_drain (per-drain) and
-- can be bulk-rebuilt by substrate.backfill_position_embedding_aggregate.
--
-- Returns (ordinal, child_hash, occurrences). C# PositionEmbeddingSynthesizer
-- mean-pools these into per-position embedding vectors as a substrate-native
-- replacement for learned positional embeddings.
--
-- Latency: <100ms on indexed PK + range scan vs the previous ~71-min
-- LATERAL get_composition_children walk.
CREATE OR REPLACE FUNCTION substrate.position_embedding_stats(
    p_max_position INT DEFAULT 512,
    p_top_n_per_pos INT DEFAULT 8192
)
RETURNS TABLE(ordinal INT, child_hash BYTEA, occurrences BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ranked AS (
        SELECT
            pea.ordinal,
            pea.child_hash,
            pea.occurrences,
            ROW_NUMBER() OVER (PARTITION BY pea.ordinal ORDER BY pea.occurrences DESC, pea.child_hash) AS rk
          FROM substrate.position_embedding_aggregate pea
         WHERE pea.ordinal >= 0
           AND pea.ordinal < p_max_position
    )
    SELECT ordinal, child_hash, occurrences
      FROM ranked
     WHERE rk <= p_top_n_per_pos
     ORDER BY ordinal, occurrences DESC, child_hash;
$$;

COMMENT ON FUNCTION substrate.position_embedding_stats(INT, INT) IS
    'Reader over substrate.position_embedding_aggregate. Top-N most-frequent child at each ordinal position. Sub-100ms on indexed read. Aggregate maintained incrementally by update_position_embedding_aggregate_from_drain per AP-37.';

-- ── sql/schema/functions/per_arena_entity_significance_stats.sql ───────────────────────────────────────
-- Per-arena distribution stats over entity_significance.mu.
-- Used by LayerNormSynthesizer to derive per-layer γ (= 1/stddev) and
-- β (= -mean/stddev) where each layer is assigned an arena. Without these
-- derived values, conventional LayerNorm γ=1 β=0 lets variance compound
-- layer-to-layer → softmax saturates → output collapses to repetition.
--
-- Returns one row per arena code with (mean_mu, stddev_mu, count). Caller
-- restricts to entity_type subset (e.g. word_form only) via the optional
-- p_entity_type_codes filter.
CREATE OR REPLACE FUNCTION substrate.per_arena_entity_significance_stats(
    p_entity_type_codes TEXT[] DEFAULT NULL
)
RETURNS TABLE(arena_code TEXT, mean_mu DOUBLE PRECISION, stddev_mu DOUBLE PRECISION, row_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH eligible AS (
        SELECT es.context_type_id, es.mu
          FROM substrate.entity_significance es
         WHERE p_entity_type_codes IS NULL
            OR EXISTS (
                SELECT 1
                  FROM substrate.entity_classification ec
                  JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                 WHERE ec.entity_hash = es.entity_hash
                   AND et.code = ANY(p_entity_type_codes)
            )
    )
    SELECT
        sc.code AS arena_code,
        avg(e.mu)::DOUBLE PRECISION AS mean_mu,
        coalesce(stddev_pop(e.mu), 1.0)::DOUBLE PRECISION AS stddev_mu,
        count(*)::BIGINT AS row_count
      FROM eligible e
      JOIN substrate.significance_context sc ON sc.id = e.context_type_id
     GROUP BY sc.code
     ORDER BY sc.code;
$$;

COMMENT ON FUNCTION substrate.per_arena_entity_significance_stats(TEXT[]) IS
    'Per-arena mean and pop-stddev of entity_significance.mu. Used by LayerNormSynthesizer to derive per-layer γ/β. Optional entity_type filter restricts to e.g. word_form only.';

-- ── sql/schema/functions/select_synth_edges_for_ffn.sql ───────────────────────────────────────
-- Substrate edge selection for FFN slot construction.
-- Each FFN intermediate row IS a substrate edge — key direction =
-- E[source], value direction = E[target], magnitude weighted by arena mu.
-- Returns top-N edges in the requested arena set where BOTH endpoints
-- are in the passed-in vocab restriction. Scoring metric:
--   mu_deviation × log(1 + games) × cross_cohort_bridge
-- Cross-cohort bridge upweights edges whose endpoints are different
-- entity_type cohorts (e.g. word_form ↔ pos), which are the load-bearing
-- classification anchors the substrate's structural backbone provides.
CREATE OR REPLACE FUNCTION substrate.select_synth_edges_for_ffn(
    p_vocab_hashes BYTEA[],
    p_arena_codes  TEXT[],
    p_top_n        INT DEFAULT 1536
)
RETURNS TABLE(source_hash BYTEA, target_hash BYTEA, mu DOUBLE PRECISION, games INT, score DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH vocab(hash) AS (
        SELECT unnest(p_vocab_hashes)
    ),
    eligible_edges AS (
        SELECT
            em_src.entity_hash AS source_hash,
            em_tgt.entity_hash AS target_hash,
            em_src.edge_type_id,
            em_src.edge_hash,
            ec_src.entity_type_id AS src_type_id,
            ec_tgt.entity_type_id AS tgt_type_id
          FROM substrate.edge_member em_src
          JOIN substrate.edge_member em_tgt
            ON em_tgt.edge_type_id = em_src.edge_type_id
           AND em_tgt.edge_hash = em_src.edge_hash
           AND em_tgt.role_position > em_src.role_position
          JOIN vocab v_src ON v_src.hash = em_src.entity_hash
          JOIN vocab v_tgt ON v_tgt.hash = em_tgt.entity_hash
          JOIN substrate.entity_classification ec_src ON ec_src.entity_hash = em_src.entity_hash
          JOIN substrate.entity_classification ec_tgt ON ec_tgt.entity_hash = em_tgt.entity_hash
    ),
    scored AS (
        SELECT
            ee.source_hash,
            ee.target_hash,
            es.mu,
            es.games,
            -- mu_deviation × log(1+games) × cohort_bridge
            abs(es.mu - 1500.0) * ln(1 + greatest(es.games, 1))
              * CASE WHEN ee.src_type_id <> ee.tgt_type_id THEN 1.5 ELSE 1.0 END
              AS score
          FROM eligible_edges ee
          JOIN substrate.edge_significance es
            ON es.edge_type_id = ee.edge_type_id
           AND es.edge_hash = ee.edge_hash
          JOIN substrate.significance_context sc ON sc.id = es.context_type_id
         WHERE sc.code = ANY(p_arena_codes)
           AND es.games > 0
    ),
    ranked AS (
        SELECT
            source_hash, target_hash, mu, games, score,
            ROW_NUMBER() OVER (ORDER BY score DESC, source_hash, target_hash) AS rk
          FROM scored
    )
    SELECT source_hash, target_hash, mu, games::INT AS games, score
      FROM ranked
     WHERE rk <= p_top_n;
$$;

COMMENT ON FUNCTION substrate.select_synth_edges_for_ffn(BYTEA[], TEXT[], INT) IS
    'Top-N substrate edges per arena set for FFN-as-substrate-edges construction. Each returned row becomes one FFN intermediate slot: key = E[source], value = E[target]. Cohort-bridge bonus upweights cross-type edges (the substrate''s classification anchors).';

-- ── sql/schema/functions/select_knowledge_subgraph.sql ───────────────────────────────────────
-- substrate.select_knowledge_subgraph
--
-- Knowledge-selection vocab builder for Build-a-bear synthesis. Given a
-- seed set of entity hashes (e.g. user-supplied concept names resolved
-- via substrate.text_decompose) and a budget, BFS through substrate.edge_member
-- weighted by edge significance mu (per-arena-weighted union) to grow a
-- coherent subgraph of word_form entities the bear will know about.
--
-- Architectural intent (rule 35-inference-and-godel + AP-1 open arenas):
--   Vocab = the bear's brain contents — concepts the user wants it to know.
--   Domain-specific bears (medical, math, code) fall out trivially by varying
--   the seed set. Generic bears seed with high-degree function words.
--   MoE experts fall out per-seed-set (different seed = different expert).
--
-- Returns the BFS-discovered subgraph as (entity_hash, edge_count) rows
-- ordered by discovery order (seeds first, then BFS layer 1, layer 2, ...).
-- This ordering becomes the model's tokenizer index — vocab[0..N-1].
--
-- Parameters:
--   p_seed_hashes      : Initial concept hashes (substrate.entity rows).
--   p_arena_weights    : Per-arena weights as (code, weight) pairs.
--   p_vocab_budget     : Target vocab size; BFS stops when reached.
--   p_top_k_per_node   : Max neighbors to add per frontier node per iteration.
--   p_entity_type      : Filter to this entity type (default 'word_form').
--
-- Notes:
--   - Set-based BFS via recursive CTE — single query, no per-node round-trip.
--   - Edge weight = SUM_over_arenas(es.mu * weight_for_arena).
--   - Visited set is the result of the recursion; dedup is the WHERE NOT EXISTS guard.
CREATE OR REPLACE FUNCTION substrate.select_knowledge_subgraph(
    p_seed_hashes    BYTEA[],
    p_arena_weights  TEXT[],         -- alternating: arena_code, weight, arena_code, weight, ...
    p_arena_values   DOUBLE PRECISION[],
    p_vocab_budget   INT,
    p_top_k_per_node INT DEFAULT 32,
    p_entity_type    TEXT DEFAULT 'word_form'
)
RETURNS TABLE (
    entity_hash      BYTEA,
    discovery_round  INT,
    edge_count       BIGINT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_entity_type_id INT;
    v_round          INT := 0;
    v_added          INT;
BEGIN
    SELECT id INTO STRICT v_entity_type_id
      FROM substrate.entity_type WHERE code = p_entity_type;

    -- Initialize visited set with seeds (round 0).
    CREATE TEMP TABLE visited (
        entity_hash     BYTEA PRIMARY KEY,
        discovery_round INT NOT NULL,
        edge_count      BIGINT NOT NULL DEFAULT 0
    ) ON COMMIT DROP;

    INSERT INTO visited (entity_hash, discovery_round, edge_count)
    SELECT DISTINCT s.h, 0, 0
      FROM unnest(p_seed_hashes) AS s(h)
     WHERE EXISTS (
         SELECT 1 FROM substrate.entity_classification ec
          WHERE ec.entity_hash = s.h AND ec.entity_type_id = v_entity_type_id
     );

    -- Arena weight lookup table (small, in-memory).
    CREATE TEMP TABLE arena_weight (
        context_type_id INT PRIMARY KEY,
        weight          DOUBLE PRECISION NOT NULL
    ) ON COMMIT DROP;

    INSERT INTO arena_weight (context_type_id, weight)
    SELECT sc.id, COALESCE(w.weight, 1.0)
      FROM substrate.significance_context sc
      LEFT JOIN unnest(p_arena_weights, p_arena_values) AS w(arena, weight)
        ON w.arena = sc.code;

    -- BFS rounds. Each round picks top-K neighbors per current-frontier node
    -- by weighted edge mu, adds them to visited, repeats until budget filled.
    WHILE (SELECT count(*) FROM visited) < p_vocab_budget LOOP
        v_round := v_round + 1;

        WITH frontier AS (
            SELECT v.entity_hash AS hash
              FROM visited v
             WHERE v.discovery_round = v_round - 1
        ),
        -- Find edges where one endpoint is in the frontier.
        candidate_edges AS (
            SELECT em_self.edge_type_id, em_self.edge_hash, em_self.entity_hash AS self_h
              FROM frontier f
              JOIN substrate.edge_member em_self ON em_self.entity_hash = f.hash
        ),
        -- For each candidate edge, the OTHER participant is a vocab candidate.
        candidate_neighbors AS (
            SELECT em_other.entity_hash AS neighbor,
                   ce.edge_type_id,
                   ce.edge_hash
              FROM candidate_edges ce
              JOIN substrate.edge_member em_other
                ON em_other.edge_type_id = ce.edge_type_id
               AND em_other.edge_hash    = ce.edge_hash
               AND em_other.entity_hash != ce.self_h
        ),
        -- Score each neighbor by sum of weighted edge mu across arenas.
        scored AS (
            SELECT cn.neighbor,
                   sum(es.mu * aw.weight) AS score,
                   count(*) AS edge_count
              FROM candidate_neighbors cn
              JOIN substrate.edge_significance es
                ON es.edge_type_id = cn.edge_type_id
               AND es.edge_hash    = cn.edge_hash
              JOIN arena_weight aw ON aw.context_type_id = es.context_type_id
              JOIN substrate.entity_classification ec
                ON ec.entity_hash    = cn.neighbor
               AND ec.entity_type_id = v_entity_type_id
             WHERE NOT EXISTS (SELECT 1 FROM visited v WHERE v.entity_hash = cn.neighbor)
             GROUP BY cn.neighbor
        ),
        ranked AS (
            SELECT neighbor, score, edge_count,
                   ROW_NUMBER() OVER (ORDER BY score DESC, neighbor) AS rk
              FROM scored
        )
        INSERT INTO visited (entity_hash, discovery_round, edge_count)
        SELECT neighbor, v_round, edge_count
          FROM ranked
         WHERE rk <= LEAST(p_top_k_per_node, p_vocab_budget - (SELECT count(*)::INT FROM visited));

        GET DIAGNOSTICS v_added = ROW_COUNT;
        IF v_added = 0 THEN EXIT; END IF;  -- frontier exhausted
        IF v_round >= 32 THEN EXIT; END IF;  -- max-depth safety
    END LOOP;

    RETURN QUERY
    SELECT v.entity_hash, v.discovery_round, v.edge_count
      FROM visited v
     ORDER BY v.discovery_round, v.edge_count DESC, v.entity_hash;
END;
$$;

COMMENT ON FUNCTION substrate.select_knowledge_subgraph(BYTEA[], TEXT[], DOUBLE PRECISION[], INT, INT, TEXT) IS
    'Build-a-bear knowledge selection: BFS-expand a seed concept set through edge_member by arena-weighted edge mu. Vocab IS the bear''s brain contents. Domain-specific bears via seed-set variation; MoE experts per-seed-set.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- The substrate.entity centroid + hilbert_index columns are populated at
-- INSERT time by the C# producer (native text decomposer emits centroids in
-- record.CentroidX/Y/Z/M; SubstrateTextDecomposer.OnRecord computes
-- 4D Hilbert via TextDecomposeNative.HilbertIndex; AddEntity 7-arg overload
-- threads them through IngestionBatch → StreamingIngestionPipeline →
-- entity.copy.sql column list). No trigger, no backfill — same Merkle
-- invariant (deterministic from hash) produces same centroid on first write.

-- ── sql/schema/functions/entity_tier_hint.sql ───────────────────────────────────────
-- substrate.entity_tier_hint — derive an approximate Merkle DAG depth from
-- the entity's stored 4D centroid radius. Atoms (codepoints) project to the
-- unit 4-sphere (glome) — Super-Fibonacci by UCA collation rank produces
-- ||p||₄d = 1 ± float noise. Compositions are arithmetic means of children's
-- centroids, so by Jensen + sphere convexity, compositions land STRICTLY
-- INSIDE the unit 4-ball. Mean of N points on the glome has expected norm
-- ~1/√N — the more constituents, the closer to origin.
--
-- The tier hint is `1 - radius`: atoms ≈ 0, deep documents ≈ 1.
-- Use for substrate-native "give me high-tier entities near angular X" queries
-- without joining substrate.entity_classification.
--
-- Returns NULL if the entity has no stored centroid yet (e.g., pre-trigger
-- inserts, or after backfill skipped the entity due to no identity physicality).
CREATE OR REPLACE FUNCTION substrate.entity_tier_hint(p_hash substrate.hash_value)
RETURNS DOUBLE PRECISION
LANGUAGE sql
STABLE
AS $$
    SELECT CASE
             WHEN e.centroid_x IS NULL THEN NULL
             ELSE 1.0 - sqrt(
                 e.centroid_x * e.centroid_x +
                 e.centroid_y * e.centroid_y +
                 e.centroid_z * e.centroid_z +
                 e.centroid_m * e.centroid_m)
           END
      FROM substrate.entity e
     WHERE e.hash = p_hash;
$$;

COMMENT ON FUNCTION substrate.entity_tier_hint(substrate.hash_value) IS
    'Approximate Merkle DAG depth derived from 4D centroid radius. Atoms (codepoints on the glome) → 0; deep compositions (documents near origin) → 1. Substrate-native tier query without joining entity_classification — the substrate''s hierarchical structure is realized geometrically via Super-Fibonacci S³ projection + arithmetic-mean centroid recursion. Bulk variant: substrate.entity_tier_hints(hash[]).';

-- ── sql/schema/functions/entity_tier_hints.sql ───────────────────────────────────────
-- substrate.entity_tier_hints — bulk variant of substrate.entity_tier_hint.
-- Returns one row per hash with a stored centroid; NULL-centroid entities
-- (pre-trigger insert, no identity physicality yet) are omitted from result.
CREATE OR REPLACE FUNCTION substrate.entity_tier_hints(p_hashes substrate.hash_value[])
RETURNS TABLE (entity_hash substrate.hash_value, tier_hint DOUBLE PRECISION)
LANGUAGE sql
STABLE
AS $$
    SELECT e.hash,
           1.0 - sqrt(
               e.centroid_x * e.centroid_x +
               e.centroid_y * e.centroid_y +
               e.centroid_z * e.centroid_z +
               e.centroid_m * e.centroid_m)
      FROM substrate.entity e
      JOIN unnest(p_hashes) AS u(h) ON u.h = e.hash
     WHERE e.centroid_x IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.entity_tier_hints(substrate.hash_value[]) IS
    'Bulk variant of substrate.entity_tier_hint. NULL-centroid entities (pre-trigger insert, no identity physicality) are omitted from result rows.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Significance machinery — per-arena initial-mu rows are inserted inline
-- at edge-emit by the bundled-emit pipeline, cross-producted against every
-- arena currently in substrate.significance_context at pipeline startup
-- (AP-1: open vocabulary, no hardcoded subset). No reset/prime watermark
-- functions; no end-of-phase post-pass.

-- ── sql/schema/functions/prune_significance.sql ───────────────────────────────────────
-- substrate.prune_significance(
--     p_min_mu    DOUBLE PRECISION,
--     p_max_sigma DOUBLE PRECISION,
--     p_dry_run   BOOLEAN)
--
-- Remove substrate.edge_significance rows whose μ has fallen below
-- p_min_mu OR whose σ has stayed above p_max_sigma after enough games.
-- Either threshold may be NULL to disable that side of the predicate.
-- Returns the number of rows pruned (or, when p_dry_run = TRUE, the
-- number that would be pruned).
--
-- Pruning never deletes from substrate.edge — only from edge_significance,
-- and only the (arena × edge) cells that have lost confidence in this
-- arena. The edge itself remains in the substrate; another arena may still
-- rate it strongly. This matches the open-vocabulary discipline (.claude/
-- rules/15 § "Arenas are open-vocabulary"): an edge can be pruned in
-- arena A while remaining alive in arena B.
--
-- Bulk DELETE — set-based, no per-row CALL loop (root CLAUDE.md "Batch
-- everything"). Single round-trip per call.

CREATE OR REPLACE FUNCTION substrate.prune_significance(
    p_min_mu    DOUBLE PRECISION DEFAULT NULL,
    p_max_sigma DOUBLE PRECISION DEFAULT NULL,
    p_dry_run   BOOLEAN          DEFAULT FALSE
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_count BIGINT;
BEGIN
    IF p_min_mu IS NULL AND p_max_sigma IS NULL THEN
        RETURN 0;  -- no predicate → no-op (refuse to delete the table)
    END IF;

    IF p_dry_run THEN
        SELECT COUNT(*)
          INTO v_count
          FROM substrate.edge_significance
         WHERE (p_min_mu    IS NULL OR mu    < p_min_mu)
           AND (p_max_sigma IS NULL OR sigma > p_max_sigma);
        RETURN v_count;
    END IF;

    DELETE FROM substrate.edge_significance
     WHERE (p_min_mu    IS NULL OR mu    < p_min_mu)
       AND (p_max_sigma IS NULL OR sigma > p_max_sigma);

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END $$;

COMMENT ON FUNCTION substrate.prune_significance(DOUBLE PRECISION, DOUBLE PRECISION, BOOLEAN) IS
    'Remove low-confidence rows from substrate.edge_significance: μ < p_min_mu AND σ > p_max_sigma (each NULL disables that side). p_dry_run = TRUE returns the would-prune count without deleting. NULL/NULL is a no-op refusing to delete everything. Returns rows pruned (or to-be-pruned).';

-- ── sql/schema/functions/prune_significance_for_context.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.prune_significance_for_context(
    p_context_code TEXT,
    p_min_mu       DOUBLE PRECISION
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_deleted BIGINT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    WITH deleted_edges AS (
        DELETE FROM substrate.edge_significance
         WHERE context_type_id = v_context_id
           AND mu < p_min_mu
         RETURNING 1
    ), deleted_entities AS (
        DELETE FROM substrate.entity_significance
         WHERE context_type_id = v_context_id
           AND mu < p_min_mu
         RETURNING 1
    )
    SELECT (SELECT count(*) FROM deleted_edges) +
           (SELECT count(*) FROM deleted_entities)
      INTO v_deleted;

    RETURN v_deleted;
END $$;

COMMENT ON FUNCTION substrate.prune_significance_for_context(TEXT, DOUBLE PRECISION) IS
    'Prune entity_significance and edge_significance rows below p_min_mu within one arena code. Returns total rows deleted across both substrate significance surfaces.';

-- ── sql/schema/functions/record_comparison.sql ───────────────────────────────────────
-- substrate.record_comparison(
--     p_arena_id              INT,
--     p_winner_edge_type_id   INT,
--     p_winner_edge_hash      BYTEA,
--     p_loser_edge_type_id    INT,
--     p_loser_edge_hash       BYTEA,
--     p_attestation_type_id   INT)
--
-- Record a head-to-head outcome between two edges in the same arena under a
-- specific attestation_type. Step 6 of inference (docs/specs/engine/inference.md):
-- when an outcome arrives (user accept/reject, downstream task succeed/fail),
-- comparison events between selected and rejected paths fire Glicko-2 on the
-- corresponding edge_significance rows. Winners' μ rises, losers' μ falls.
-- The substrate learns from every interaction — closed-loop without training,
-- without gradient descent, without labeled data.
--
-- attestation_type stratifies the rating: an inference_outcome_accept event
-- updates a different row than a corpus_co_occurrence_window event, so the
-- engine can blend them at query time per AttestationTypeBlend rather than
-- collapsing all evidence into one mu.
--
-- Algorithm: Glickman 2012 (http://www.glicko.net/glicko/glicko2.pdf), tau=0.5.
-- Implementation: ONE call to public.glicko2_bulk_update (native C —
-- ext/libhartonomous/src/glicko_bulk.c via ext/hartonomous_pg/src/pg_glicko_bulk.c).

DROP FUNCTION IF EXISTS substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_comparison(
    p_arena_id              INT,
    p_winner_edge_type_id   INT,
    p_winner_edge_hash      BYTEA,
    p_loser_edge_type_id    INT,
    p_loser_edge_hash       BYTEA,
    p_attestation_type_id   INT
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;

    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_winner_edge_type_id, p_winner_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0),
        (p_arena_id, p_loser_edge_type_id,  p_loser_edge_hash,  p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_winner_edge_type_id
       AND edge_hash            = p_winner_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_loser_edge_type_id
       AND edge_hash            = p_loser_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          ARRAY[w_mu,    l_mu]::DOUBLE PRECISION[],
          ARRAY[w_sigma, l_sigma]::DOUBLE PRECISION[],
          ARRAY[w_vol,   l_vol]::DOUBLE PRECISION[],
          ARRAY[l_mu,    w_mu]::DOUBLE PRECISION[],
          ARRAY[l_sigma, w_sigma]::DOUBLE PRECISION[],
          ARRAY[1.0,     0.0]::DOUBLE PRECISION[]
      ) g;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[1],
           sigma      = new_sigma[1],
           volatility = new_vol[1],
           games      = w_games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_winner_edge_type_id
       AND edge_hash            = p_winner_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[2],
           sigma      = new_sigma[2],
           volatility = new_vol[2],
           games      = l_games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_loser_edge_type_id
       AND edge_hash            = p_loser_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA, INT) IS
    'Glicko-2 head-to-head update on substrate.edge_significance for a (winner, loser) pair within (arena, attestation_type). Calls public.glicko2_bulk_update once with n=2 — the formula lives in C (ext/libhartonomous/src/glicko_bulk.c), not in plpgsql. Auto-creates missing rows at default rating before updating. games += 1 on both rows. attestation_type stratifies — same edge can have separate ratings under inference_outcome_accept vs corpus_co_occurrence_window etc.';

-- ── sql/schema/functions/record_edge_comparison.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.record_edge_comparison(TEXT, TEXT, BYTEA, TEXT, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_edge_comparison(
    p_context_code          TEXT,
    p_winner_edge_type_code TEXT,
    p_winner_edge_hash      BYTEA,
    p_loser_edge_type_code  TEXT,
    p_loser_edge_hash       BYTEA,
    p_attestation_type_code TEXT DEFAULT 'inference_outcome_accept'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id           INT;
    v_winner_edge_type_id  INT;
    v_loser_edge_type_id   INT;
    v_attestation_type_id  INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    SELECT id INTO v_winner_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_winner_edge_type_code;
    IF v_winner_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown winner edge_type: %', p_winner_edge_type_code;
    END IF;

    SELECT id INTO v_loser_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_loser_edge_type_code;
    IF v_loser_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown loser edge_type: %', p_loser_edge_type_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    PERFORM substrate.record_comparison(
        v_context_id,
        v_winner_edge_type_id,
        p_winner_edge_hash,
        v_loser_edge_type_id,
        p_loser_edge_hash,
        v_attestation_type_id);
END $$;

COMMENT ON FUNCTION substrate.record_edge_comparison(TEXT, TEXT, BYTEA, TEXT, BYTEA, TEXT) IS
    'Resolve arena, edge type codes, and attestation_type code, then record a Glicko-2 head-to-head update on substrate.edge_significance. Default attestation_type is inference_outcome_accept (Step 6 of inference). Pass corpus_co_occurrence_window or model_attention_pattern for ingestion-time pair comparisons.';

-- ── sql/schema/functions/record_entity_comparison.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.record_entity_comparison(TEXT, BYTEA, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_entity_comparison(
    p_context_code          TEXT,
    p_winner_entity_hash    BYTEA,
    p_loser_entity_hash     BYTEA,
    p_attestation_type_code TEXT DEFAULT 'inference_outcome_accept'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id           INT;
    v_attestation_type_id  INT;
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.entity_significance
        (context_type_id, entity_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, p_winner_entity_hash, v_attestation_type_id, 1500.0, 350.0, 0.06, 0),
        (v_context_id, p_loser_entity_hash,  v_attestation_type_id, 1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, entity_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.entity_significance
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_winner_entity_hash
       AND attestation_type_id = v_attestation_type_id;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.entity_significance
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_loser_entity_hash
       AND attestation_type_id = v_attestation_type_id;

    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          ARRAY[w_mu,    l_mu]::DOUBLE PRECISION[],
          ARRAY[w_sigma, l_sigma]::DOUBLE PRECISION[],
          ARRAY[w_vol,   l_vol]::DOUBLE PRECISION[],
          ARRAY[l_mu,    w_mu]::DOUBLE PRECISION[],
          ARRAY[l_sigma, w_sigma]::DOUBLE PRECISION[],
          ARRAY[1.0,     0.0]::DOUBLE PRECISION[]
      ) g;

    UPDATE substrate.entity_significance
       SET mu = new_mu[1],
           sigma = new_sigma[1],
           volatility = new_vol[1],
           games = w_games + 1
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_winner_entity_hash
       AND attestation_type_id = v_attestation_type_id;

    UPDATE substrate.entity_significance
       SET mu = new_mu[2],
           sigma = new_sigma[2],
           volatility = new_vol[2],
           games = l_games + 1
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_loser_entity_hash
       AND attestation_type_id = v_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_entity_comparison(TEXT, BYTEA, BYTEA, TEXT) IS
    'Glicko-2 head-to-head update on substrate.entity_significance for winner/loser entity hashes within (arena, attestation_type). Default attestation_type is inference_outcome_accept. Uses public.glicko2_bulk_update; auto-creates missing rows at default rating.';

-- ── sql/schema/functions/record_corroboration.sql ───────────────────────────────────────
-- substrate.record_corroboration(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_strength              DOUBLE PRECISION,
--     p_attestation_type_id   INT)
--
-- Record a positive corroboration event without head-to-head comparison.
-- Algebraically: a Glicko-2 draw against a synthetic opponent equal to this
-- edge itself, scaled by p_strength ∈ (0, 1]. Cross-source corroboration
-- naturally lands here — when a second source attests the same edge, sigma
-- narrows; mu unchanged.
--
-- attestation_type stratifies — corroboration from corpus_co_occurrence_window
-- updates a different rating row than corroboration from
-- cross_model_corroboration; the engine blends them per AttestationTypeBlend.

DROP FUNCTION IF EXISTS substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_corroboration(
    p_arena_id              INT,
    p_edge_type_id          INT,
    p_edge_hash             BYTEA,
    p_strength              DOUBLE PRECISION,
    p_attestation_type_id   INT
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    c_pi_sq CONSTANT DOUBLE PRECISION := pi() * pi();
    cur_sigma DOUBLE PRECISION;
    g_val     DOUBLE PRECISION;
    new_sigma_full DOUBLE PRECISION;
BEGIN
    IF p_strength IS NULL OR p_strength <= 0.0 THEN
        RETURN;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT sigma
      INTO cur_sigma
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    g_val          := 1.0 / sqrt(1.0 + 3.0 * cur_sigma * cur_sigma / c_pi_sq);
    new_sigma_full := 1.0 / sqrt(
                          1.0 / (cur_sigma * cur_sigma)
                          + (g_val * g_val) / 4.0
                      );

    UPDATE substrate.edge_significance
       SET sigma = cur_sigma + (new_sigma_full - cur_sigma) * LEAST(p_strength, 1.0),
           games = games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION, INT) IS
    'Glicko-2 corroboration update on substrate.edge_significance: lightweight sigma narrowing (μ unchanged) for the algebraic specialization of a draw against self. p_strength scales the σ narrowing; 1.0 = full draw-against-self update, 0 = no-op. games += 1. attestation_type required — corroboration from different evidence kinds lands in different rating rows.';

-- ── sql/schema/functions/record_outcome.sql ───────────────────────────────────────
-- substrate.record_outcome(
--     p_arena_id              INT,
--     p_winner_target_hash    BYTEA,
--     p_loser_target_hashes   BYTEA[],
--     p_attestation_type_id   INT)
--
-- Engine spec Step 6 (inference.md): Glicko-2 comparison events update
-- significance ratings on edges that supported selected vs rejected
-- paths. attestation_type stratifies the rating row updated — typical
-- Step 6 calls pass inference_outcome_accept (winners) or
-- inference_outcome_reject (losers) so outcome evidence accumulates
-- separately from corpus/model/lexicon evidence on the same edges.
--
-- For each (winner, loser) pair: identify strongest edge in the
-- (arena, attestation_type) row family, then update both sides.
--
-- Set-based + native bulk-Glicko. No FOREACH, no per-row PERFORM.
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[]);
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[], INT);

CREATE OR REPLACE FUNCTION substrate.record_outcome(
    p_arena_id            INT,
    p_winner_target_hash  BYTEA,
    p_loser_target_hashes BYTEA[],
    p_attestation_type_id INT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_w_etid       INT;
    v_w_hash       BYTEA;
    v_w_mu         double precision;
    v_w_sigma      double precision;
    v_w_vol        double precision;
    v_pair_count   INT;
    v_w_mu_arr     double precision[];
    v_w_sigma_arr  double precision[];
    v_w_vol_arr    double precision[];
    v_l_etid_arr   int[];
    v_l_hash_arr   bytea[];
    v_l_mu_arr     double precision[];
    v_l_sigma_arr  double precision[];
    v_l_vol_arr    double precision[];
    v_score_w_arr  double precision[];
    v_score_l_arr  double precision[];
    v_w_new_mu     double precision[];
    v_w_new_sigma  double precision[];
    v_w_new_vol    double precision[];
    v_l_new_mu     double precision[];
    v_l_new_sigma  double precision[];
    v_l_new_vol    double precision[];
    v_w_final_mu    double precision;
    v_w_final_sigma double precision;
    v_w_final_vol   double precision;
BEGIN
    IF p_winner_target_hash IS NULL OR p_loser_target_hashes IS NULL THEN
        RETURN 0;
    END IF;

    SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
      INTO v_w_etid, v_w_hash, v_w_mu, v_w_sigma, v_w_vol
      FROM substrate.edge_member em
      JOIN substrate.edge_significance es
        ON es.edge_type_id        = em.edge_type_id
       AND es.edge_hash            = em.edge_hash
       AND es.context_type_id     = p_arena_id
       AND es.attestation_type_id = p_attestation_type_id
     WHERE em.entity_hash = p_winner_target_hash
     ORDER BY es.mu DESC NULLS LAST
     LIMIT 1;

    IF v_w_etid IS NULL THEN RETURN 0; END IF;

    SELECT
        array_agg(le.edge_type_id),
        array_agg(le.edge_hash),
        array_agg(le.mu),
        array_agg(le.sigma),
        array_agg(le.volatility)
      INTO v_l_etid_arr, v_l_hash_arr, v_l_mu_arr, v_l_sigma_arr, v_l_vol_arr
      FROM unnest(p_loser_target_hashes) AS lt(loser_hash)
      CROSS JOIN LATERAL (
          SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
            FROM substrate.edge_member em
            JOIN substrate.edge_significance es
              ON es.edge_type_id        = em.edge_type_id
             AND es.edge_hash            = em.edge_hash
             AND es.context_type_id     = p_arena_id
             AND es.attestation_type_id = p_attestation_type_id
           WHERE em.entity_hash = lt.loser_hash
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) le
     WHERE lt.loser_hash IS NOT NULL
       AND lt.loser_hash <> p_winner_target_hash;

    v_pair_count := COALESCE(array_length(v_l_etid_arr, 1), 0);
    IF v_pair_count = 0 THEN RETURN 0; END IF;

    v_w_mu_arr    := array_fill(v_w_mu,    ARRAY[v_pair_count]);
    v_w_sigma_arr := array_fill(v_w_sigma, ARRAY[v_pair_count]);
    v_w_vol_arr   := array_fill(v_w_vol,   ARRAY[v_pair_count]);
    v_score_w_arr := array_fill(1.0::double precision, ARRAY[v_pair_count]);
    v_score_l_arr := array_fill(0.0::double precision, ARRAY[v_pair_count]);

    SELECT new_mu, new_sigma, new_volatility
      INTO v_w_new_mu, v_w_new_sigma, v_w_new_vol
      FROM public.glicko2_bulk_update(
          v_w_mu_arr,  v_w_sigma_arr, v_w_vol_arr,
          v_l_mu_arr,  v_l_sigma_arr,
          v_score_w_arr);

    SELECT new_mu, new_sigma, new_volatility
      INTO v_l_new_mu, v_l_new_sigma, v_l_new_vol
      FROM public.glicko2_bulk_update(
          v_l_mu_arr,  v_l_sigma_arr, v_l_vol_arr,
          v_w_mu_arr,  v_w_sigma_arr,
          v_score_l_arr);

    SELECT mu, sigma, volatility
      INTO v_w_final_mu, v_w_final_sigma, v_w_final_vol
      FROM unnest(v_w_new_mu, v_w_new_sigma, v_w_new_vol) AS u(mu, sigma, volatility)
     ORDER BY sigma DESC LIMIT 1;

    UPDATE substrate.edge_significance
       SET mu         = v_w_final_mu,
           sigma      = v_w_final_sigma,
           volatility = v_w_final_vol,
           games      = games + v_pair_count
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = v_w_etid
       AND edge_hash           = v_w_hash
       AND attestation_type_id = p_attestation_type_id;

    UPDATE substrate.edge_significance es
       SET mu         = u.new_mu,
           sigma      = u.new_sigma,
           volatility = u.new_volatility,
           games      = es.games + 1
      FROM unnest(v_l_etid_arr, v_l_hash_arr, v_l_new_mu, v_l_new_sigma, v_l_new_vol)
        AS u(etype_id, ehash, new_mu, new_sigma, new_volatility)
     WHERE es.context_type_id     = p_arena_id
       AND es.edge_type_id        = u.etype_id
       AND es.edge_hash           = u.ehash
       AND es.attestation_type_id = p_attestation_type_id;

    RETURN v_pair_count;
END $$;

COMMENT ON FUNCTION substrate.record_outcome(INT, BYTEA, BYTEA[], INT) IS
    'Engine Step 6 outcome update — set-based + native bulk-Glicko, scoped to (arena, attestation_type). unnest + LATERAL gather pairs; public.glicko2_bulk_update (C) computes new ratings; UPDATE ... FROM unnest applies them. attestation_type required — typically inference_outcome_accept for winner-side outcomes, inference_outcome_reject for loser-side.';

-- ── sql/schema/functions/record_outcomes_bulk.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.record_outcomes_bulk(
    p_winner_target_hashes BYTEA[],
    p_winner_group_ids     INT[],
    p_loser_target_hashes  BYTEA[],
    p_loser_group_ids      INT[],
    p_attestation_type_code TEXT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_attestation_type_id INT;
    v_events INT;
BEGIN
    IF p_winner_target_hashes IS NULL
       OR p_winner_group_ids IS NULL
       OR p_loser_target_hashes IS NULL
       OR p_loser_group_ids IS NULL THEN
        RETURN 0;
    END IF;

    SELECT id
      INTO v_attestation_type_id
      FROM substrate.attestation_type
     WHERE code = p_attestation_type_code;

    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type code: %', p_attestation_type_code;
    END IF;

    WITH winner_groups AS (
        SELECT winner_hash, group_id
        FROM unnest(p_winner_target_hashes, p_winner_group_ids) AS w(winner_hash, group_id)
        WHERE winner_hash IS NOT NULL
    ),
    loser_groups AS (
        SELECT group_id, array_agg(loser_hash) AS loser_hashes
        FROM unnest(p_loser_target_hashes, p_loser_group_ids) AS l(loser_hash, group_id)
        WHERE loser_hash IS NOT NULL
        GROUP BY group_id
    ),
    outcome_calls AS (
        SELECT substrate.record_outcome(
                   sc.id,
                   wg.winner_hash,
                   lg.loser_hashes,
                   v_attestation_type_id) AS events
        FROM winner_groups AS wg
        JOIN loser_groups AS lg USING (group_id)
        CROSS JOIN substrate.significance_context AS sc
    )
    SELECT COALESCE(SUM(events), 0)::INT
      INTO v_events
      FROM outcome_calls;

    RETURN v_events;
END $$;

COMMENT ON FUNCTION substrate.record_outcomes_bulk(BYTEA[], INT[], BYTEA[], INT[], TEXT) IS
    'Bulk Step-6 outcome recorder. C# sends flattened winner/loser groups once; SQL fans out across all significance contexts and delegates each grouped comparison to substrate.record_outcome, which performs set-based edge selection and native bulk-Glicko updates.';

-- ── sql/schema/functions/record_attestation.sql ───────────────────────────────────────
-- substrate.record_attestation(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_attestation_type_id   INT,
--     p_score                 DOUBLE PRECISION,
--     p_weight                DOUBLE PRECISION DEFAULT 1.0)
--
-- Sign-bearing per-edge Glicko-2 attestation event. The substrate's primary
-- decomposer-side rating surface for "this evidence supports / opposes this
-- edge with this magnitude" — per `docs/01-tensor-primitive-spec.md` §V and
-- AP-31 (sign-throwing decomposers).
--
-- Algebraically the edge plays one Glicko-2 game against a synthetic neutral
-- opponent at the arena's default rating (1500, 350, 0.06). p_score in [0, 1]
-- — 1.0 = win, 0.0 = loss, 0.5 = draw — encodes sign. The substrate's
-- bidirectional mu around the 1500 neutral encodes the model's positive vs
-- negative consensus on this attested relationship; mu well above 1500 means
-- repeated positive corroboration, well below means repeated suppression /
-- anti-correspondence evidence.
--
-- p_weight scales the per-event effect on mu and sigma. Internally implemented
-- by running the Glicko event with both the actual opponent AND `(weight - 1)`
-- additional draws against self (algebraic equivalent of weight rounds) — this
-- preserves Glicko's variance bookkeeping rather than fractionally scaling
-- score (which breaks the estimator). Weight clamped to [0.0, 1024.0]; weight
-- < 1.0 reduces effect proportionally by attenuating the rating-period delta.
--
-- attestation_type stratifies — same edge can carry separate ratings under
-- model_attention_qk_pattern, model_ffn_full_path, model_input_embedding, etc.
-- Cross-model corroboration accumulates on the SAME (arena, edge, atest) row.
--
-- Auto-creates the row at default before updating (matches record_comparison /
-- record_corroboration shape).
DROP FUNCTION IF EXISTS substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION);
DROP FUNCTION IF EXISTS substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_attestation(
    p_arena_id              INT,
    p_edge_type_id          INT,
    p_edge_hash             BYTEA,
    p_attestation_type_id   INT,
    p_score                 DOUBLE PRECISION,
    p_weight                DOUBLE PRECISION DEFAULT 1.0
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    cur_mu     DOUBLE PRECISION;
    cur_sigma  DOUBLE PRECISION;
    cur_vol    DOUBLE PRECISION;
    cur_games  INT;
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
    n_repeats  INT;
    fractional DOUBLE PRECISION;
    score_clamped DOUBLE PRECISION;
    opp_mu     DOUBLE PRECISION[];
    opp_sigma  DOUBLE PRECISION[];
    self_mu    DOUBLE PRECISION[];
    self_sigma DOUBLE PRECISION[];
    self_vol   DOUBLE PRECISION[];
    scores     DOUBLE PRECISION[];
BEGIN
    IF p_weight IS NULL OR p_weight <= 0.0 THEN
        RETURN;
    END IF;
    IF p_score IS NULL THEN
        RETURN;
    END IF;
    score_clamped := GREATEST(0.0, LEAST(1.0, p_score));

    -- Ensure row exists at default before reading.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO cur_mu, cur_sigma, cur_vol, cur_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    -- Weight handling:
    --   weight >= 1: run floor(weight) full Glicko events at score_clamped, plus
    --                a fractional final event whose effect is interpolated.
    --   weight < 1: run one Glicko event but interpolate the result between
    --               (mu, sigma, vol) and the post-update values by weight.
    n_repeats  := GREATEST(1, LEAST(1024, FLOOR(p_weight)::INT));
    fractional := GREATEST(0.0, LEAST(1.0, p_weight - n_repeats));

    -- Build the n_repeats × game arrays. Each game pits the edge against a
    -- fresh neutral-default opponent; Glicko-2 processes them as one rating
    -- period (which is the correct shape — per Glickman 2012 §3, all games in
    -- a period are aggregated before update).
    self_mu    := array_fill(cur_mu,    ARRAY[n_repeats]);
    self_sigma := array_fill(cur_sigma, ARRAY[n_repeats]);
    self_vol   := array_fill(cur_vol,   ARRAY[n_repeats]);
    opp_mu     := array_fill(1500.0,    ARRAY[n_repeats]);
    opp_sigma  := array_fill(350.0,     ARRAY[n_repeats]);
    scores     := array_fill(score_clamped, ARRAY[n_repeats]);

    -- Glicko-2 takes per-self arrays where each row is "this rating's update
    -- considering THIS many games against THESE opponents." For one row with
    -- n games, we'd ordinarily pass arrays-of-arrays. The bulk surface here
    -- treats each pair as its own row's update; for n games on the same edge
    -- we run them as n parallel rows, take the LAST as the post-period state.
    -- This is algebraically sound only for small n; for large weights the
    -- strict-period formulation needs the scalar variance aggregator. n is
    -- capped at 1024 above to keep the approximation tight.
    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          self_mu, self_sigma, self_vol,
          opp_mu,  opp_sigma,
          scores
      ) g;

    IF fractional > 0.0 THEN
        cur_mu    := cur_mu    + (new_mu[n_repeats]    - cur_mu)    * fractional;
        cur_sigma := cur_sigma + (new_sigma[n_repeats] - cur_sigma) * fractional;
        cur_vol   := cur_vol   + (new_vol[n_repeats]   - cur_vol)   * fractional;
    ELSE
        cur_mu    := new_mu[n_repeats];
        cur_sigma := new_sigma[n_repeats];
        cur_vol   := new_vol[n_repeats];
    END IF;

    UPDATE substrate.edge_significance
       SET mu         = cur_mu,
           sigma      = cur_sigma,
           volatility = cur_vol,
           games      = cur_games + n_repeats + (CASE WHEN fractional > 0.0 THEN 1 ELSE 0 END)
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION, DOUBLE PRECISION) IS
    'Sign-bearing Glicko-2 attestation event on substrate.edge_significance. Plays the edge against a neutral-default synthetic opponent under (arena, attestation_type); p_score in [0,1] encodes sign (1 = positive evidence, 0 = negative); p_weight scales the rating-period game count. Auto-creates missing rows at default. Per docs/01-tensor-primitive-spec.md §V and AP-31 in .claude/rules/45-anti-patterns.md — replaces sign-throwing Math.Abs decomposers.';

-- ── sql/schema/functions/record_attestations_bulk.sql ───────────────────────────────────────
-- substrate.record_attestations_bulk(
--     p_arena_id              INT,
--     p_attestation_type_id   INT,
--     p_edge_type_ids         INT[],
--     p_edge_hashes           BYTEA[],
--     p_scores                DOUBLE PRECISION[],
--     p_weights               DOUBLE PRECISION[])
--
-- Set-based sign-bearing Glicko-2 attestation events on substrate.edge_significance.
-- Per-event ONE-shot Glicko-2 step against the arena's neutral default
-- (1500, 350, 0.06); the standard formula's mu/sigma/volatility deltas are
-- scaled by per-event weight before write. ONE call to the native bulk
-- Glicko-2 kernel processes ALL events; ONE set-based UPDATE writes them
-- back. NO plpgsql loops. Per AP-2 (no RBAR), AP-31 (sign-bearing).
--
-- p_scores[i] in [0, 1] — 1.0 = positive evidence, 0.0 = negative,
-- 0.5 = ambiguous draw. Encodes the SIGN of the underlying measurement.
-- p_weights[i] > 0 — magnitude of the measurement (|projection|, |response|,
-- |cosine|). Scales the per-event mu/sigma/volatility delta linearly. Weight
-- = 1 reproduces the canonical single-game Glicko step; weight > 1 amplifies
-- the move; weight < 1 attenuates. Sigma/volatility are clamped to a small
-- positive floor on write so a high-corroboration batch can converge toward
-- certainty without violating the strictly-positive domains.
--
-- All four input arrays must be the same length. Rows with weight <= 0 or
-- NULL score are skipped. Auto-creates missing rows at default before update.
--
-- attestation_type stratifies — same edge can carry separate ratings under
-- model_attention_qk_pattern, model_ffn_full_path, model_input_embedding, etc.
-- Cross-model corroboration accumulates on the SAME (arena, edge, atest) row.
DROP FUNCTION IF EXISTS substrate.record_attestations_bulk(INT, INT, INT[], BYTEA[], DOUBLE PRECISION[], DOUBLE PRECISION[]);

CREATE OR REPLACE FUNCTION substrate.record_attestations_bulk(
    p_arena_id              INT,
    p_attestation_type_id   INT,
    p_edge_type_ids         INT[],
    p_edge_hashes           BYTEA[],
    p_scores                DOUBLE PRECISION[],
    p_weights               DOUBLE PRECISION[]
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    n_in        INT;
    n_processed INT;
    self_mu     DOUBLE PRECISION[];
    self_sigma  DOUBLE PRECISION[];
    self_vol    DOUBLE PRECISION[];
    opp_mu      DOUBLE PRECISION[];
    opp_sigma   DOUBLE PRECISION[];
    scores_arr  DOUBLE PRECISION[];
    weights_arr DOUBLE PRECISION[];
    etype_arr   INT[];
    ehash_arr   BYTEA[];
    new_mu      DOUBLE PRECISION[];
    new_sigma   DOUBLE PRECISION[];
    new_vol     DOUBLE PRECISION[];
BEGIN
    n_in := COALESCE(cardinality(p_edge_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_edge_type_ids) <> n_in
       OR cardinality(p_scores)     <> n_in
       OR cardinality(p_weights)    <> n_in THEN
        RAISE EXCEPTION 'record_attestations_bulk: array length mismatch (% / % / % / %)',
            n_in, cardinality(p_edge_type_ids), cardinality(p_scores), cardinality(p_weights);
    END IF;

    -- Step 1: ensure every targeted row exists at default (set-based).
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    SELECT DISTINCT
           p_arena_id, t.edge_type_id, t.edge_hash, p_attestation_type_id,
           COALESCE(pea.initial_mu, p.initial_mu * et.semantic_weight * p.derivation_decay, at.default_initial_mu),
           COALESCE(pea.initial_sigma, p.initial_sigma, at.default_initial_sigma),
           0.06,
           0
       FROM unnest(p_edge_type_ids, p_edge_hashes, p_scores, p_weights)
            AS t(edge_type_id, edge_hash, score, weight)
       JOIN substrate.attestation_type at
         ON at.id = p_attestation_type_id
       LEFT JOIN substrate.edge e
         ON e.edge_type_id = t.edge_type_id
        AND e.hash = t.edge_hash
       LEFT JOIN substrate.edge_type et
         ON et.id = t.edge_type_id
       LEFT JOIN substrate.provenance p
         ON p.id = e.provenance_id
       LEFT JOIN substrate.provenance_edge_authority pea
         ON pea.provenance_id = e.provenance_id
        AND pea.edge_type_id = t.edge_type_id
      WHERE t.weight IS NOT NULL AND t.weight > 0.0 AND t.score IS NOT NULL
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    -- Step 2: gather current state in input order, filter the no-op rows.
    -- One JOIN, no loop. Arrays are then handed to the native bulk kernel.
    WITH inp AS (
        SELECT t.ord,
               t.edge_type_id,
               t.edge_hash,
               GREATEST(0.0, LEAST(1.0, t.score))::DOUBLE PRECISION AS score,
               t.weight
          FROM unnest(p_edge_type_ids, p_edge_hashes, p_scores, p_weights)
               WITH ORDINALITY AS t(edge_type_id, edge_hash, score, weight, ord)
         WHERE t.weight IS NOT NULL AND t.weight > 0.0 AND t.score IS NOT NULL
    ),
    cur AS (
        SELECT inp.ord, inp.edge_type_id, inp.edge_hash, inp.score, inp.weight,
               es.mu, es.sigma, es.volatility
          FROM inp
          JOIN substrate.edge_significance es
            ON es.context_type_id     = p_arena_id
           AND es.edge_type_id        = inp.edge_type_id
           AND es.edge_hash           = inp.edge_hash
           AND es.attestation_type_id = p_attestation_type_id
         ORDER BY inp.ord
    )
    SELECT array_agg(mu),
           array_agg(sigma),
           array_agg(volatility),
           array_agg(1500.0::DOUBLE PRECISION),
           array_agg(350.0::DOUBLE PRECISION),
           array_agg(score),
           array_agg(weight),
           array_agg(edge_type_id),
           array_agg(edge_hash)
      INTO self_mu, self_sigma, self_vol,
           opp_mu, opp_sigma, scores_arr, weights_arr,
           etype_arr, ehash_arr
      FROM cur;

    IF self_mu IS NULL OR cardinality(self_mu) = 0 THEN RETURN 0; END IF;

    -- Step 3: ONE native bulk Glicko-2 call. The kernel returns
    -- post-period (new_mu, new_sigma, new_vol) per parallel game.
    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          self_mu, self_sigma, self_vol,
          opp_mu,  opp_sigma,
          scores_arr
      ) g;

    -- Step 4: write back per row. Each row's actual update is the canonical
    -- Glicko delta scaled by per-event weight. games += 1 per event regardless
    -- of weight (weight scales the rating-period magnitude, not the count).
    UPDATE substrate.edge_significance es
       SET mu         = es.mu + u.delta_mu,
           sigma      = GREATEST(1e-9::DOUBLE PRECISION, es.sigma + u.delta_sigma),
           volatility = GREATEST(1e-9::DOUBLE PRECISION, es.volatility + u.delta_volatility),
           games      = es.games + u.games
      FROM (
          SELECT raw.edge_type_id,
                 raw.edge_hash,
                 SUM((raw.new_mu - raw.self_mu) * raw.weight) AS delta_mu,
                 SUM((raw.new_sigma - raw.self_sigma) * raw.weight) AS delta_sigma,
                 SUM((raw.new_vol - raw.self_vol) * raw.weight) AS delta_volatility,
                 COUNT(*)::INT AS games
            FROM unnest(etype_arr, ehash_arr,
                        self_mu, self_sigma, self_vol,
                        new_mu,  new_sigma,  new_vol,
                        weights_arr)
                  AS raw(edge_type_id, edge_hash,
                         self_mu, self_sigma, self_vol,
                         new_mu,  new_sigma,  new_vol,
                         weight)
           GROUP BY raw.edge_type_id, raw.edge_hash
      ) AS u
     WHERE es.context_type_id     = p_arena_id
       AND es.edge_type_id        = u.edge_type_id
       AND es.edge_hash           = u.edge_hash
       AND es.attestation_type_id = p_attestation_type_id;

    GET DIAGNOSTICS n_processed = ROW_COUNT;
    RETURN n_processed;
END $$;

COMMENT ON FUNCTION substrate.record_attestations_bulk(INT, INT, INT[], BYTEA[], DOUBLE PRECISION[], DOUBLE PRECISION[]) IS
    'Set-based sign-bearing Glicko-2 attestation events on substrate.edge_significance. ONE public.glicko2_bulk_update call processes thousands of edges; ONE UPDATE FROM unnest applies them. p_scores in [0,1] encodes sign; p_weights linearly scales the canonical Glicko per-event delta. Auto-creates missing rows at default. Per docs/01-tensor-primitive-spec.md §V and AP-31. Drain calls this once per (arena, attestation_type) chunk — no RBAR.';

-- ── sql/schema/functions/initialize_edge_significance.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.initialize_edge_significance(
    p_context_code          TEXT,
    p_edge_type_code        TEXT,
    p_edge_hash             BYTEA,
    p_initial_mu            DOUBLE PRECISION,
    p_attestation_type_code TEXT DEFAULT 'positive_evidence'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id          INT;
    v_edge_type_id        INT;
    v_attestation_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    SELECT id INTO v_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_edge_type_code;
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown edge_type: %', p_edge_type_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, v_edge_type_id, p_edge_hash, v_attestation_type_id,
         p_initial_mu, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id)
    DO UPDATE SET mu = EXCLUDED.mu;
END $$;

COMMENT ON FUNCTION substrate.initialize_edge_significance(TEXT, TEXT, BYTEA, DOUBLE PRECISION, TEXT) IS
    'Initialize or reset the mu value for one edge_significance row addressed by (arena, edge handle, attestation_type). Default attestation_type is positive_evidence — the kind of evidence that ingestion-time priming represents. Preserves sigma, volatility, and games on existing rows.';

-- ── sql/schema/functions/initialize_entity_significance.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.initialize_entity_significance(
    p_context_code          TEXT,
    p_entity_hash           BYTEA,
    p_initial_mu            DOUBLE PRECISION,
    p_attestation_type_code TEXT DEFAULT 'positive_evidence'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id          INT;
    v_attestation_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.entity_significance
        (context_type_id, entity_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, p_entity_hash, v_attestation_type_id,
         p_initial_mu, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, entity_hash, attestation_type_id)
    DO UPDATE SET mu = EXCLUDED.mu;
END $$;

COMMENT ON FUNCTION substrate.initialize_entity_significance(TEXT, BYTEA, DOUBLE PRECISION, TEXT) IS
    'Initialize or reset the mu value for one entity_significance row addressed by (arena, entity, attestation_type). Default attestation_type is positive_evidence — ingestion-time priming. Preserves sigma, volatility, and games on existing rows.';

-- ── sql/schema/functions/blended_edge_mu.sql ───────────────────────────────────────
-- substrate.blended_edge_mu(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_attestation_codes     TEXT[]   -- nullable: NULL = include all
--     p_weights               FLOAT8[] -- nullable: NULL or empty = uniform
-- ) RETURNS FLOAT8
--
-- Compute the blended μ for one edge in one arena, weighting per-attestation_type
-- rating rows. Used by the inference engine to apply an AttestationTypeBlend
-- recipe at traversal time without forcing the C extension's pg_traversal.c
-- to know about per-blend dispatch.
--
-- Semantics:
--   - p_attestation_codes NULL → include every attestation_type present on
--     this (arena, edge); equal weights.
--   - p_attestation_codes set, p_weights NULL → uniform 1.0 weights across
--     the listed attestation_types.
--   - p_attestation_codes set, p_weights set → SUM(es.μ × w_i) / SUM(w_i).
--     Arrays must be the same length; mismatch raises.
--   - No matching rows → returns the substrate default (1500.0) so callers
--     never hit NULL.
--
-- STABLE: same arguments + same substrate state → same result. Used at
-- traversal-time hot path; index-only scan over the (context_type_id,
-- edge_type_id, edge_hash, attestation_type_id) PK suffices.

CREATE OR REPLACE FUNCTION substrate.blended_edge_mu(
    p_arena_id          INT,
    p_edge_type_id      INT,
    p_edge_hash         BYTEA,
    p_attestation_codes TEXT[]   DEFAULT NULL,
    p_weights           FLOAT8[] DEFAULT NULL
)
RETURNS FLOAT8
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    v_blended FLOAT8;
BEGIN
    IF p_attestation_codes IS NOT NULL AND p_weights IS NOT NULL
        AND cardinality(p_attestation_codes) <> cardinality(p_weights) THEN
        RAISE EXCEPTION 'blended_edge_mu: attestation codes (%) and weights (%) length mismatch',
            cardinality(p_attestation_codes), cardinality(p_weights);
    END IF;

    IF p_attestation_codes IS NULL THEN
        -- All attestation types present on this edge; equal weights.
        SELECT AVG(es.mu)
          INTO v_blended
          FROM substrate.edge_significance es
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash;
    ELSIF p_weights IS NULL THEN
        -- Listed attestation types, uniform weights.
        SELECT AVG(es.mu)
          INTO v_blended
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash
           AND at.code = ANY(p_attestation_codes);
    ELSE
        -- Listed attestation types with explicit weights. Build a weight map
        -- via unnest, JOIN to significance rows, weighted average.
        WITH wmap AS (
            SELECT code, weight
              FROM unnest(p_attestation_codes, p_weights) AS u(code, weight)
        )
        SELECT SUM(es.mu * wmap.weight) / NULLIF(SUM(wmap.weight), 0)
          INTO v_blended
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
          JOIN wmap ON wmap.code = at.code
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash;
    END IF;

    RETURN COALESCE(v_blended, 1500.0);
END $$;

COMMENT ON FUNCTION substrate.blended_edge_mu(INT, INT, BYTEA, TEXT[], FLOAT8[]) IS
    'Per-(arena, edge) blended μ across attestation_types. NULL codes = include all; NULL weights = uniform; both set = SUM(μ × w) / SUM(w). Returns 1500 default when no rows match. STABLE PARALLEL SAFE — usable inside the inference engine traversal hot path.';

-- ── sql/schema/functions/consensus_token_pairs.sql ───────────────────────────────────────
-- substrate.consensus_token_pairs(
--     p_arena_code      TEXT,
--     p_attestation_codes TEXT[]   DEFAULT NULL,
--     p_min_mu          FLOAT8   DEFAULT 1500.0,
--     p_min_attestations INT     DEFAULT 2,
--     p_limit           INT      DEFAULT 1000
-- )
--
-- Returns token↔token edges where the substrate has consensus across
-- multiple model decompositions. "Consensus" = at least p_min_attestations
-- distinct attestation events on the edge in the requested arena (counted
-- by the games column on edge_significance), filtered by attestation_type
-- if p_attestation_codes is set, mu above p_min_mu.
--
-- Use case: after decomposing Llama4-Maverick + Qwen3-480B (or any N
-- models), this function surfaces the edges where the models AGREE about
-- token-pair relationships. Edges with games=1 had only one model attest
-- to them; edges with games >= N indicate cross-model corroboration. The
-- recomposer's WHERE-clause distillation pulls from this consensus when
-- producing a new student model that reflects shared knowledge.
--
-- Returns one row per qualifying edge: token_a (sorted lower hash for
-- symmetric edges, source for directed), token_b, blended_mu, attestation
-- count, list of attestation_types present.

CREATE OR REPLACE FUNCTION substrate.consensus_token_pairs(
    p_arena_code        TEXT,
    p_attestation_codes TEXT[] DEFAULT NULL,
    p_min_mu            FLOAT8 DEFAULT 1500.0,
    p_min_attestations  INT    DEFAULT 2,
    p_limit             INT    DEFAULT 1000
)
RETURNS TABLE (
    edge_type_code        TEXT,
    edge_hash             BYTEA,
    token_a_hash          BYTEA,
    token_b_hash          BYTEA,
    blended_mu            FLOAT8,
    total_games           INT,
    attestation_types     TEXT[]
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH arena AS (
        SELECT id FROM substrate.significance_context WHERE code = p_arena_code
    ),
    qualifying_significance AS (
        SELECT
            es.edge_type_id,
            es.edge_hash,
            es.mu,
            es.games,
            at.code AS attestation_code
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
         WHERE es.context_type_id = (SELECT id FROM arena)
           AND es.mu >= p_min_mu
           AND (p_attestation_codes IS NULL OR at.code = ANY(p_attestation_codes))
    ),
    aggregated AS (
        SELECT
            qs.edge_type_id,
            qs.edge_hash,
            AVG(qs.mu) AS blended_mu,
            SUM(qs.games)::INT AS total_games,
            array_agg(qs.attestation_code ORDER BY qs.attestation_code) AS attestation_types
          FROM qualifying_significance qs
         GROUP BY qs.edge_type_id, qs.edge_hash
        HAVING SUM(qs.games) >= p_min_attestations
    ),
    with_members AS (
        SELECT
            et.code AS edge_type_code,
            a.edge_hash,
            a.blended_mu,
            a.total_games,
            a.attestation_types,
            (
                SELECT em.entity_hash
                  FROM substrate.edge_member em
                  JOIN substrate.edge_role er ON er.id = em.edge_role_id
                 WHERE em.edge_type_id = a.edge_type_id
                   AND em.edge_hash    = a.edge_hash
                   AND er.code         = 'source'
                 LIMIT 1
            ) AS token_a_hash,
            (
                SELECT em.entity_hash
                  FROM substrate.edge_member em
                  JOIN substrate.edge_role er ON er.id = em.edge_role_id
                 WHERE em.edge_type_id = a.edge_type_id
                   AND em.edge_hash    = a.edge_hash
                   AND er.code         = 'target'
                 LIMIT 1
            ) AS token_b_hash
          FROM aggregated a
          JOIN substrate.edge_type et ON et.id = a.edge_type_id
         WHERE et.code IN ('model_concept_similarity', 'model_attention_pattern', 'model_ffn_factor', 'co_occurrence')
    )
    SELECT
        edge_type_code,
        edge_hash,
        token_a_hash,
        token_b_hash,
        blended_mu,
        total_games,
        attestation_types
      FROM with_members
     WHERE token_a_hash IS NOT NULL AND token_b_hash IS NOT NULL
     ORDER BY blended_mu DESC, total_games DESC
     LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.consensus_token_pairs(TEXT, TEXT[], FLOAT8, INT, INT) IS
    'Surface token-pair edges with cross-model consensus. Filters by arena, attestation_types, mu floor, and minimum attestation count. Returns blended mu (avg across attestation_types), total games, and the full attestation_type set present. Used by the recomposer''s WHERE-clause distillation to identify the substrate''s accumulated cross-model agreement.';

-- ── sql/schema/functions/create_arena.sql ───────────────────────────────────────
-- substrate.create_arena(code TEXT, backfill BOOLEAN DEFAULT TRUE)
--
-- Adds a new arena to substrate.significance_context (the open-vocabulary
-- arena registry). The backfill parameter is retained for call-site
-- compatibility but no longer registers a watermark — drain-completion
-- post-passes were deleted per AP-37. Edge-significance priors are now
-- emitted inline at edge-emit by the bundled-emit pipeline, which
-- cross-products against every arena currently in significance_context
-- at pipeline startup. New arenas created mid-corpus are picked up the
-- next time a StreamingIngestionPipeline opens; new edges from that
-- point on prime against the new arena. Back-priming over edges that
-- already landed before the arena was created is left to the practitioner
-- (re-emit affected edges, or re-run the relevant phase).
--
-- Returns the new arena's id. Idempotent: a second call with the same
-- code returns the existing id without re-registering.
CREATE OR REPLACE FUNCTION substrate.create_arena(
    p_code     TEXT,
    p_backfill BOOLEAN DEFAULT TRUE
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_id INT;
BEGIN
    IF p_code IS NULL OR length(trim(p_code)) = 0 THEN
        RAISE EXCEPTION 'p_code must be a non-empty arena code';
    END IF;

    -- p_backfill kept for call-site compatibility; ignored on purpose.
    PERFORM p_backfill;

    SELECT id INTO v_id
      FROM substrate.significance_context
     WHERE code = p_code;

    IF v_id IS NULL THEN
        INSERT INTO substrate.significance_context (code)
        VALUES (p_code)
        RETURNING id INTO v_id;
    END IF;

    RETURN v_id;
END $$;

COMMENT ON FUNCTION substrate.create_arena(TEXT, BOOLEAN) IS
    'Add an arena to substrate.significance_context. Per AP-37, no post-pass priming — edge-significance priors are emitted inline at edge-emit by the bundled-emit pipeline. The backfill argument is retained for call-site compatibility and ignored. Returns the arena id; idempotent.';

-- ── sql/schema/functions/create_model_trust_arena.sql ───────────────────────────────────────
-- substrate.create_model_trust_arena(model_provenance_code TEXT)
--
-- Convenience: creates the per-model trust arena `model_trust:<provenance>`
-- when a model is ingested. Wraps substrate.create_arena with the canonical
-- naming convention. Returns the arena id.
CREATE OR REPLACE FUNCTION substrate.create_model_trust_arena(
    p_model_provenance_code TEXT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_arena_code TEXT;
BEGIN
    IF p_model_provenance_code IS NULL OR length(trim(p_model_provenance_code)) = 0 THEN
        RAISE EXCEPTION 'p_model_provenance_code must be a non-empty provenance code';
    END IF;

    v_arena_code := 'model_trust:' || p_model_provenance_code;
    RETURN substrate.create_arena(v_arena_code, TRUE);
END $$;

COMMENT ON FUNCTION substrate.create_model_trust_arena(TEXT) IS
    'Create per-model trust arena `model_trust:<provenance>` for an ingested model. Backfills against existing edges. Idempotent.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Server-side populate_*_from_ext removed 2026-05-17 (Gate 1 Task #22).
-- Reference vocabularies + codepoint atoms + edges are emitted by
-- UnicodeDecomposer reading the UCD source directly via
-- BlobUcdPropertyAccessor (native blob) + IIngestionPipeline.CreateBatch.
-- Per Principle 1 — blob and substrate are siblings derived from the same
-- source; neither populates the other. The ucd_materialization_counts and
-- ucd_reference_vocabulary_counts functions are read-only validation
-- probes re-introduced 2026-05-19 (named-function AP-2 compliance for the
-- decomposer's §2 / §14 verification steps); they observe substrate state,
-- they do not populate it.

-- ── sql/schema/functions/ucd_materialization_counts.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.ucd_materialization_counts()
RETURNS TABLE (
    codepoint_classifications      BIGINT,
    simple_case_edges              BIGINT,
    simple_case_edges_without_geom BIGINT,
    arenas                         BIGINT,
    simple_case_edge_significance  BIGINT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*)
           FROM substrate.entity_classification ec
           JOIN substrate.entity_type et ON et.id = ec.entity_type_id
           JOIN substrate.provenance p   ON p.id  = ec.provenance_id
          WHERE et.code = 'codepoint' AND p.code = 'unicode_consortium')
            AS codepoint_classifications,
        (SELECT count(*)
           FROM substrate.edge e
           JOIN substrate.edge_type et ON et.id = e.edge_type_id
          WHERE et.code IN ('maps_to_lowercase','maps_to_uppercase','maps_to_titlecase','case_folds_to'))
            AS simple_case_edges,
        (SELECT count(*)
           FROM substrate.edge e
           JOIN substrate.edge_type et ON et.id = e.edge_type_id
          WHERE et.code IN ('maps_to_lowercase','maps_to_uppercase','maps_to_titlecase','case_folds_to')
            AND e.geom IS NULL)
            AS simple_case_edges_without_geom,
        (SELECT count(*) FROM substrate.significance_context)
            AS arenas,
        (SELECT count(*)
           FROM substrate.edge_significance es
           JOIN substrate.edge_type et ON et.id = es.edge_type_id
          WHERE et.code IN ('maps_to_lowercase','maps_to_uppercase','maps_to_titlecase','case_folds_to'))
            AS simple_case_edge_significance;
$f$;

COMMENT ON FUNCTION substrate.ucd_materialization_counts() IS
    'Single-row 5-column post-decomposition validation probe for UnicodeDecomposer §14. Verifies codepoint classifications, simple-case edges (with non-NULL geom per AP-37 drain-completion invariant), arena count, and per-arena edge_significance row counts. Re-introduced 2026-05-19 to close the AP-2 raw-SQL leak left by the Gate 1 Task #22 removal of the prior populate_*_from_ext variant.';

-- ── sql/schema/functions/ucd_reference_vocabulary_counts.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.ucd_reference_vocabulary_counts()
RETURNS TABLE (
    general_category_rows BIGINT,
    script_rows           BIGINT,
    block_rows            BIGINT,
    bidi_class_rows       BIGINT,
    east_asian_width_rows BIGINT,
    break_property_rows   BIGINT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*) FROM substrate.general_category)  AS general_category_rows,
        (SELECT count(*) FROM substrate.script)            AS script_rows,
        (SELECT count(*) FROM substrate.block)             AS block_rows,
        (SELECT count(*) FROM substrate.bidi_class)        AS bidi_class_rows,
        (SELECT count(*) FROM substrate.east_asian_width)  AS east_asian_width_rows,
        (SELECT count(*) FROM substrate.break_property)    AS break_property_rows;
$f$;

COMMENT ON FUNCTION substrate.ucd_reference_vocabulary_counts() IS
    'Single-row 6-column row-count probe for the UCD reference vocabularies. Used by UnicodeDecomposer §2 to verify seed presence before §3 codepoint atom emission relies on the +1 enum-code-to-id arithmetic.';

-- ── sql/schema/functions/unicode_edge_hash.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.unicode_edge_hash(
    p_edge_type_id INT,
    p_member_hashes substrate.hash_value[]
)
RETURNS substrate.hash_value
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    payload bytea := decode('00000000', 'hex');
BEGIN
    payload := set_byte(payload, 0, p_edge_type_id & 255);
    payload := set_byte(payload, 1, (p_edge_type_id >> 8) & 255);
    payload := set_byte(payload, 2, (p_edge_type_id >> 16) & 255);
    payload := set_byte(payload, 3, (p_edge_type_id >> 24) & 255);

    SELECT payload || COALESCE(string_agg(member_hash::bytea, ''::bytea ORDER BY ordinality), ''::bytea)
      INTO payload
      FROM unnest(p_member_hashes) WITH ORDINALITY AS members(member_hash, ordinality);

    RETURN blake3_hash(payload)::substrate.hash_value;
END;
$$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- substrate.recompose_content removed 2026-05-18 (Gate 1 reopened item #36) —
-- see C# ContentRecomposer comment above.
-- (Staging drain functions deleted post-W2E refactor. The pipeline now
--  drains within the same connection that COPY-loaded a session-local
--  temp table — no persistent staging, no auto-discovered drain manifest.)
-- Inference / recall

-- ── sql/schema/functions/infer.sql ───────────────────────────────────────
-- substrate.infer(prompt_doc_hash, max_depth, max_results)
--
-- The forward pass — substrate-side, single round-trip from C#.
-- Hash-only entity references throughout (Phase C unification).
--
-- Steps 1-4 of docs/specs/engine/inference.md, executed inside one PG
-- function:
--   1. Seed activation: collect the prompt's word_form children from
--      composition physicality metadata + cross-classification matches via
--      substrate.entity_classification (a hash classified as "lemma" by
--      WordNet AND as "word_form" by Tatoeba is the SAME hash; A* gets
--      both classifications' edge sets implicitly).
--   2. Cross-arena A* via the C extension's traverse_astar (called per
--      arena × per seed). NOTE: the C extension's signature drops
--      entity_type_id with the schema collapse — caller passes hash only.
--   3. Max-pool path significance per terminal entity hash.
--   4. Recompose: walk highest-significance terminal via substrate.recompose_text.
CREATE OR REPLACE FUNCTION substrate.infer(
    p_doc_hash    bytea,
    p_max_depth   INT  DEFAULT 5,
    p_max_results INT  DEFAULT 50
) RETURNS TABLE (
    answer_text         TEXT,
    seed_count          INT,
    distinct_targets    BIGINT,
    best_target_hash    bytea,
    best_total_mu       DOUBLE PRECISION,
    elapsed_ms          INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_started      TIMESTAMP := clock_timestamp();
    v_seed_count   INT := 0;
    v_target_count BIGINT := 0;
    v_best_hash    bytea;
    v_best_mu      DOUBLE PRECISION;
    v_answer       TEXT;
    v_word_form_id INT;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Materialize seeds: prompt's word_form-classified composition children
    -- + the prompt itself + parent compositions of those word_forms.
    CREATE TEMP TABLE IF NOT EXISTS _infer_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _infer_seeds;
    INSERT INTO _infer_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_doc_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
    ),
    -- Inverse-composition: lemma / synset compositions that contain the
    -- prompt's word_form hashes as children. These are the substrate's
    -- "where else does this word appear" bridges into the rich graph.
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.composition_parents(d.h) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_doc_hash
    )
    SELECT h FROM direct_seeds
    UNION
    SELECT h FROM indirect_seeds
    ON CONFLICT (seed_hash) DO NOTHING;

    SELECT count(*) INTO v_seed_count FROM _infer_seeds;

    -- Pool: cross-arena traverse_astar fan-out, max-pool by target hash.
    CREATE TEMP TABLE IF NOT EXISTS _infer_pooled (
        target_hash bytea PRIMARY KEY,
        best_mu     DOUBLE PRECISION
    ) ON COMMIT DROP;
    TRUNCATE _infer_pooled;
    INSERT INTO _infer_pooled (target_hash, best_mu)
    SELECT
        rp.target_hash,
        MAX(rp.total_mu) AS best_mu
    FROM (
        SELECT
            t.target_entity_hash AS target_hash,
            t.total_mu
        FROM _infer_seeds AS s
        CROSS JOIN substrate.significance_context AS a
        CROSS JOIN LATERAL public.traverse_astar(
            s.seed_hash,
            NULL::INT,
            a.id,
            p_max_depth, p_max_results, NULL::DOUBLE PRECISION
        ) AS t
        WHERE t.target_entity_hash IS NOT NULL
    ) rp
    GROUP BY rp.target_hash
    ON CONFLICT (target_hash) DO UPDATE SET best_mu = GREATEST(_infer_pooled.best_mu, EXCLUDED.best_mu);

    SELECT count(*) INTO v_target_count FROM _infer_pooled;

    SELECT p.target_hash, p.best_mu
    INTO v_best_hash, v_best_mu
    FROM _infer_pooled p
    ORDER BY p.best_mu DESC, p.target_hash
    LIMIT 1;

    IF v_best_hash IS NOT NULL THEN
        v_answer := substrate.recompose_text(v_best_hash, p_max_depth);
    END IF;

    RETURN QUERY SELECT
        v_answer,
        v_seed_count,
        v_target_count,
        v_best_hash,
        v_best_mu,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.infer(BYTEA, INT, INT) IS
    'Forward pass — Steps 1-4 of inference.md. Hash-only signature (Phase C unification). Cross-arena A* + max-pool + recompose. Single PG round-trip.';

-- Drop old signature.
DROP FUNCTION IF EXISTS substrate.infer(INT, substrate.hash_value, INT, INT);

-- ── sql/schema/functions/infer_topk.sql ───────────────────────────────────────
-- substrate.infer_topk(p_doc_hash, p_max_depth, p_max_results, p_top_k)
--
-- Top-K variant of substrate.infer. Same forward pass — seed activation
-- via prompt's word_form children + lemma/synset parents, cross-arena A*
-- via traverse_astar, max-pool by target hash — but instead of returning
-- only the best target, returns the K highest-mu targets with each one's
-- recomposed text. The Gödel Engine uses this for:
--
--   * Self-Consistency voting: a target reached by multiple traversal
--     paths (same hash recurs across seed × arena combinations) accrues
--     a higher vote count; agreement boosts confidence.
--   * Tree-of-Thought selection: each top-K row is a candidate "thought
--     branch" the engine evaluates by significance vs path coherence.
--   * Honest abstention threshold: when no top-K row exceeds a confidence
--     floor, the engine abstains rather than fabricating.
--
-- Hash-only signature throughout. recompose_text walks physicality metadata
-- to codepoint leaves; each row is a real recomposition of substrate
-- content, not a sampled string.
DROP FUNCTION IF EXISTS substrate.infer_topk(BYTEA, INT, INT, INT);
CREATE OR REPLACE FUNCTION substrate.infer_topk(
    p_doc_hash    bytea,
    p_max_depth   INT  DEFAULT 5,
    p_max_results INT  DEFAULT 50,
    p_top_k       INT  DEFAULT 5
) RETURNS TABLE (
    rank             INT,
    target_hash      bytea,
    total_mu         DOUBLE PRECISION,
    path_count       BIGINT,
    recomposed_text  TEXT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_word_form_id INT;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Seeds: prompt's word_form-classified composition children + their
    -- lemma/synset parent compositions. Same seed activation as substrate.infer.
    CREATE TEMP TABLE IF NOT EXISTS _topk_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _topk_seeds;
    INSERT INTO _topk_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_doc_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
    ),
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.composition_parents(d.h) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_doc_hash
    )
    SELECT h FROM direct_seeds
    UNION
    SELECT h FROM indirect_seeds
    ON CONFLICT (seed_hash) DO NOTHING;

    -- Pool: cross-arena traverse_astar with both max(mu) AND count(*).
    -- path_count = how many distinct (seed, arena) traversals reached this
    -- target. Self-Consistency: high path_count = independent corroboration.
    CREATE TEMP TABLE IF NOT EXISTS _topk_pooled (
        target_hash bytea PRIMARY KEY,
        best_mu     DOUBLE PRECISION,
        path_count  BIGINT
    ) ON COMMIT DROP;
    TRUNCATE _topk_pooled;
    INSERT INTO _topk_pooled (target_hash, best_mu, path_count)
    SELECT
        rp.target_hash,
        MAX(rp.total_mu) AS best_mu,
        COUNT(*)         AS path_count
    FROM (
        SELECT
            t.target_entity_hash AS target_hash,
            t.total_mu
        FROM _topk_seeds AS s
        CROSS JOIN substrate.significance_context AS a
        CROSS JOIN LATERAL public.traverse_astar(
            s.seed_hash,
            NULL::INT,
            a.id,
            p_max_depth, p_max_results, NULL::DOUBLE PRECISION
        ) AS t
        WHERE t.target_entity_hash IS NOT NULL
    ) rp
    GROUP BY rp.target_hash;

    -- Top-K with stable tie-break (best_mu DESC, path_count DESC,
    -- target_hash ASC). Each row is recomposed via substrate.recompose_text
    -- — all-substrate generation, deterministic across runs.
    RETURN QUERY
    SELECT
        ROW_NUMBER() OVER (ORDER BY p.best_mu DESC, p.path_count DESC, p.target_hash)::INT AS rank,
        p.target_hash,
        p.best_mu,
        p.path_count,
        substrate.recompose_text(p.target_hash, p_max_depth)
    FROM _topk_pooled p
    ORDER BY p.best_mu DESC, p.path_count DESC, p.target_hash
    LIMIT p_top_k;
END $$;

COMMENT ON FUNCTION substrate.infer_topk(BYTEA, INT, INT, INT) IS
    'Top-K targets from a forward pass over the prompt. Hash-only. Returns rank, target_hash, total_mu, path_count, recomposed_text. The Gödel Engine consumes this for Self-Consistency voting, ToT branch selection, and honest-abstention thresholds.';

-- ── sql/schema/functions/prompt_document_ready.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.prompt_document_ready(p_hash BYTEA)
RETURNS TABLE (entity_count BIGINT, composition_child_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*) FROM substrate.entity e WHERE e.hash = p_hash)::BIGINT AS entity_count,
        (SELECT count(*) FROM substrate.get_composition_children(p_hash))::BIGINT AS composition_child_count;
$f$;

COMMENT ON FUNCTION substrate.prompt_document_ready(BYTEA) IS
    'Return prompt document drain-barrier counts for entity and composition-physicality child metadata.';

-- ── sql/schema/functions/recall.sql ───────────────────────────────────────
-- substrate.recall(p_prompt_hash) — the brain's primary direct operation,
-- now structured around hub-intersection rather than max-pool best-target.
--
-- For a prompt's text_composition root:
--   1. Activate seeds: word_form sequence children + their lemma/synset
--      parent compositions (cross-decomposer bridges).
--   2. Cross-reference via substrate.intersect — find entities most strongly
--      intersected across the seeds via edges (in/out), sequence adjacency,
--      and 4D geometric proximity (Fréchet-style bridging of decomposer
--      surface variants).
--   3. Take the top intersected entity. If it's identity-only (synset,
--      lemma, etc.), follow has_gloss/has_text/has_example to a
--      recomposable text_composition. Recompose.
--
-- Cross-decomposer surface bridging is automatic: WordNet "competitor.n.01",
-- Wiktionary "competitor", Tatoeba bare "competitor" inside attested
-- sentences — when their content hashes agree they collapse to one entity;
-- when surfaces differ but trajectories cluster, geometric intersection
-- bridges them.
DROP FUNCTION IF EXISTS substrate.recall(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.recall(
    p_prompt_hash       BYTEA,
    p_max_depth         INT              DEFAULT 3,
    p_top_k             INT              DEFAULT 25,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    answer        TEXT,
    target_hash   BYTEA,
    confidence    DOUBLE PRECISION,
    seed_count    INT,
    target_count  BIGINT,
    elapsed_ms    INT
)
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_started      TIMESTAMP := clock_timestamp();
    v_word_form_id INT;
    v_seeds        BYTEA[];
    v_best_hash    BYTEA;
    v_best_score   DOUBLE PRECISION;
    v_best_seeds   INT;
    v_target_count BIGINT := 0;
    v_answer       TEXT;
    v_text_hash    BYTEA;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Seed activation: prompt's word_form composition children + their
    -- lemma/synset parent compositions.
    SELECT array_agg(DISTINCT h)
    INTO v_seeds
    FROM (
        SELECT s.child_hash AS h
        FROM substrate.get_composition_children(p_prompt_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        UNION
        SELECT s.parent_hash AS h
        FROM substrate.get_composition_children(p_prompt_hash) sd
        JOIN substrate.composition_parents(sd.child_hash) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_prompt_hash
    ) seeds;

    IF v_seeds IS NULL OR array_length(v_seeds, 1) = 0 THEN
        RETURN QUERY SELECT
            NULL::TEXT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
            0, 0::BIGINT,
            EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

    -- Hub intersection across seeds. Top-1 is the substrate's most
    -- structurally-intersected entity for this prompt.
    SELECT i.neighbor_hash, i.score, i.seed_count
    INTO v_best_hash, v_best_score, v_best_seeds
    FROM substrate.intersect(v_seeds, NULL, 1, p_frechet_threshold) i
    LIMIT 1;

    SELECT count(*)
    INTO v_target_count
    FROM substrate.intersect(v_seeds, NULL, 1000, p_frechet_threshold);

    IF v_best_hash IS NULL THEN
        RETURN QUERY SELECT
            NULL::TEXT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
            COALESCE(array_length(v_seeds, 1), 0), v_target_count,
            EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

    -- Try direct recompose first (works if best target is itself a
    -- text_composition).
    v_answer := substrate.recompose_text(v_best_hash, p_max_depth);

    -- If identity-only, bridge to the canonical surface text via has_gloss /
    -- has_text / has_etymology / has_example edges.
    IF v_answer IS NULL OR length(v_answer) = 0 THEN
        SELECT em_t.entity_hash
        INTO v_text_hash
        FROM substrate.edge e
        JOIN substrate.edge_type et ON et.id = e.edge_type_id
        JOIN substrate.edge_member em_s
          ON em_s.edge_type_id = e.edge_type_id
         AND em_s.edge_hash    = e.hash
        JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
        JOIN substrate.edge_member em_t
          ON em_t.edge_type_id = e.edge_type_id
         AND em_t.edge_hash    = e.hash
        JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
        JOIN substrate.entity_classification c_t ON c_t.entity_hash = em_t.entity_hash
        JOIN substrate.entity_type et_t ON et_t.id = c_t.entity_type_id
        WHERE em_s.entity_hash = v_best_hash
          AND et.code IN ('has_gloss', 'has_example', 'has_text', 'has_etymology', 'has_pronunciation')
          AND et_t.code = 'text_composition'
          AND EXISTS (SELECT 1 FROM substrate.get_composition_children(em_t.entity_hash) LIMIT 1)
        ORDER BY
            CASE et.code
                WHEN 'has_gloss'     THEN 0
                WHEN 'has_text'      THEN 1
                WHEN 'has_etymology' THEN 2
                WHEN 'has_example'   THEN 3
                ELSE 9
            END
        LIMIT 1;

        IF v_text_hash IS NOT NULL THEN
            v_answer := substrate.recompose_text(v_text_hash, p_max_depth);
        END IF;
    END IF;

    RETURN QUERY SELECT
        v_answer,
        v_best_hash,
        v_best_score,
        COALESCE(array_length(v_seeds, 1), 0),
        v_target_count,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.recall(BYTEA, INT, INT, DOUBLE PRECISION) IS
    'Brain''s primary direct operation. Activates seeds from prompt''s text_composition, runs hub intersection (substrate.intersect over edges + sequence + 4D geometric proximity), takes the top intersected entity, recomposes its surface text (directly or via has_gloss/has_text/has_example bridge).';

-- ── sql/schema/functions/intersect.sql ───────────────────────────────────────
-- substrate.intersect(p_seed_hashes, p_arena_id, p_top_k, p_frechet_threshold)
--
-- The substrate's actual brain operation. For a set of seed entities (the
-- prompt's word_forms, plus their lemma/synset parent compositions), find
-- the entities most strongly INTERSECTED across them.
--
-- An entity is "intersected" by the seeds when it appears in the
-- neighborhood of MULTIPLE seeds. The substrate's invention vs transformer
-- attention: every entity is a typed hub; cross-referencing across multiple
-- inputs surfaces the entities at the geometric / structural intersection.
--
-- Intersection signal is a weighted combination:
--   * count(distinct seeds reaching it)         — Self-Consistency votes
--   * sum(edge_mu) across reaching paths        — Glicko-weighted relevance
--   * inverse Fréchet distance for geometric    — cross-decomposer bridging
--   * sequence-proximity bonus                  — composition adjacency
--
-- Returns top-K entities by intersection score. The brain picks among
-- them based on intent (definition vs surprise vs translation).
DROP FUNCTION IF EXISTS substrate.intersect(BYTEA[], INT, INT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.intersect(
    p_seed_hashes       BYTEA[],
    p_arena_id          INT              DEFAULT NULL,
    p_top_k             INT              DEFAULT 10,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    rank          INT,
    neighbor_hash BYTEA,
    seed_count    INT,
    score         DOUBLE PRECISION,
    edge_signal   DOUBLE PRECISION,
    geom_signal   DOUBLE PRECISION,
    seq_signal    DOUBLE PRECISION
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_seed_count INT := array_length(p_seed_hashes, 1);
BEGIN
    IF v_seed_count IS NULL OR v_seed_count = 0 THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH expanded AS (
        SELECT
            s.seed_hash,
            n.relation,
            n.neighbor_hash,
            n.edge_mu,
            n.frechet_distance,
            n.sequence_ordinal
        FROM unnest(p_seed_hashes) AS s(seed_hash)
        CROSS JOIN LATERAL substrate.neighborhood(s.seed_hash, p_arena_id, p_frechet_threshold) AS n
    ),
    pooled AS (
        SELECT
            e.neighbor_hash,
            COUNT(DISTINCT e.seed_hash)::INT AS seed_count,
            -- Edge signal: sum of mu across distinct (seed, edge_type) pairs.
            COALESCE(SUM(e.edge_mu) FILTER (WHERE e.relation IN ('outbound_edge','inbound_edge')), 0.0::DOUBLE PRECISION) AS edge_signal,
            -- Geometric signal: count of Fréchet hits, weighted by inverse distance.
            COALESCE(SUM(1.0::DOUBLE PRECISION / (1e-9 + e.frechet_distance)) FILTER (WHERE e.relation = 'frechet_neighbor'), 0.0::DOUBLE PRECISION) AS geom_signal,
            -- Sequence signal: count of composition adjacencies.
            COALESCE(SUM(1.0::DOUBLE PRECISION) FILTER (WHERE e.relation IN ('sequence_parent','sequence_child')), 0.0::DOUBLE PRECISION) AS seq_signal
        FROM expanded e
        WHERE e.neighbor_hash <> ALL(p_seed_hashes)  -- exclude seeds themselves
        GROUP BY e.neighbor_hash
    ),
    scored AS (
        SELECT
            p.neighbor_hash,
            p.seed_count,
            p.edge_signal,
            p.geom_signal,
            p.seq_signal,
            -- Composite score: seed_count is the strongest term (real
            -- intersection across distinct prompts beats high mu from one
            -- path); edge mu is the next strongest; sequence + geometric
            -- are contributing signals.
            (p.seed_count::DOUBLE PRECISION * 1000.0)
            + (p.edge_signal * 1.0)
            + (p.geom_signal * 50.0)
            + (p.seq_signal * 100.0) AS score
        FROM pooled p
    )
    SELECT
        ROW_NUMBER() OVER (ORDER BY s.score DESC, s.neighbor_hash)::INT AS rank,
        s.neighbor_hash,
        s.seed_count,
        s.score,
        s.edge_signal,
        s.geom_signal,
        s.seq_signal
    FROM scored s
    ORDER BY s.score DESC, s.neighbor_hash
    LIMIT p_top_k;
END $$;

COMMENT ON FUNCTION substrate.intersect(BYTEA[], INT, INT, DOUBLE PRECISION) IS
    'Multi-seed intersection. The substrate''s primary brain operation. For seed entities, finds entities most strongly intersected across them via edges (incoming/outgoing), sequence adjacency, and 4D Fréchet geometric proximity. Replaces single-target max-pool with intersection-of-hubs ranking.';

-- ── sql/schema/functions/neighborhood.sql ───────────────────────────────────────
-- substrate.neighborhood(p_entity_hash, p_arena_id, p_frechet_threshold) —
-- the hub view of one entity. Each substrate.entity sits at a hub: every
-- typed edge it participates in (outbound, inbound), every composition it
-- belongs to (sequence parents), every entity geometrically near it
-- (Fréchet over physicality trajectories) is part of its neighborhood.
--
-- Different decomposers produce different surface forms — WordNet uses
-- "competitor.n.01", Wiktionary uses "competitor", Tatoeba uses bare
-- "competitor" inside attested sentences. Their content hashes may differ
-- but their geometric trajectories cluster. Fréchet bridges these surface
-- variants so the brain finds neighbors that aren't explicitly edge-linked.
--
-- Returns one row per neighbor with the relation kind: 'outbound_edge',
-- 'inbound_edge', 'sequence_parent', 'sequence_child', 'frechet_neighbor'.
-- The brain uses this as the raw signal layer that intersect / recall
-- ranking operates on.
DROP FUNCTION IF EXISTS substrate.neighborhood(BYTEA, INT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.neighborhood(
    p_entity_hash       BYTEA,
    p_arena_id          INT              DEFAULT NULL,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    relation         TEXT,
    neighbor_hash    BYTEA,
    edge_type_code   TEXT,
    edge_role_code   TEXT,
    edge_mu          DOUBLE PRECISION,
    frechet_distance DOUBLE PRECISION,
    sequence_ordinal INT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- 1. Outbound edges: this entity is in the source role.
    SELECT
        'outbound_edge'::TEXT AS relation,
        em_t.entity_hash      AS neighbor_hash,
        et.code               AS edge_type_code,
        r_t.code              AS edge_role_code,
        COALESCE(es.mu, p.initial_mu * et.semantic_weight * p.derivation_decay) AS edge_mu,
        NULL::DOUBLE PRECISION AS frechet_distance,
        NULL::INT             AS sequence_ordinal
    FROM substrate.edge_member em_s
    JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
    JOIN substrate.edge e ON e.edge_type_id = em_s.edge_type_id AND e.hash = em_s.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.provenance p  ON p.id  = e.provenance_id
    JOIN substrate.edge_member em_t
      ON em_t.edge_type_id = em_s.edge_type_id
     AND em_t.edge_hash    = em_s.edge_hash
     AND em_t.entity_hash <> em_s.entity_hash
    JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id
    LEFT JOIN substrate.edge_significance es
      ON es.context_type_id = COALESCE(p_arena_id, es.context_type_id)
     AND es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND (p_arena_id IS NULL OR es.context_type_id = p_arena_id)
    WHERE em_s.entity_hash = p_entity_hash

    UNION ALL

    -- 2. Inbound edges: this entity is in a target / non-source role.
    SELECT
        'inbound_edge'::TEXT,
        em_other.entity_hash,
        et.code,
        r_self.code,
        COALESCE(es.mu, p.initial_mu * et.semantic_weight * p.derivation_decay),
        NULL::DOUBLE PRECISION,
        NULL::INT
    FROM substrate.edge_member em_self
    JOIN substrate.edge_role r_self ON r_self.id = em_self.edge_role_id
    JOIN substrate.edge e ON e.edge_type_id = em_self.edge_type_id AND e.hash = em_self.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.provenance p  ON p.id  = e.provenance_id
    JOIN substrate.edge_member em_other
      ON em_other.edge_type_id = em_self.edge_type_id
     AND em_other.edge_hash    = em_self.edge_hash
     AND em_other.entity_hash <> em_self.entity_hash
    LEFT JOIN substrate.edge_significance es
      ON es.context_type_id = COALESCE(p_arena_id, es.context_type_id)
     AND es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND (p_arena_id IS NULL OR es.context_type_id = p_arena_id)
    WHERE em_self.entity_hash = p_entity_hash
      AND r_self.code <> 'source'

    UNION ALL

    -- 3. Composition parents: compositions containing this entity.
    SELECT
        'composition_parent'::TEXT,
        s.parent_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.composition_parents(p_entity_hash) s

    UNION ALL

    -- 4. Composition children: entities this composition contains (if any).
    SELECT
        'composition_child'::TEXT,
        s.child_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.get_composition_children(p_entity_hash) s

    UNION ALL

    -- 5. Geometric neighbors: entities whose physicality is 4D-near.
    -- Bridges decomposer surface variants whose content hashes differ but
    -- whose 4D physicality coordinates cluster. Skipped when threshold<=0
    -- — the geometric branch can be a heavy join over physicality and
    -- callers may want to disable it for cheap edge-only lookups.
    SELECT
        'frechet_neighbor'::TEXT,
        p_other.entity_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        substrate.dist_4d(substrate.geometryzm_to_geometry4d(p_self.geom), substrate.geometryzm_to_geometry4d(p_other.geom)),
        NULL::INT
    FROM substrate.physicality p_self
    JOIN substrate.physicality p_other
      ON p_other.entity_hash <> p_self.entity_hash
     AND p_other.physicality_type_id = p_self.physicality_type_id
    WHERE p_self.entity_hash = p_entity_hash
      AND p_frechet_threshold > 0
      AND p_self.geom IS NOT NULL
      AND p_other.geom IS NOT NULL
      AND substrate.dist_4d(substrate.geometryzm_to_geometry4d(p_self.geom), substrate.geometryzm_to_geometry4d(p_other.geom)) <= p_frechet_threshold;
$$;

COMMENT ON FUNCTION substrate.neighborhood(BYTEA, INT, DOUBLE PRECISION) IS
    'Hub view of one entity: outbound edges, inbound edges, sequence parents, sequence children, geometric (Fréchet) neighbors. Cross-decomposer surface variants bridge here via geometric proximity over substrate.physicality. The raw signal the brain operates on.';

-- ── sql/schema/functions/surprise.sql ───────────────────────────────────────
-- substrate.surprise(p_top_k) — open-ended fact selection.
--
-- For prompts that don't point at a specific entity ("tell me something
-- interesting"), direct recall is the wrong operation. The brain instead
-- picks structurally interesting entities from the substrate:
--   * high mu (well-corroborated)
--   * synset-tier (carries gloss text via has_gloss)
--   * not yet served in the current user_session (avoids repetition)
--
-- Returns up to p_top_k candidate facts, each with its associated text
-- (recomposed gloss) and confidence. The caller picks whichever fits the
-- prompt's framing.
DROP FUNCTION IF EXISTS substrate.surprise(INT, INT);
CREATE OR REPLACE FUNCTION substrate.surprise(
    p_top_k       INT DEFAULT 5,
    p_max_depth   INT DEFAULT 100000
) RETURNS TABLE (
    rank          INT,
    target_hash   BYTEA,
    confidence    DOUBLE PRECISION,
    answer        TEXT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH high_mu_synsets AS (
        SELECT
            c.entity_hash,
            -- Pick the highest mu across all arenas for ranking.
            MAX(es.mu) AS best_mu
        FROM substrate.entity_classification c
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        JOIN substrate.edge_member em ON em.entity_hash = c.entity_hash
        JOIN substrate.edge_significance es
          ON es.edge_type_id = em.edge_type_id
         AND es.edge_hash    = em.edge_hash
        WHERE et.code = 'synset'
        GROUP BY c.entity_hash
        ORDER BY best_mu DESC NULLS LAST, c.entity_hash
        LIMIT p_top_k * 4    -- oversample so we can filter to ones with glosses
    ),
    with_gloss AS (
        SELECT
            h.entity_hash,
            h.best_mu,
            -- Find the gloss text_composition this synset has_gloss to.
            (SELECT em_t.entity_hash
               FROM substrate.edge e
               JOIN substrate.edge_type et2 ON et2.id = e.edge_type_id
               JOIN substrate.edge_member em_s
                 ON em_s.edge_type_id = e.edge_type_id
                AND em_s.edge_hash    = e.hash
               JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
               JOIN substrate.edge_member em_t
                 ON em_t.edge_type_id = e.edge_type_id
                AND em_t.edge_hash    = e.hash
               JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
              WHERE em_s.entity_hash = h.entity_hash
                AND et2.code = 'has_gloss'
                AND EXISTS (SELECT 1 FROM substrate.get_composition_children(em_t.entity_hash) LIMIT 1)
              LIMIT 1
            ) AS gloss_hash
        FROM high_mu_synsets h
    )
    -- Gate 1 reopened item #36 (2026-05-18): substrate.recompose_text removed.
    -- The recomposition surface is now the C# bulk-tier walker
    -- (Hartonomous.Core.Recomposition.BulkTierContentWalk). This SQL function
    -- returns the gloss target's entity hash + NULL answer; callers must
    -- pass the gloss_hash through ContentRecomposer.RecomposeAsync to
    -- materialize the surface text. p_max_depth is preserved on the
    -- signature for compatibility (unused).
    SELECT
        ROW_NUMBER() OVER (ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash)::INT AS rank,
        w.gloss_hash AS target_hash,
        w.best_mu    AS confidence,
        NULL::TEXT   AS answer
    FROM with_gloss w
    WHERE w.gloss_hash IS NOT NULL
    ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash
    LIMIT p_top_k;
$$;

COMMENT ON FUNCTION substrate.surprise(INT, INT) IS
    'Open-ended fact selector. Picks up to p_top_k high-mu synsets that have associated gloss text. Returns the gloss target hash and confidence; the answer column is NULL — caller materializes the surface text via the C# ContentRecomposer (Gate 1 #36, 2026-05-18). p_max_depth preserved on signature for compatibility.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- AI operation primitives (V1)

-- ── sql/schema/functions/embed_lookup.sql ───────────────────────────────────────
-- substrate.embed_lookup(seed_hash, entity_type_code, k, distance_kind)
--
-- Top-k entities by 4D distance from the seed's stored physicality. The seed
-- supplies its own geometry; the candidate set is filtered by entity_type
-- (which lives on substrate.entity_classification, since substrate.entity is
-- hash-only). All inner work — neighbor enumeration, distance evaluation,
-- top-k heap — happens inside the pg_similarity_topk C SRF; this plpgsql
-- function only resolves the seed centroid and the entity-type filter, then
-- hands the candidate query to the C kernel.
--
-- Distance kinds:
--   '4d'      → substrate.dist_4d (POINT4D short-circuits to native
--               distance_4d; multi-vertex geometries fall through to native
--               frechet_4d over native trajectory vertices).
--   'frechet' → substrate.frechet_4d_geom (always Fréchet over depth-first
--               vertex sequence, even for two POINTs — costs more, but
--               useful when comparing trajectory shapes).
--   's3'      → reserved for unit-quaternion S3 distance; not yet wired
--               (substrate.dist_s3(geometry, geometry) wrapper is a TODO).
--               pg_similarity_topk will ereport on this kind today.
DROP FUNCTION IF EXISTS substrate.embed_lookup(BYTEA, TEXT, INT, TEXT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.embed_lookup(
    p_seed_hash         BYTEA,
    p_entity_type_code  TEXT,
    p_k                 INT              DEFAULT 10,
    p_distance_kind     TEXT             DEFAULT '4d',
    p_distance_threshold DOUBLE PRECISION DEFAULT NULL
) RETURNS TABLE (
    entity_type_id INT,
    entity_hash    BYTEA,
    distance       DOUBLE PRECISION,
    elapsed_ms     INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started          TIMESTAMP := clock_timestamp();
    v_entity_type_id   INT;
    v_seed_geom        GEOMETRY;
    v_candidate_query  TEXT;
BEGIN
    SELECT id INTO v_entity_type_id
    FROM substrate.entity_type
    WHERE code = p_entity_type_code;

    IF v_entity_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown entity_type code: %', p_entity_type_code
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Resolve the seed centroid. Take the first physicality available for
    -- this entity (most entities have exactly one; multi-physicality entities
    -- like firefly atoms get the lowest physicality_type_id deterministically).
    SELECT geom INTO v_seed_geom
    FROM substrate.physicality
    WHERE entity_hash = p_seed_hash
    ORDER BY physicality_type_id
    LIMIT 1;

    IF v_seed_geom IS NULL THEN
        RAISE EXCEPTION 'seed entity has no physicality: hash=%',
            encode(p_seed_hash, 'hex')
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Candidate query: every entity classified as the requested type that
    -- has a physicality. The (entity_type_id, entity_hash) index on
    -- substrate.entity_classification gives O(log N) bounded scan; the JOIN
    -- to physicality is selective via the same hash. We exclude the seed
    -- itself from candidates.
    v_candidate_query := format(
        'SELECT %s::int AS entity_type_id, p.entity_hash, p.geom '
        || 'FROM substrate.entity_classification c '
        || 'JOIN substrate.physicality p ON p.entity_hash = c.entity_hash '
        || 'WHERE c.entity_type_id = %s '
        || '  AND c.entity_hash <> %L::bytea',
        v_entity_type_id,
        v_entity_type_id,
        p_seed_hash);

    RETURN QUERY
    SELECT
        s.entity_type_id,
        s.entity_hash,
        s.distance,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT AS elapsed_ms
    FROM substrate.similarity_topk(
        v_seed_geom,
        p_k,
        p_distance_kind,
        v_candidate_query,
        p_distance_threshold) s;
END $$;

COMMENT ON FUNCTION substrate.embed_lookup(BYTEA, TEXT, INT, TEXT, DOUBLE PRECISION) IS
    'Top-k entities by 4D distance from the seed entity''s stored physicality, filtered to a target entity_type via substrate.entity_classification. Uses the pg_similarity_topk C SRF for the inner scan and heap. Distance kinds: 4d (default; POINT4D fast path) | frechet (vertex-stream Frechet) | s3.';

-- ── sql/schema/functions/classify.sql ───────────────────────────────────────
-- substrate.classify(seed_hash, junction_kind, k)
--
-- Top-k labels for an entity from a junction table, ranked by Glicko-2 mu
-- desc, sigma asc (tighter confidence wins ties). Junction kinds:
--   'pos'           → substrate.entity_pos          (Glicko-2 native, stratified)
--   'sense'         → has_sense substrate edges     (Glicko-2 edge significance)
--   'pattern_deprel'→ substrate.pattern_deprel      (Glicko-2 native, stratified)
--   'language'      → substrate.entity_language     (no Glicko, single per-entity assertion)
--   'morph_feature' → substrate.entity_morph_feature(no Glicko, per-feature assertion)
--   'classification'→ substrate.entity_classification(entity_type provenance trail)
--
-- This is reference-table-resolution, not edge traversal. The substrate's
-- "what kind of thing is this entity" surface is junction-indexed and
-- microsecond-fast. Edge-graph traversal lives in substrate.infer / .recall.
DROP FUNCTION IF EXISTS substrate.classify(BYTEA, TEXT, INT);
CREATE OR REPLACE FUNCTION substrate.classify(
    p_seed_hash      BYTEA,
    p_junction_kind  TEXT,
    p_k              INT DEFAULT 10
) RETURNS TABLE (
    label_id    INT,
    label_code  TEXT,
    mu          DOUBLE PRECISION,
    sigma       DOUBLE PRECISION,
    games       INT,
    elapsed_ms  INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started TIMESTAMP := clock_timestamp();
BEGIN
    IF p_junction_kind = 'pos' THEN
        RETURN QUERY
         SELECT p.id,
             p.code,
             AVG(ep.mu)::DOUBLE PRECISION,
             AVG(ep.sigma)::DOUBLE PRECISION,
             COALESCE(SUM(ep.games), 0)::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_pos ep
        JOIN substrate.pos p ON p.id = ep.pos_id
        WHERE ep.entity_hash = p_seed_hash
         GROUP BY p.id, p.code
         ORDER BY AVG(ep.mu) DESC, AVG(ep.sigma) ASC, p.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'sense' THEN
        RETURN QUERY
         WITH constants AS (
             SELECT et.id AS edge_type_id,
                 er_source.id AS source_role_id,
                 er_target.id AS target_role_id,
                 sc.id AS context_type_id
            FROM substrate.edge_type et
            JOIN substrate.edge_role er_source ON er_source.code = 'source'
            JOIN substrate.edge_role er_target ON er_target.code = 'target'
            JOIN substrate.significance_context sc ON sc.code = 'lexical_disambiguation'
              WHERE et.code = 'has_sense'
         ), ranked AS (
             SELECT encode(target_member.entity_hash, 'hex') AS label_code,
                 COALESCE(AVG(es.mu), 1500.0)::DOUBLE PRECISION AS mu,
                 COALESCE(AVG(es.sigma), 350.0)::DOUBLE PRECISION AS sigma,
                 COALESCE(SUM(es.games), 0)::INT AS games
            FROM constants c
            JOIN substrate.edge e
              ON e.edge_type_id = c.edge_type_id
            JOIN substrate.edge_member source_member
              ON source_member.edge_type_id = e.edge_type_id
             AND source_member.edge_hash = e.hash
             AND source_member.edge_role_id = c.source_role_id
             AND source_member.entity_hash = p_seed_hash
            JOIN substrate.edge_member target_member
              ON target_member.edge_type_id = e.edge_type_id
             AND target_member.edge_hash = e.hash
             AND target_member.edge_role_id = c.target_role_id
            LEFT JOIN substrate.edge_significance es
              ON es.context_type_id = c.context_type_id
             AND es.edge_type_id = e.edge_type_id
             AND es.edge_hash = e.hash
              GROUP BY target_member.entity_hash
         )
         SELECT row_number() OVER (ORDER BY ranked.mu DESC, ranked.sigma ASC, ranked.label_code ASC)::INT AS label_id,
             ranked.label_code,
             ranked.mu,
             ranked.sigma,
             ranked.games,
             EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
           FROM ranked
          ORDER BY ranked.mu DESC, ranked.sigma ASC, ranked.label_code ASC
          LIMIT p_k;

    ELSIF p_junction_kind = 'pattern_deprel' THEN
        RETURN QUERY
         SELECT d.id,
             d.code,
             AVG(pd.mu)::DOUBLE PRECISION,
             AVG(pd.sigma)::DOUBLE PRECISION,
             COALESCE(SUM(pd.games), 0)::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.pattern_deprel pd
        JOIN substrate.deprel d ON d.id = pd.deprel_id
        WHERE pd.entity_hash = p_seed_hash
         GROUP BY d.id, d.code
         ORDER BY AVG(pd.mu) DESC, AVG(pd.sigma) ASC, d.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'language' THEN
        RETURN QUERY
         SELECT l.id, l.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_language el
        JOIN substrate.language l ON l.id = el.language_id
        WHERE el.entity_hash = p_seed_hash
        ORDER BY l.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'morph_feature' THEN
        RETURN QUERY
        SELECT mf.id, mf.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_morph_feature emf
        JOIN substrate.morph_feature mf ON mf.id = emf.morph_feature_id
        WHERE emf.entity_hash = p_seed_hash
        ORDER BY mf.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'classification' THEN
        RETURN QUERY
        SELECT et.id, et.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_classification ec
        JOIN substrate.entity_type et ON et.id = ec.entity_type_id
        WHERE ec.entity_hash = p_seed_hash
        ORDER BY et.code ASC
        LIMIT p_k;

    ELSE
        RAISE EXCEPTION 'unknown junction_kind: %, expected pos|sense|pattern_deprel|language|morph_feature|classification', p_junction_kind
            USING ERRCODE = 'invalid_parameter_value';
    END IF;
END $$;

COMMENT ON FUNCTION substrate.classify(BYTEA, TEXT, INT) IS
    'Top-k labels for an entity. pos/pattern_deprel aggregate stratified junction Glicko rows; sense ranks has_sense edges in lexical_disambiguation and returns synset hashes as labels; language, morph_feature, classification return default rating values for a stable non-null result shape.';

-- ── sql/schema/functions/rerank.sql ───────────────────────────────────────
-- substrate.rerank(candidate_hashes, arena_code, k)
--
-- Rerank a candidate set of entities by their Glicko-2 mu in the named
-- arena (sigma asc as tie-break — tighter confidence wins). Candidates that
-- have no rating in the arena get default 1500 mu / 350 sigma so unrated
-- candidates fall mid-pack rather than being silently dropped. Returns the
-- top-k.
--
-- Use cases:
--   - Cross-source rerank: union top-k from embed_lookup across multiple
--     entity_types, then rerank by global semantic_relevance arena.
--   - Authority-weighted rerank: same candidate set, sort by source_authority
--     arena to prefer canonical sources.
--   - Multi-arena composite: caller invokes rerank twice in different arenas
--     and combines results.
DROP FUNCTION IF EXISTS substrate.rerank(BYTEA[], TEXT, INT);
CREATE OR REPLACE FUNCTION substrate.rerank(
    p_candidate_hashes BYTEA[],
    p_arena_code       TEXT,
    p_k                INT DEFAULT 25
) RETURNS TABLE (
    entity_hash BYTEA,
    mu          DOUBLE PRECISION,
    sigma       DOUBLE PRECISION,
    games       INT,
    rank        INT,
    elapsed_ms  INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started     TIMESTAMP := clock_timestamp();
    v_arena_id    INT;
    v_default_mu  DOUBLE PRECISION := 1500.0;
    v_default_sig DOUBLE PRECISION := 350.0;
BEGIN
    SELECT id INTO v_arena_id
    FROM substrate.significance_context
    WHERE code = p_arena_code;

    IF v_arena_id IS NULL THEN
        RAISE EXCEPTION 'unknown arena code: %', p_arena_code
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    IF p_candidate_hashes IS NULL OR array_length(p_candidate_hashes, 1) IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH cands AS (
        SELECT DISTINCT h AS entity_hash
        FROM unnest(p_candidate_hashes) h
        WHERE h IS NOT NULL
    ),
    ranked AS (
        SELECT
            c.entity_hash,
            COALESCE(s.mu,    v_default_mu)  AS mu,
            COALESCE(s.sigma, v_default_sig) AS sigma,
            COALESCE(s.games, 0)             AS games
        FROM cands c
        LEFT JOIN substrate.entity_significance s
               ON s.context_type_id = v_arena_id
              AND s.entity_hash     = c.entity_hash
    )
    SELECT
        r.entity_hash,
        r.mu,
        r.sigma,
        r.games,
        ROW_NUMBER() OVER (ORDER BY r.mu DESC, r.sigma ASC, r.entity_hash ASC)::INT AS rank,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT AS elapsed_ms
    FROM ranked r
    ORDER BY r.mu DESC, r.sigma ASC, r.entity_hash ASC
    LIMIT p_k;
END $$;

COMMENT ON FUNCTION substrate.rerank(BYTEA[], TEXT, INT) IS
    'Rerank a candidate entity set by Glicko-2 mu in the named arena (sigma asc tie-break). Unrated candidates get default 1500 mu / 350 sigma so they fall mid-pack instead of being dropped. Returns top-k with rank, mu, sigma, games.';

-- ── sql/schema/functions/complete.sql ───────────────────────────────────────
-- substrate.complete(seed_hash, max_depth, max_results, lang_code)
--
-- Code-completion specialization of substrate.infer. Constrains traversal to
-- the code_completion arena (where Qwen-Coder / DeepSeek-Coder donor edges
-- carry their primed mu) and biases candidate targets toward bpe_token /
-- word_form entities tagged with the requested programming language via
-- substrate.entity_classification + substrate.entity_language.
--
-- Returns the best continuation as a recomposed text composition.
DROP FUNCTION IF EXISTS substrate.complete(BYTEA, INT, INT, TEXT);
CREATE OR REPLACE FUNCTION substrate.complete(
    p_seed_hash    BYTEA,
    p_max_depth    INT  DEFAULT 4,
    p_max_results  INT  DEFAULT 25,
    p_lang_code    TEXT DEFAULT NULL
) RETURNS TABLE (
    answer_text     TEXT,
    seed_count      INT,
    distinct_targets BIGINT,
    best_target_hash BYTEA,
    best_total_mu    DOUBLE PRECISION,
    elapsed_ms       INT
)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_started     TIMESTAMP := clock_timestamp();
    v_arena_id    INT;
    v_lang_id     INT;
    v_seed_count  INT := 0;
    v_targets     BIGINT := 0;
    v_best_hash   BYTEA;
    v_best_mu     DOUBLE PRECISION := 0.0;
    v_answer      TEXT;
BEGIN
    SELECT id INTO v_arena_id
    FROM substrate.significance_context
    WHERE code = 'code_completion';

    -- code_completion arena is open-vocabulary; if absent, fall back to
    -- semantic_relevance so the call still produces a result rather than
    -- erroring on a fresh substrate that hasn't seen the arena yet.
    IF v_arena_id IS NULL THEN
        SELECT id INTO v_arena_id
        FROM substrate.significance_context
        WHERE code = 'semantic_relevance';
    END IF;

    IF p_lang_code IS NOT NULL THEN
        SELECT id INTO v_lang_id
        FROM substrate.language
        WHERE code = p_lang_code;
    END IF;

    -- Seed activation: bpe_token / word_form children of the prompt
    -- composition, optionally filtered by the requested programming
    -- language via entity_language.
    WITH seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_seed_hash) s
        JOIN substrate.entity_classification c ON c.entity_hash = s.child_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        LEFT JOIN substrate.entity_language el
               ON el.entity_hash = s.child_hash
              AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
        WHERE et.code IN ('bpe_token', 'word_form')
          AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
    ),
    seed_count AS (SELECT count(*) AS n FROM seeds)
    SELECT n INTO v_seed_count FROM seed_count;

    IF v_seed_count = 0 THEN
        RETURN QUERY
        SELECT NULL::TEXT, 0, 0::BIGINT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

        -- Walk one step out from each seed, accumulating Glicko-2 mu in the
        -- code_completion arena, and pick the best candidate.
        SELECT count(*), max(cands.total_mu),
                     (array_agg(cands.target_hash ORDER BY cands.total_mu DESC))[1]
    INTO v_targets, v_best_mu, v_best_hash
            FROM (
                    SELECT ranked.target_hash, ranked.total_mu
                        FROM (
                                SELECT em_t.entity_hash AS target_hash,
                                             sum(COALESCE(es.mu, 1500.0)) AS total_mu,
                                             row_number() OVER (
                                                     ORDER BY sum(COALESCE(es.mu, 1500.0)) DESC, em_t.entity_hash ASC
                                             ) AS rn
                                    FROM substrate.get_composition_children(p_seed_hash) sq
                                    JOIN substrate.edge_member em_s
                                        ON em_s.entity_hash = sq.child_hash
                                    JOIN substrate.edge e
                                        ON e.edge_type_id = em_s.edge_type_id
                                     AND e.hash = em_s.edge_hash
                                    JOIN substrate.edge_role r_s
                                        ON r_s.id = em_s.edge_role_id
                                     AND r_s.code = 'source'
                                    JOIN substrate.edge_member em_t
                                        ON em_t.edge_type_id = e.edge_type_id
                                     AND em_t.edge_hash = e.hash
                                    JOIN substrate.edge_role r_t
                                        ON r_t.id = em_t.edge_role_id
                                     AND r_t.code = 'target'
                                    LEFT JOIN substrate.edge_significance es
                                        ON es.edge_type_id = e.edge_type_id
                                     AND es.edge_hash = e.hash
                                     AND es.context_type_id = v_arena_id
                                 WHERE em_t.entity_hash <> p_seed_hash
                                 GROUP BY em_t.entity_hash
                        ) ranked
                     WHERE ranked.rn <= GREATEST(COALESCE(p_max_results, 25), 0)
            ) cands;

    IF v_best_hash IS NOT NULL THEN
        v_answer := substrate.recompose_text(v_best_hash, p_max_depth);
    END IF;

    RETURN QUERY
    SELECT COALESCE(v_answer, '')::TEXT,
           v_seed_count,
           v_targets,
           v_best_hash,
           v_best_mu,
           EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.complete(BYTEA, INT, INT, TEXT) IS
    'Code-completion specialization of substrate.infer. Constrains traversal to the code_completion arena (falls back to semantic_relevance if the arena does not yet exist) and biases candidate targets to bpe_token/word_form entities tagged with the requested programming language via entity_language. Recomposes the best continuation via substrate.recompose_text.';

-- ── sql/schema/functions/bind_bpe_tokens_to_seed_pos.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_pos(p_model_source_id INT)
RETURNS BIGINT
LANGUAGE sql VOLATILE AS $f$
    WITH att AS (
        SELECT id FROM substrate.attestation_type WHERE code = 'model_attention_pattern'
    ),
    inserted AS (
        -- Propagated POS attestations land as model_attention_pattern: the
        -- BPE token's POS is asserted because the model's covers_lemma edge
        -- ties it to a lemma whose POS is curated. The attestation kind is
        -- model-derived — separate from the underlying lemma_pos rating row
        -- which carries lexical_curated_relation evidence.
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, attestation_type_id, mu, sigma)
        SELECT DISTINCT token_member.entity_hash, lemma_pos.pos_id, att.id, lemma_pos.mu, lemma_pos.sigma
          FROM substrate.edge coverage
          CROSS JOIN att
          JOIN substrate.edge_type coverage_type ON coverage_type.id = coverage.edge_type_id
          JOIN substrate.edge_member token_member
            ON token_member.edge_type_id = coverage.edge_type_id
           AND token_member.edge_hash = coverage.hash
          JOIN substrate.edge_role token_role
            ON token_role.id = token_member.edge_role_id
           AND token_role.code = 'source'
          JOIN substrate.edge_member lemma_member
            ON lemma_member.edge_type_id = coverage.edge_type_id
           AND lemma_member.edge_hash = coverage.hash
          JOIN substrate.edge_role lemma_role
            ON lemma_role.id = lemma_member.edge_role_id
           AND lemma_role.code = 'target'
          JOIN substrate.entity_pos lemma_pos ON lemma_pos.entity_hash = lemma_member.entity_hash
          JOIN substrate.entity_model_source model_entity
            ON model_entity.entity_hash = token_member.entity_hash
         WHERE coverage_type.code = 'covers_lemma'
           AND model_entity.model_source_id = p_model_source_id
        ON CONFLICT (entity_hash, pos_id, attestation_type_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$f$;

COMMENT ON FUNCTION substrate.bind_bpe_tokens_to_seed_pos(INT) IS
    'Propagate POS junction evidence from lemma targets to model bpe_token sources over covers_lemma edges.';

-- ── sql/schema/functions/bind_bpe_tokens_to_seed_morph.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_morph(p_model_source_id INT)
RETURNS BIGINT
LANGUAGE sql VOLATILE AS $f$
    WITH inserted AS (
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT DISTINCT token_member.entity_hash, lemma_morph.morph_feature_id
          FROM substrate.edge coverage
          JOIN substrate.edge_type coverage_type ON coverage_type.id = coverage.edge_type_id
          JOIN substrate.edge_member token_member
            ON token_member.edge_type_id = coverage.edge_type_id
           AND token_member.edge_hash = coverage.hash
          JOIN substrate.edge_role token_role
            ON token_role.id = token_member.edge_role_id
           AND token_role.code = 'source'
          JOIN substrate.edge_member lemma_member
            ON lemma_member.edge_type_id = coverage.edge_type_id
           AND lemma_member.edge_hash = coverage.hash
          JOIN substrate.edge_role lemma_role
            ON lemma_role.id = lemma_member.edge_role_id
           AND lemma_role.code = 'target'
          JOIN substrate.entity_morph_feature lemma_morph
            ON lemma_morph.entity_hash = lemma_member.entity_hash
          JOIN substrate.entity_model_source model_entity
            ON model_entity.entity_hash = token_member.entity_hash
         WHERE coverage_type.code = 'covers_lemma'
           AND model_entity.model_source_id = p_model_source_id
        ON CONFLICT (entity_hash, morph_feature_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$f$;

COMMENT ON FUNCTION substrate.bind_bpe_tokens_to_seed_morph(INT) IS
    'Propagate morphological feature junction evidence from lemma targets to model bpe_token sources over covers_lemma edges.';

-- ── sql/schema/functions/claim_or_get_embedding_anchor.sql ───────────────────────────────────────
-- substrate.claim_or_get_embedding_anchor(p_model_source_id, p_intersection_count)
--
-- Atomic anchor selection for cross-model embedding alignment. Returns the
-- existing anchor's model_source_id if any; otherwise claims the supplied
-- model as the canonical anchor (first-write-wins via ON CONFLICT). The
-- caller (EmbeddingAlignmentPass) compares the returned id with its own
-- to decide whether to skip alignment (it IS the anchor) or proceed
-- (Procrustes-fit a rotation against the anchor).

CREATE OR REPLACE FUNCTION substrate.claim_or_get_embedding_anchor(
    p_model_source_id    INT,
    p_intersection_count INT
) RETURNS INT
LANGUAGE SQL
VOLATILE
AS $$
    INSERT INTO substrate.embedding_alignment_anchor
        (model_source_id, vocab_intersection_token_count)
    VALUES
        (p_model_source_id, p_intersection_count)
    ON CONFLICT (model_source_id) DO NOTHING;

    SELECT model_source_id
      FROM substrate.embedding_alignment_anchor
     ORDER BY set_at ASC
     LIMIT 1;
$$;

COMMENT ON FUNCTION substrate.claim_or_get_embedding_anchor(INT, INT) IS
    'Returns current canonical embedding anchor''s model_source_id (first-write-wins). Atomic via ON CONFLICT DO NOTHING. Used by EmbeddingAlignmentPass to decide anchor-vs-aligner role.';

-- ── sql/schema/functions/embedding_firefly_token_hashes.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.embedding_firefly_token_hashes(p_model_source_id INT)
RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT DISTINCT p.entity_hash
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE ems.model_source_id = p_model_source_id
       AND pt.code = 'firefly'
     ORDER BY p.entity_hash ASC;
$f$;

COMMENT ON FUNCTION substrate.embedding_firefly_token_hashes(INT) IS
    'Return bpe_token entity hashes with embedding_firefly physicality for one model_source.';

-- ── sql/schema/functions/apply_firefly_rotation.sql ───────────────────────────────────────
-- substrate.apply_firefly_rotation(p_model_source_id, R 3x3)
--
-- Rotate every firefly POINTZM physicality of a given model_source by a
-- 3×3 orthogonal matrix R, leaving the M coordinate (L2 magnitude)
-- untouched. Run after EmbeddingFireflyPass for non-anchor models. R must
-- be orthogonal (det = +1); the caller is responsible — Procrustes
-- (Kabsch) returns such an R.
--
-- PostGIS-native geom: builds the rotated point via ST_MakePoint(x, y, z, m)
-- — returns geometry(POINTZM). The original (X, Y, Z) extracted via
-- ST_X / ST_Y / ST_Z; M passed through unchanged.
CREATE OR REPLACE FUNCTION substrate.apply_firefly_rotation(
    p_model_source_id INT,
    p_r00 FLOAT8, p_r01 FLOAT8, p_r02 FLOAT8,
    p_r10 FLOAT8, p_r11 FLOAT8, p_r12 FLOAT8,
    p_r20 FLOAT8, p_r21 FLOAT8, p_r22 FLOAT8
) RETURNS BIGINT
LANGUAGE SQL
VOLATILE
AS $$
    WITH updated AS (
        UPDATE substrate.physicality p
           SET geom = ST_MakePoint(
                  p_r00 * ST_X(p.geom)
                      + p_r01 * ST_Y(p.geom)
                      + p_r02 * ST_Z(p.geom),
                  p_r10 * ST_X(p.geom)
                      + p_r11 * ST_Y(p.geom)
                      + p_r12 * ST_Z(p.geom),
                  p_r20 * ST_X(p.geom)
                      + p_r21 * ST_Y(p.geom)
                      + p_r22 * ST_Z(p.geom),
                  ST_M(p.geom)
              )
          FROM substrate.entity_model_source ems,
              substrate.physicality_type pt
         WHERE p.entity_hash         = ems.entity_hash
           AND ems.model_source_id   = p_model_source_id
           AND p.physicality_type_id = pt.id
           AND pt.code               = 'firefly'
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM updated;
$$;

COMMENT ON FUNCTION substrate.apply_firefly_rotation(INT, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8) IS
    'Rotate every firefly POINTZM of one model_source by a 3×3 orthogonal R. M (L2 magnitude) preserved. Caller (Procrustes/Kabsch) ensures det(R)=+1. Returns count of rotated rows.';

-- ── sql/schema/functions/get_firefly_coords.sql ───────────────────────────────────────
-- substrate.get_firefly_coords(p_bpe_token_entity_hashes BYTEA[], p_model_source_id INT)
--
-- Return per-entity firefly POINTZM (X, Y, Z) for a vocab intersection
-- set, scoped to one model_source. Used by EmbeddingAlignmentPass to pull
-- the (anchor, this-model) coordinate pairs into managed memory for
-- Procrustes/Kabsch fitting.
--
-- PostGIS-native: physicality.geom is geometry(POINTZM); ST_X / ST_Y / ST_Z
-- extract coordinates directly without going through point4d_to_array.
-- M (L2 magnitude) intentionally omitted — Kabsch rotation operates on
-- the 3D direction and M is preserved separately.
CREATE OR REPLACE FUNCTION substrate.get_firefly_coords(
    p_bpe_token_entity_hashes BYTEA[],
    p_model_source_id         INT
) RETURNS TABLE (
    entity_hash BYTEA,
    x           FLOAT8,
    y           FLOAT8,
    z           FLOAT8
)
LANGUAGE SQL
STABLE
AS $$
    SELECT p.entity_hash,
           ST_X(p.geom) AS x,
           ST_Y(p.geom) AS y,
           ST_Z(p.geom) AS z
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems
        ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt
        ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = ANY(p_bpe_token_entity_hashes)
       AND ems.model_source_id = p_model_source_id
       AND pt.code = 'firefly'
     ORDER BY p.entity_hash ASC;
$$;

COMMENT ON FUNCTION substrate.get_firefly_coords(BYTEA[], INT) IS
    'Per-entity firefly XYZ for a vocab intersection set, scoped to one model_source. Ordered by entity_hash ASC so cross-model calls return aligned arrays. Used by EmbeddingAlignmentPass for Procrustes input.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Universal substrate query surface (V1)

-- ── sql/schema/functions/model_inventory.sql ───────────────────────────────────────
-- substrate.model_inventory(p_model_arch_hash bytea)
--
-- Inventory of an ingested model's substrate state. V1 surface returns
-- counts that are reliably computable from the existing ingestion-time
-- substrate without name-parsing or junction-row population:
--
--   tensor_count                   total tensors via has_tensor edges
--   architectural_classification   total Track 2 architectural-classification
--                                  edges (attention_head_in_layer / ffn_*_in_layer
--                                  / vocab_embedding / etc.)
--   per_role_unit_count            per-role units bound to this model's tensors
--                                  (attention_pattern, ffn_neuron, embedding_position,
--                                  logit_projection, moe_expert_neuron, etc.)
--   embedding_firefly_count        Track 1 fireflies attached to token entities
--                                  reachable from this model
--
-- Layer / head / expert counts are NOT included until
-- substrate.tensor_position_index (migration 0037) is populated by the
-- decomposer (deferred until IIngestionBatch grows AddTensorPositionIndex).
-- The legacy approach of decoding edge_member.role_position is incorrect:
-- role_position is for ordering participants WITHIN AN EDGE, not content
-- placement. See migration 0037's commentary.
DROP FUNCTION IF EXISTS substrate.model_inventory(bytea);
CREATE OR REPLACE FUNCTION substrate.model_inventory(p_model_arch_hash bytea)
RETURNS TABLE (
    metric_code text,
    metric_value bigint,
    metric_detail text
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- Tensor count: tensors bound to this model_architecture via has_tensor.
    SELECT 'tensor_count'::text,
           count(DISTINCT em_tgt.entity_hash)::bigint,
           NULL::text
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.code = 'has_tensor'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_src.entity_hash = p_model_arch_hash

    UNION ALL

    -- Architectural classification edges (Track 2 V1 vocabulary).
    SELECT 'architectural_classification'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.edge_member em_tgt
      JOIN substrate.edge_type et      ON et.id = em_tgt.edge_type_id
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_tgt.entity_hash = p_model_arch_hash
       AND et.code IN (
            'attention_head_in_layer',
            'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
            'residual_stream_position',
            'vocab_embedding','vocab_unembedding',
            'tokenizer_belongs_to_model',
            'position_encoding_for_layer',
            'layer_norm_for_layer_position',
            'tensor_in_model_at_position',
            'expert_in_moe_router','moe_router_for_layer','shared_expert_in_layer',
            'vision_feature_path','object_query_in_layer',
            'vision_classification_head','vision_localization_head',
            'cross_modal_attention',
            'audio_feature_path','audio_to_text_attention',
            'pipeline_component_of_model'
       )

    UNION ALL

    -- Per-role unit count: per-row analysis-pass entities (attention_pattern,
    -- ffn_neuron, embedding_position, logit_projection, moe_expert_neuron,
    -- etc.) bound to this model's tensors. Counts via the has_*_component /
    -- has_ffn_neuron / has_embedding_position / etc. edges that the existing
    -- analysis passes emit.
    SELECT 'per_role_unit_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.edge_member em_tensor_src
      JOIN substrate.edge_type et_has_tensor
        ON et_has_tensor.id = em_tensor_src.edge_type_id
       AND et_has_tensor.code = 'has_tensor'
      JOIN substrate.edge_role er_src
        ON er_src.id = em_tensor_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tensor_tgt
        ON em_tensor_tgt.edge_type_id = em_tensor_src.edge_type_id
       AND em_tensor_tgt.edge_hash    = em_tensor_src.edge_hash
      JOIN substrate.edge_role er_tgt
        ON er_tgt.id = em_tensor_tgt.edge_role_id AND er_tgt.code = 'target'
      JOIN substrate.edge_member em_unit_src
        ON em_unit_src.entity_hash = em_tensor_tgt.entity_hash
      JOIN substrate.edge_type et_has_unit
        ON et_has_unit.id = em_unit_src.edge_type_id
       AND et_has_unit.code IN (
            'has_attention_component','has_ffn_neuron','has_embedding_position',
            'has_logit_projection','has_moe_neuron','has_route_direction',
            'has_object_query','has_vision_feature','has_class_projection',
            'has_bbox_projection','has_codec_filter','has_conformer_component',
            'has_conv_filter','has_diffusion_component','has_lora_component',
            'has_modality_basis','has_layer_norm_scale','has_rope_freqs',
            'has_rank_component','has_moe_routing'
       )
     WHERE em_tensor_src.entity_hash = p_model_arch_hash

    UNION ALL

    -- Firefly count: firefly POINTZM physicalities on any substrate entity
    -- reachable from this model via entity_model_source. The substrate
    -- mechanic is universal — fireflies attach to whatever content-
    -- addressed entity the Procrustes/Laplacian projection from the
    -- model's N-dim embedding row landed on, regardless of classification
    -- (word_form / codepoint / pixel_region / audio_chunk / video_frame /
    -- lemma / synset / etc.). Modality- and language-agnostic by design.
    SELECT 'firefly_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'firefly'
      JOIN substrate.entity_model_source ems_entity
        ON ems_entity.entity_hash = p.entity_hash
      JOIN substrate.entity_model_source ems_arch
        ON ems_arch.model_source_id = ems_entity.model_source_id
       AND ems_arch.entity_hash = p_model_arch_hash;
$$;

COMMENT ON FUNCTION substrate.model_inventory(bytea) IS
    'Inventory of an ingested model: tensor count, architectural-classification edge count, per-role unit count, firefly count. Layer/head/expert counts deferred until tensor_position_index junction is populated.';

-- ── sql/schema/functions/model_vocab_recovered.sql ───────────────────────────────────────
-- substrate.model_vocab_recovered(p_model_arch_hash bytea)
--
-- Counts distinct vocab tokens recoverable from the substrate for a given
-- ingested model. Walks the existing has_token_in_tokenizer edge from the
-- model_architecture entity to word_form / bpe_token entities. Compared
-- against the model's declared `vocab_size` (from config.json) by the
-- D-vocab-recovered validation gate.
--
-- Returns a single row with the total recovered count. A model whose
-- recovered count is less than declared vocab_size is missing tokenizer
-- ingestion data; the gate fires before downstream recompose can succeed.
DROP FUNCTION IF EXISTS substrate.model_vocab_recovered(bytea);
CREATE OR REPLACE FUNCTION substrate.model_vocab_recovered(p_model_arch_hash bytea)
RETURNS BIGINT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT count(DISTINCT em_tgt.entity_hash)::bigint
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.code = 'has_token_in_tokenizer'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_src.entity_hash = p_model_arch_hash;
$$;

COMMENT ON FUNCTION substrate.model_vocab_recovered(bytea) IS
    'Distinct vocab tokens recoverable for a model via has_token_in_tokenizer edges. Compared against declared vocab_size by D-vocab-recovered gate.';

-- ── sql/schema/functions/cross_model_consensus.sql ───────────────────────────────────────
-- substrate.cross_model_consensus(p_token_hash bytea)
--
-- Voronoi-tessellation centroid + dispersion + agreement score over a
-- token entity's firefly cloud. Each model that has ingested this token
-- contributed one POINTZM physicality of type embedding_firefly.
--
-- PostGIS-native: physicality.geom is geometry(POINTZM); cast to
-- public.point4d via the geometry->point4d bridge for libhartonomous
-- kernel calls (centroid_4d aggregate, distance_4d).
DROP FUNCTION IF EXISTS substrate.cross_model_consensus(bytea);
CREATE OR REPLACE FUNCTION substrate.cross_model_consensus(p_token_hash bytea)
RETURNS TABLE (
    centroid        public.point4d,
    n_contributing  int,
    dispersion_max  double precision,
    agreement_score double precision
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        c.centroid,
        c.n,
        d.max_dist,
        CASE WHEN c.n = 0 THEN NULL
             ELSE 1.0 / (1.0 + COALESCE(d.max_dist, 0.0))
        END
      FROM (
          SELECT public.centroid_4d(p.geom::public.point4d) AS centroid,
                 count(*)::int                              AS n
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'firefly'
           WHERE p.entity_hash = p_token_hash
      ) c
      CROSS JOIN LATERAL (
          SELECT max(public.distance_4d(p.geom::public.point4d, c.centroid)) AS max_dist
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'firefly'
           WHERE p.entity_hash = p_token_hash
      ) d;
$$;

COMMENT ON FUNCTION substrate.cross_model_consensus(bytea) IS
    'Centroid + dispersion + agreement over a token''s firefly cloud. PostGIS POINTZM cast to public.point4d via the geometry->point4d bridge; aggregates via the libhartonomous centroid_4d + distance_4d native kernels.';

-- ── sql/schema/functions/cross_model_divergence.sql ───────────────────────────────────────
-- substrate.cross_model_divergence(p_token_hash bytea, p_model_a_arch_hash bytea, p_model_b_arch_hash bytea)
--
-- Pairwise 4D Euclidean distance between two models' fireflies for the
-- same token entity. Returns NULL when either model has no firefly for
-- the token. Drives D-cross-model-divergence-nonzero gate.
--
-- PostGIS-native: extracts (X, Y, Z, M) via ST_X / ST_Y / ST_Z / ST_M from
-- POINTZM geometry directly.
DROP FUNCTION IF EXISTS substrate.cross_model_divergence(bytea, bytea, bytea);
CREATE OR REPLACE FUNCTION substrate.cross_model_divergence(
    p_token_hash         bytea,
    p_model_a_arch_hash  bytea,
    p_model_b_arch_hash  bytea
)
RETURNS DOUBLE PRECISION
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH a AS (
        SELECT ST_X(p.geom) AS x,
               ST_Y(p.geom) AS y,
               ST_Z(p.geom) AS z,
               ST_M(p.geom) AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'firefly'
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_a
            ON ems_a.model_source_id = ems_t.model_source_id
           AND ems_a.entity_hash = p_model_a_arch_hash
         WHERE p.entity_hash = p_token_hash
    ),
    b AS (
        SELECT ST_X(p.geom) AS x,
               ST_Y(p.geom) AS y,
               ST_Z(p.geom) AS z,
               ST_M(p.geom) AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'firefly'
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_b
            ON ems_b.model_source_id = ems_t.model_source_id
           AND ems_b.entity_hash = p_model_b_arch_hash
         WHERE p.entity_hash = p_token_hash
    )
    SELECT sqrt((a.x - b.x) ^ 2 + (a.y - b.y) ^ 2 + (a.z - b.z) ^ 2 + (a.m - b.m) ^ 2)
      FROM a, b;
$$;

COMMENT ON FUNCTION substrate.cross_model_divergence(bytea, bytea, bytea) IS
    'Pairwise 4D distance between model A''s and model B''s fireflies for a shared token. Reads PostGIS POINTZM coords directly via ST_X / ST_Y / ST_Z / ST_M.';

-- ── sql/schema/functions/codepoint_property_rows.sql ───────────────────────────────────────
-- Per-codepoint runtime property rows for the inference-path
-- NpgsqlCodepointPropertiesCache (text segmentation + case folding).
--
-- Gate 1 #38 refactor 2026-05-18: rewritten against the new narrow per-property
-- analytics caches (substrate.cp_grapheme_break / cp_word_break /
-- cp_sentence_break / cp_line_break) populated by UnicodeDecomposer §3.
-- The wide flat substrate.codepoint_property junction (deleted) is replaced
-- by typed has_cp_* edges on substrate.edge plus these narrow tables for
-- index-locality lookups.
--
-- Codepoint entity identity = BLAKE3 over the codepoint integer. The JOIN
-- reverse-resolves codepoint_value → entity hash via substrate.cp_hash(cp)
-- (C extension binding for hartonomous_blake3_codepoint).
--
-- The case-fold and is_extended_pictographic fields are NULL in this
-- function pending the case-fold narrow caches and extended_pictographic
-- table landing. Callers fall back to the embedded UCD blob via
-- BlobUcdPropertyAccessor.{SimpleCaseFold, FullCaseFold,
-- IsExtendedPictographic} — siblings per Principle 1.
CREATE OR REPLACE FUNCTION substrate.codepoint_property_rows(p_codepoints INT[] DEFAULT NULL)
RETURNS TABLE (
    codepoint_value INT,
    gcb_id INT,
    wb_id INT,
    sb_id INT,
    lb_id INT,
    is_extended_pictographic BOOLEAN,
    simple_case_fold INT,
    full_case_fold INT[]
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH cps AS (
        SELECT u.cp::INT AS codepoint_value, substrate.cp_hash(u.cp::INT) AS entity_hash
          FROM unnest(COALESCE(p_codepoints, ARRAY(SELECT generate_series(0, 1114111)))) AS u(cp)
    )
    SELECT
        cps.codepoint_value,
        gcb.break_property_id  AS gcb_id,
        wb.break_property_id   AS wb_id,
        sb.break_property_id   AS sb_id,
        lb.break_property_id   AS lb_id,
        NULL::BOOLEAN          AS is_extended_pictographic,
        NULL::INT              AS simple_case_fold,
        NULL::INT[]            AS full_case_fold
      FROM cps
      LEFT JOIN substrate.cp_grapheme_break  gcb ON gcb.entity_hash = cps.entity_hash
      LEFT JOIN substrate.cp_word_break      wb  ON wb.entity_hash  = cps.entity_hash
      LEFT JOIN substrate.cp_sentence_break  sb  ON sb.entity_hash  = cps.entity_hash
      LEFT JOIN substrate.cp_line_break      lb  ON lb.entity_hash  = cps.entity_hash
     ORDER BY cps.codepoint_value;
$f$;

COMMENT ON FUNCTION substrate.codepoint_property_rows(INT[]) IS
    'Per-codepoint runtime properties from narrow per-property junctions. Gate 1 #38 refactor — case-fold and extended_pictographic fields are NULL pending narrow-cache landing; callers fall back to embedded UCD blob.';

-- ── sql/schema/functions/break_property_code_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.break_property_code_map()
RETURNS TABLE (id INT, code VARCHAR(32))
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT bp.id, bp.code
      FROM substrate.break_property bp
     ORDER BY bp.id;
$f$;

COMMENT ON FUNCTION substrate.break_property_code_map() IS
    'Return break_property id/code rows for C# UAX #29 cache compatibility.';

-- ── sql/schema/functions/break_property_full_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.break_property_full_map()
RETURNS TABLE (id INT, category TEXT, enum_id INT, code TEXT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT bp.id, bp.category::text, bp.enum_id, bp.code::text
      FROM substrate.break_property bp
     ORDER BY bp.category, bp.enum_id;
$f$;

COMMENT ON FUNCTION substrate.break_property_full_map() IS
    'Full break_property rows (id, category, enum_id, code) keyed for composite (category, enum_id) lookup in the UCD decomposer.';

-- ── sql/schema/functions/query_entities.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_entities(
    p_entity_type_codes    TEXT[] DEFAULT NULL,
    p_model_source_ids     INT[] DEFAULT NULL,
    p_min_significance_mu  FLOAT8 DEFAULT NULL,
    p_context_type_code    TEXT DEFAULT NULL,
    p_limit                INT DEFAULT NULL
)
  RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT results.entity_type_code, results.entity_hash
      FROM (
        SELECT DISTINCT et.code AS entity_type_code, e.hash AS entity_hash, ranked.mu AS rank_mu
          FROM substrate.entity e
          JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
          LEFT JOIN LATERAL (
              SELECT max(significance.mu) AS mu
                FROM substrate.entity_significance significance
                LEFT JOIN substrate.significance_context context
                  ON context.id = significance.context_type_id
               WHERE significance.entity_hash = e.hash
                 AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
          ) ranked ON TRUE
         WHERE (COALESCE(array_length(p_entity_type_codes, 1), 0) = 0 OR et.code = ANY(p_entity_type_codes))
           AND (COALESCE(array_length(p_model_source_ids, 1), 0) = 0 OR EXISTS (
                   SELECT 1
                     FROM substrate.entity_model_source model_entity
                    WHERE model_entity.entity_hash = e.hash
                      AND model_entity.model_source_id = ANY(p_model_source_ids)))
           AND (p_min_significance_mu IS NULL OR ranked.mu >= p_min_significance_mu)
      ) results
     ORDER BY
       CASE WHEN p_min_significance_mu IS NOT NULL THEN results.rank_mu END DESC NULLS LAST,
       CASE WHEN p_min_significance_mu IS NULL THEN results.entity_type_code END ASC,
       results.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_entities(TEXT[], INT[], FLOAT8, TEXT, INT) IS
    'Filter entities by classification, model source, optional arena significance threshold, and limit. Returns type code plus hash handles.';

-- ── sql/schema/functions/query_tensors_for_architecture.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_tensors_for_architecture(
    p_model_architecture_type_code TEXT,
    p_model_architecture_hash      BYTEA,
    p_model_source_ids             INT[] DEFAULT NULL,
    p_min_significance_mu          FLOAT8 DEFAULT NULL,
    p_context_type_code            TEXT DEFAULT NULL,
    p_limit                        INT DEFAULT NULL
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT results.entity_type_code, results.entity_hash
      FROM (
        SELECT DISTINCT target_type.code AS entity_type_code,
               target_member.entity_hash AS entity_hash,
               ranked.mu AS rank_mu
          FROM substrate.edge edge_row
          JOIN substrate.edge_type edge_type
            ON edge_type.id = edge_row.edge_type_id
           AND edge_type.code = 'has_tensor'
          JOIN substrate.edge_member source_member
            ON source_member.edge_type_id = edge_row.edge_type_id
           AND source_member.edge_hash = edge_row.hash
          JOIN substrate.edge_role source_role
            ON source_role.id = source_member.edge_role_id
           AND source_role.code = 'source'
          JOIN substrate.edge_member target_member
            ON target_member.edge_type_id = edge_row.edge_type_id
           AND target_member.edge_hash = edge_row.hash
          JOIN substrate.edge_role target_role
            ON target_role.id = target_member.edge_role_id
           AND target_role.code = 'target'
          JOIN substrate.entity_classification source_class
            ON source_class.entity_hash = source_member.entity_hash
          JOIN substrate.entity_type source_type
            ON source_type.id = source_class.entity_type_id
          JOIN substrate.entity_classification target_class
            ON target_class.entity_hash = target_member.entity_hash
          JOIN substrate.entity_type target_type
            ON target_type.id = target_class.entity_type_id
          LEFT JOIN LATERAL (
              SELECT max(significance.mu) AS mu
                FROM substrate.entity_significance significance
                LEFT JOIN substrate.significance_context context
                  ON context.id = significance.context_type_id
               WHERE significance.entity_hash = target_member.entity_hash
                 AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
          ) ranked ON TRUE
         WHERE source_type.code = p_model_architecture_type_code
           AND source_member.entity_hash = p_model_architecture_hash
           AND (COALESCE(array_length(p_model_source_ids, 1), 0) = 0 OR EXISTS (
                   SELECT 1
                     FROM substrate.entity_model_source model_entity
                    WHERE model_entity.entity_hash = target_member.entity_hash
                      AND model_entity.model_source_id = ANY(p_model_source_ids)))
           AND (p_min_significance_mu IS NULL OR ranked.mu >= p_min_significance_mu)
      ) results
     ORDER BY
       CASE WHEN p_min_significance_mu IS NOT NULL THEN results.rank_mu END DESC NULLS LAST,
       results.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_tensors_for_architecture(TEXT, BYTEA, INT[], FLOAT8, TEXT, INT) IS
    'Return tensor handles attached to a model_architecture by has_tensor, with optional model-source and significance filters.';

-- ── sql/schema/functions/query_tensors_for_model_source.sql ───────────────────────────────────────
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
           package_child.ordinal,
           occurrence_type.code AS occurrence_type_code,
           package_child.child_hash AS occurrence_hash,
           tensor_type.code AS tensor_type_code,
           tensor_child.child_hash AS tensor_hash
      FROM substrate.entity_model_source package_source
      JOIN substrate.entity_classification package_class
        ON package_class.entity_hash = package_source.entity_hash
      JOIN substrate.entity_type package_type
        ON package_type.id = package_class.entity_type_id
       AND package_type.code = 'model_package'
      JOIN LATERAL substrate.get_composition_children(package_class.entity_hash) package_child ON TRUE
      JOIN substrate.entity_classification occurrence_class
        ON occurrence_class.entity_hash = package_child.child_hash
      JOIN substrate.entity_type occurrence_type
        ON occurrence_type.id = occurrence_class.entity_type_id
       AND occurrence_type.code = 'model_package_tensor'
      JOIN LATERAL substrate.get_composition_children(package_child.child_hash) tensor_child ON TRUE
       AND tensor_child.ordinal = 1
      JOIN substrate.entity_classification tensor_class
        ON tensor_class.entity_hash = tensor_child.child_hash
      JOIN substrate.entity_type tensor_type
        ON tensor_type.id = tensor_class.entity_type_id
       AND tensor_type.code = 'tensor'
     WHERE package_source.model_source_id = p_model_source_id
     ORDER BY package_class.entity_hash ASC, package_child.ordinal ASC;
$f$;

COMMENT ON FUNCTION substrate.query_tensors_for_model_source(INT) IS
    'Return one model_source package tensor enumeration from composition physicality metadata, preserving package-scoped tensor order without conflating shared model_architecture entities.';

-- ── sql/schema/functions/query_fireflies_for_vocab.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_fireflies_for_vocab(
    p_bpe_token_hashes     BYTEA[],
    p_min_significance_mu  FLOAT8,
    p_context_type_code    TEXT,
    p_limit                INT DEFAULT NULL
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ranked.entity_type_code, ranked.entity_hash
      FROM (
        SELECT source_type.code AS entity_type_code,
               source_entity.hash AS entity_hash,
               max(significance.mu) AS rank_mu
          FROM substrate.entity source_entity
          JOIN substrate.entity_classification source_class
            ON source_class.entity_hash = source_entity.hash
          JOIN substrate.entity_type source_type
            ON source_type.id = source_class.entity_type_id
          JOIN substrate.physicality firefly
            ON firefly.entity_hash = source_entity.hash
          JOIN substrate.physicality_type firefly_type
            ON firefly_type.id = firefly.physicality_type_id
           AND firefly_type.code = 'firefly'
          JOIN substrate.entity_significance significance
            ON significance.entity_hash = source_entity.hash
          JOIN substrate.significance_context context
            ON context.id = significance.context_type_id
         WHERE source_entity.hash = ANY(p_bpe_token_hashes)
           AND source_type.code = 'word_form'
           AND significance.mu >= p_min_significance_mu
           AND context.code = p_context_type_code
         GROUP BY source_type.code, source_entity.hash
      ) ranked
     ORDER BY ranked.rank_mu DESC, ranked.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_fireflies_for_vocab(BYTEA[], FLOAT8, TEXT, INT) IS
    'Return word_form handles from the supplied vocabulary hash set that carry embedding_firefly physicality above an arena significance threshold.';

-- ── sql/schema/functions/query_ffn_neurons_by_hidden_dim.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_ffn_neurons_by_hidden_dim(
    p_hidden_size_hash  BYTEA,
    p_context_type_code TEXT,
    p_top_k             INT
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.entity_significance significance ON significance.entity_hash = target_member.entity_hash
      JOIN substrate.significance_context context ON context.id = significance.context_type_id
      JOIN substrate.edge size_edge
        ON size_edge.edge_type_id = (SELECT id FROM substrate.edge_type WHERE code = 'has_hidden_size')
      JOIN substrate.edge_member size_source
        ON size_source.edge_type_id = size_edge.edge_type_id
       AND size_source.edge_hash = size_edge.hash
      JOIN substrate.edge_role size_source_role
        ON size_source_role.id = size_source.edge_role_id
       AND size_source_role.code = 'source'
      JOIN substrate.edge_member size_target
        ON size_target.edge_type_id = size_edge.edge_type_id
       AND size_target.edge_hash = size_edge.hash
      JOIN substrate.edge_role size_target_role
        ON size_target_role.id = size_target.edge_role_id
       AND size_target_role.code = 'target'
     WHERE edge_type.code = 'has_ffn_neuron'
       AND target_type.code = 'ffn_neuron'
       AND context.code = p_context_type_code
       AND size_source.entity_hash = source_member.entity_hash
       AND size_target.entity_hash = p_hidden_size_hash
     ORDER BY significance.mu DESC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_ffn_neurons_by_hidden_dim(BYTEA, TEXT, INT) IS
    'Return top ffn_neuron handles for FFN tensors whose has_hidden_size target hash matches the supplied hidden-size hash.';

-- ── sql/schema/functions/query_attention_components.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_attention_components(
    p_archetype_hash    BYTEA DEFAULT NULL,
    p_context_type_code TEXT DEFAULT NULL,
    p_top_k             INT DEFAULT 25
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.entity_significance significance ON significance.entity_hash = target_member.entity_hash
      JOIN substrate.significance_context context ON context.id = significance.context_type_id
     WHERE edge_type.code = 'has_attention_component'
       AND target_type.code = 'attention_component'
       AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
       AND (p_archetype_hash IS NULL OR EXISTS (
             SELECT 1
               FROM substrate.edge archetype_edge
               JOIN substrate.edge_type archetype_edge_type
                 ON archetype_edge_type.id = archetype_edge.edge_type_id
                AND archetype_edge_type.code = 'encodes_archetype'
               JOIN substrate.edge_member archetype_source
                 ON archetype_source.edge_type_id = archetype_edge.edge_type_id
                AND archetype_source.edge_hash = archetype_edge.hash
               JOIN substrate.edge_role archetype_source_role
                 ON archetype_source_role.id = archetype_source.edge_role_id
                AND archetype_source_role.code = 'source'
               JOIN substrate.edge_member archetype_target
                 ON archetype_target.edge_type_id = archetype_edge.edge_type_id
                AND archetype_target.edge_hash = archetype_edge.hash
               JOIN substrate.edge_role archetype_target_role
                 ON archetype_target_role.id = archetype_target.edge_role_id
                AND archetype_target_role.code = 'target'
              WHERE archetype_source.entity_hash = source_member.entity_hash
                AND archetype_target.entity_hash = p_archetype_hash))
     ORDER BY significance.mu DESC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_attention_components(BYTEA, TEXT, INT) IS
    'Return top attention_component handles, optionally requiring the source attention tensor to encode a supplied archetype hash.';

-- ── sql/schema/functions/query_singular_directions_for_role.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_singular_directions_for_role(
    p_tensor_role_code TEXT,
    p_top_k            INT
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.tensor_tensor_role tensor_role_link ON tensor_role_link.entity_hash = source_member.entity_hash
      JOIN substrate.tensor_role tensor_role ON tensor_role.id = tensor_role_link.tensor_role_id
     WHERE edge_type.code = 'has_rank_component'
       AND tensor_role.code = p_tensor_role_code
     ORDER BY edge_row.hash ASC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_singular_directions_for_role(TEXT, INT) IS
    'Return svd rank-component handles for tensors with the supplied tensor_role code.';

-- ── sql/schema/functions/preview_target_arch.sql ───────────────────────────────────────
-- substrate.preview_target_arch(p_target_spec jsonb, p_recipe jsonb)
--
-- For a proposed target architecture spec + recipe, return per-tensor-role
-- counts of substrate edges that qualify under the recipe. Drives the
-- future model-config UI's "Preview" panel: estimated output size, sparsity
-- ratio, vocab coverage, expert clustering preview. NO files written.
--
-- p_target_spec example:
--   {"hidden_size":4096, "num_layers":32, "num_attention_heads":32,
--    "vocab_size":32768, "moe_experts":null, "ffn_intermediate":11008}
--
-- p_recipe example (Mode 2 origination, curated-only, semantic-relevance):
--   {"provenance_filter":"provenance.curator_class IN ('authoritative_standard','academic_curated')",
--    "arena_codes":["semantic_relevance","corroboration_strength"],
--    "significance_floor":0.7}
--
-- Returns one row per architectural-tensor role; the future UI aggregates
-- across roles to produce the headline estimate.
DROP FUNCTION IF EXISTS substrate.preview_target_arch(jsonb, jsonb);
CREATE OR REPLACE FUNCTION substrate.preview_target_arch(
    p_target_spec jsonb,
    p_recipe      jsonb
)
RETURNS TABLE (
    tensor_role               text,
    qualifying_edges          bigint,
    estimated_nonzero_count   bigint,
    sparsity_ratio            double precision,
    estimated_bytes           bigint
)
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    v_hidden          int := COALESCE((p_target_spec->>'hidden_size')::int, 0);
    v_layers          int := COALESCE((p_target_spec->>'num_layers')::int, 0);
    v_heads           int := COALESCE((p_target_spec->>'num_attention_heads')::int, 0);
    v_vocab           int := COALESCE((p_target_spec->>'vocab_size')::int, 0);
    v_ffn_intermed    int := COALESCE((p_target_spec->>'ffn_intermediate')::int, v_hidden * 4);
    v_floor           double precision := COALESCE((p_recipe->>'significance_floor')::double precision, 0.5);
    v_arena_codes     text[];
    v_arena_ids       int[];
BEGIN
    -- Resolve arena codes → ids (open vocabulary; missing codes silently
    -- excluded so a recipe referencing a not-yet-created arena returns 0
    -- qualifying edges rather than error).
    IF p_recipe ? 'arena_codes' THEN
        SELECT array_agg(value)::text[] INTO v_arena_codes
          FROM jsonb_array_elements_text(p_recipe->'arena_codes');
    ELSE
        v_arena_codes := ARRAY['semantic_relevance', 'corroboration_strength'];
    END IF;

    SELECT array_agg(id) INTO v_arena_ids
      FROM substrate.significance_context
     WHERE code = ANY(v_arena_codes);

    -- For each architectural-tensor role, count substrate edges that
    -- qualify under the recipe (above significance floor in any of the
    -- requested arenas). The estimate count = qualifying_edges (= tensor
    -- count needed if we project one row per qualifying source unit);
    -- estimated_bytes scales by target dim and dtype.
    RETURN QUERY
    WITH role_buckets AS (
        SELECT 'attention_head_in_layer'::text AS role,
               v_layers::bigint * v_heads::bigint AS slot_count,
               (v_hidden::bigint * (v_hidden / GREATEST(v_heads, 1))::bigint) AS bytes_per_slot
        UNION ALL SELECT 'ffn_up_in_layer'::text,   v_layers::bigint, v_hidden::bigint * v_ffn_intermed::bigint
        UNION ALL SELECT 'ffn_gate_in_layer'::text, v_layers::bigint, v_hidden::bigint * v_ffn_intermed::bigint
        UNION ALL SELECT 'ffn_down_in_layer'::text, v_layers::bigint, v_ffn_intermed::bigint * v_hidden::bigint
        UNION ALL SELECT 'vocab_embedding'::text,   1::bigint,         v_vocab::bigint * v_hidden::bigint
        UNION ALL SELECT 'vocab_unembedding'::text, 1::bigint,         v_hidden::bigint * v_vocab::bigint
        UNION ALL SELECT 'layer_norm_for_layer_position'::text,
                                                    v_layers::bigint * 2::bigint, v_hidden::bigint
    ),
    edge_counts AS (
        SELECT et.code AS role,
               count(DISTINCT (es.edge_type_id, es.edge_hash)) FILTER (WHERE es.mu > v_floor) AS qualifying
          FROM substrate.edge_significance es
          JOIN substrate.edge_type et ON et.id = es.edge_type_id
         WHERE et.code IN (
                'attention_head_in_layer',
                'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
                'vocab_embedding','vocab_unembedding',
                'layer_norm_for_layer_position'
           )
           AND (v_arena_ids IS NULL OR es.context_type_id = ANY(v_arena_ids))
         GROUP BY et.code
    )
    SELECT rb.role,
           COALESCE(ec.qualifying, 0)::bigint                           AS qualifying_edges,
           LEAST(COALESCE(ec.qualifying, 0), rb.slot_count)::bigint     AS estimated_nonzero_count,
           CASE
              WHEN rb.slot_count = 0 THEN 0.0
              ELSE 1.0 - (LEAST(COALESCE(ec.qualifying, 0), rb.slot_count)::double precision
                          / rb.slot_count::double precision)
           END                                                          AS sparsity_ratio,
           (rb.slot_count * rb.bytes_per_slot * 2)::bigint              AS estimated_bytes  -- BF16 = 2 bytes/element
      FROM role_buckets rb
      LEFT JOIN edge_counts ec ON ec.role = rb.role
     ORDER BY rb.role;
END $$;

COMMENT ON FUNCTION substrate.preview_target_arch(jsonb, jsonb) IS
    'Per-tensor-role preview for a proposed target architecture + recipe. Returns qualifying edge counts, estimated nonzero counts, sparsity ratio, byte estimates. NO files written. Drives the future model-config UI''s preview panel.';

-- ── sql/schema/functions/refinement_summary.sql ───────────────────────────────────────
-- substrate.refinement_summary(p_model_arch_hash bytea, p_arena_code text DEFAULT 'corroboration_strength')
--
-- Per-tensor refinement preview for an ingested model. For each tensor with
-- an architectural edge, reports:
--   source_only_mu  — edge significance using only the source model's
--                     sub-provenance contribution (μ at provenance-default).
--   consensus_mu    — edge significance with cross-source corroboration in
--                     the requested arena (μ that would be used if
--                     RefinementPolicy = Consensus).
--   delta_mu        — consensus_mu - source_only_mu (positive = corroborated,
--                     pushed up; negative = contradicted, pushed down).
--   above_threshold — whether the consensus μ clears a typical 0.7 floor.
--
-- The recomposer can be queried with this function to preview which
-- positions will be reinforced vs which will be zeroed out at recompose.
-- The future UI plots delta_mu as a histogram so the user can see how
-- much the substrate's accumulated cross-source state will reshape this
-- model on refined export.
DROP FUNCTION IF EXISTS substrate.refinement_summary(bytea, text);
CREATE OR REPLACE FUNCTION substrate.refinement_summary(
    p_model_arch_hash bytea,
    p_arena_code      text DEFAULT 'corroboration_strength'
)
RETURNS TABLE (
    tensor_hash          bytea,
    edge_type_code       text,
    source_only_mu       double precision,
    consensus_mu         double precision,
    delta_mu             double precision,
    above_threshold      boolean
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH arena AS (
        SELECT id FROM substrate.significance_context WHERE code = p_arena_code
    ),
    model_tensors AS (
        SELECT em_src.entity_hash AS tensor_hash, et.code AS edge_type_code,
               em_src.edge_type_id, em_src.edge_hash
          FROM substrate.edge_member em_tgt
          JOIN substrate.edge_type et      ON et.id = em_tgt.edge_type_id
          JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
          JOIN substrate.edge_member em_src
            ON em_src.edge_type_id = em_tgt.edge_type_id
           AND em_src.edge_hash    = em_tgt.edge_hash
          JOIN substrate.edge_role er_src ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
         WHERE em_tgt.entity_hash = p_model_arch_hash
           AND et.category = 'model_derived'
    )
    SELECT mt.tensor_hash,
           mt.edge_type_code,
           p.initial_mu * et.semantic_weight * p.derivation_decay AS source_only_mu,
           es.mu AS consensus_mu,
           es.mu - (p.initial_mu * et.semantic_weight * p.derivation_decay) AS delta_mu,
           es.mu > 0.7 * p.initial_mu AS above_threshold
      FROM model_tensors mt
      JOIN substrate.edge e         ON e.edge_type_id = mt.edge_type_id AND e.hash = mt.edge_hash
      JOIN substrate.edge_type et   ON et.id = e.edge_type_id
      JOIN substrate.provenance p   ON p.id = e.provenance_id
      JOIN arena                    ON TRUE
      JOIN substrate.edge_significance es
        ON es.edge_type_id = e.edge_type_id
       AND es.edge_hash    = e.hash
       AND es.context_type_id = arena.id
     ORDER BY delta_mu DESC NULLS LAST;
$$;

COMMENT ON FUNCTION substrate.refinement_summary(bytea, text) IS
    'Per-tensor refinement preview: source-only μ vs cross-source-consensus μ vs threshold. Identifies positions that will be reinforced or zeroed at recompose. The future UI plots this as a histogram.';

-- ── sql/schema/functions/refinement_summary_top.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.refinement_summary_top(
    p_model_arch_hash BYTEA,
    p_arena_code      TEXT DEFAULT 'corroboration_strength',
    p_limit           INT DEFAULT 25
)
RETURNS TABLE (
    tensor_hash     BYTEA,
    edge_type_code  TEXT,
    source_only_mu  FLOAT8,
    consensus_mu    FLOAT8,
    delta_mu        FLOAT8,
    above_threshold BOOLEAN
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT summary.tensor_hash,
           summary.edge_type_code,
           summary.source_only_mu,
           summary.consensus_mu,
           summary.delta_mu,
           summary.above_threshold
      FROM substrate.refinement_summary(p_model_arch_hash, p_arena_code) summary
     ORDER BY summary.delta_mu DESC NULLS LAST
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.refinement_summary_top(BYTEA, TEXT, INT) IS
    'Top-N refinement summary rows ordered by consensus delta for CLI/UI quote surfaces.';

-- ── sql/schema/functions/tensor_provenance_chain.sql ───────────────────────────────────────
-- substrate.tensor_provenance_chain(p_tensor_hash bytea)
--
-- Full provenance walk for a single tensor: which model_architecture(s)
-- contain it, which provenances contributed evidence, with significance per
-- arena. The recomposer's __metadata__.hartonomous_provenance_chain is built
-- by joining this output across every output tensor.
DROP FUNCTION IF EXISTS substrate.tensor_provenance_chain(bytea);
CREATE OR REPLACE FUNCTION substrate.tensor_provenance_chain(p_tensor_hash bytea)
RETURNS TABLE (
    model_arch_hash      bytea,
    edge_type_code       text,
    provenance_code      text,
    arena_code           text,
    mu                   double precision,
    sigma                double precision,
    games                int
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT em_tgt.entity_hash      AS model_arch_hash,
           et.code                 AS edge_type_code,
           prov.code               AS provenance_code,
           sc.code                 AS arena_code,
           es.mu, es.sigma, es.games
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.category = 'model_derived'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge e
        ON e.edge_type_id = em_src.edge_type_id
       AND e.hash         = em_src.edge_hash
      JOIN substrate.provenance prov   ON prov.id = e.provenance_id
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = e.edge_type_id
       AND es.edge_hash    = e.hash
      LEFT JOIN substrate.significance_context sc
        ON sc.id = es.context_type_id
     WHERE em_src.entity_hash = p_tensor_hash
     ORDER BY arena_code NULLS LAST, mu DESC NULLS LAST;
$$;

COMMENT ON FUNCTION substrate.tensor_provenance_chain(bytea) IS
    'Full provenance walk for a tensor: model_architecture(s) it''s in, provenances that contributed, arena μ/σ/games. Used by recomposer __metadata__ audit chain emission.';

-- ── sql/schema/functions/recompose_audit_walk.sql ───────────────────────────────────────
-- substrate.recompose_audit_walk(p_provenance_chain jsonb)
--
-- Walks a recomposed model's __metadata__.hartonomous_provenance_chain
-- back through the substrate to verify every claimed (tensor, source,
-- arena, μ) tuple actually exists in current substrate state. Returns
-- one row per chain entry with verified=true/false and a divergence
-- detail string. The D-recompose-audit-chain gate runs this for every
-- exported tensor.
--
-- p_provenance_chain example (one entry per output tensor):
--   [
--     {"tensor_hash":"<hex>","provenance":"huggingface_model:llama-4-maverick","arena":"corroboration_strength","mu":78321.5},
--     ...
--   ]
--
-- Implementation: one flat SELECT, no CTE, no plpgsql.
--   * jsonb_array_elements WITH ORDINALITY (native built-in) expands the
--     chain to rows preserving original order.
--   * jsonb_to_record (native C) extracts named fields per row.
--   * LATERAL JOIN with LIMIT 1 (executor-level, native) does one indexed
--     lookup per chain row against substrate.edge_significance.
DROP FUNCTION IF EXISTS substrate.recompose_audit_walk(jsonb);
CREATE OR REPLACE FUNCTION substrate.recompose_audit_walk(p_provenance_chain jsonb)
RETURNS TABLE (
    chain_index int,
    tensor_hash bytea,
    claimed_mu  double precision,
    actual_mu   double precision,
    verified    boolean,
    detail      text
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        arr.ordinality::int                                                AS chain_index,
        decode(j.tensor_hash, 'hex')                                       AS tensor_hash,
        j.mu                                                                AS claimed_mu,
        actual.mu                                                           AS actual_mu,
        actual.mu IS NOT NULL
            AND abs(COALESCE(actual.mu, 0) - COALESCE(j.mu, 0)) < 1.0       AS verified,
        CASE WHEN actual.mu IS NULL THEN 'no edge in current substrate'
             WHEN abs(actual.mu - j.mu) >= 1.0 THEN
                 format('mu drift: claimed=%s actual=%s', j.mu, actual.mu)
             ELSE 'ok' END                                                  AS detail
      FROM jsonb_array_elements(p_provenance_chain) WITH ORDINALITY
        AS arr(elem, ordinality)
      CROSS JOIN LATERAL jsonb_to_record(arr.elem)
        AS j(tensor_hash text, provenance text, arena text, mu double precision)
      LEFT JOIN LATERAL (
          SELECT es.mu
            FROM substrate.edge_member em
            JOIN substrate.edge e
              ON e.edge_type_id = em.edge_type_id
             AND e.hash         = em.edge_hash
            JOIN substrate.provenance prov
              ON prov.id   = e.provenance_id
             AND prov.code = j.provenance
            JOIN substrate.edge_significance es
              ON es.edge_type_id = e.edge_type_id
             AND es.edge_hash    = e.hash
            JOIN substrate.significance_context sc
              ON sc.id   = es.context_type_id
             AND sc.code = j.arena
           WHERE em.entity_hash = decode(j.tensor_hash, 'hex')
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) actual ON TRUE
     ORDER BY arr.ordinality;
$$;

COMMENT ON FUNCTION substrate.recompose_audit_walk(jsonb) IS
    'Verify every (tensor, provenance, arena, μ) entry in a recomposed model''s __metadata__ provenance chain. Flat SELECT — jsonb_array_elements WITH ORDINALITY + jsonb_to_record (native C) + LATERAL LIMIT 1 (native executor). No CTE, no plpgsql.';

-- ── sql/schema/functions/significance_context_ids.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.significance_context_ids()
RETURNS TABLE (id INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.id
      FROM substrate.significance_context sc
     ORDER BY sc.id;
$f$;

COMMENT ON FUNCTION substrate.significance_context_ids() IS
    'Return all significance_context ids in deterministic order. The arena vocabulary is open-ended.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Monitor write functions

-- ── sql/schema/functions/monitor_create_session.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.create_session(
    p_label TEXT,
    p_notes TEXT DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
AS $$
DECLARE
    v_id UUID := gen_random_uuid();
BEGIN
    INSERT INTO monitor.session (id, user_label, started_at, notes)
    VALUES (v_id, p_label, NOW(), p_notes);
    RETURN v_id;
END $$;

COMMENT ON FUNCTION monitor.create_session(TEXT, TEXT) IS
    'Open a new monitor.session row and return its UUID.';

-- ── sql/schema/functions/monitor_close_session.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.close_session()
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_rows INT;
BEGIN
    UPDATE monitor.session
       SET ended_at = NOW()
     WHERE ended_at IS NULL
       AND started_at = (SELECT MAX(started_at) FROM monitor.session WHERE ended_at IS NULL);

  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows > 0;
END $$;

COMMENT ON FUNCTION monitor.close_session() IS
  'Close the most recent open session and return true when a row was closed.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 14: procedures ─────────────────────────────────────────────
-- Substrate write procedures

-- ── sql/schema/procedures/write_codepoint_properties.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE substrate.write_codepoint_properties(p_rows JSONB)
LANGUAGE plpgsql
AS $$
BEGIN
    IF p_rows IS NULL OR jsonb_typeof(p_rows) <> 'array' THEN
        RAISE EXCEPTION 'Codepoint property payload must be a JSON array';
    END IF;

    INSERT INTO substrate.codepoint_property (
        entity_hash,
        codepoint_value,
        general_category_id,
        script_id,
        block_id,
        gcb_id,
        wb_id,
        sb_id,
        lb_id,
        is_extended_pictographic,
        ccc,
        decomposition_type,
        decomposition_mapping,
        simple_case_fold,
        full_case_fold
    )
    SELECT
        decode(src.entity_hash_hex, 'hex')::substrate.hash_value,
        src.codepoint_value,
        src.general_category_id,
        src.script_id,
        src.block_id,
        src.gcb_id,
        src.wb_id,
        src.sb_id,
        src.lb_id,
        src.is_extended_pictographic,
        src.ccc,
        src.decomposition_type,
        src.decomposition_mapping,
        src.simple_case_fold,
        src.full_case_fold
      FROM jsonb_to_recordset(p_rows) AS src(
        entity_hash_hex TEXT,
        codepoint_value INT,
        general_category_id INT,
        script_id INT,
        block_id INT,
        gcb_id INT,
        wb_id INT,
        sb_id INT,
        lb_id INT,
        is_extended_pictographic BOOLEAN,
        ccc SMALLINT,
        decomposition_type VARCHAR(16),
        decomposition_mapping INT[],
        simple_case_fold INT,
        full_case_fold INT[]
      )
    ON CONFLICT (entity_hash) DO NOTHING;
END $$;

COMMENT ON PROCEDURE substrate.write_codepoint_properties(JSONB) IS
    'Bulk insert codepoint_property rows from a JSONB recordset payload, preserving idempotent ON CONFLICT behavior.';

-- ── sql/schema/procedures/write_glicko_junction.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE substrate.write_glicko_junction(
    p_table_name            TEXT,
    p_ref_column            TEXT,
    p_entity_hashes         BYTEA[],
    p_ref_ids               INT[],
    p_mus                   DOUBLE PRECISION[],
    p_sigmas                DOUBLE PRECISION[],
    p_attestation_type_code TEXT DEFAULT 'lexical_curated_relation'
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT := lower(CASE WHEN left(p_table_name, 10) = 'substrate.' THEN substring(p_table_name FROM 11) ELSE p_table_name END);
    v_ref_column TEXT := lower(p_ref_column);
    v_attestation_type_id INT;
BEGIN
    IF p_entity_hashes IS NULL OR p_ref_ids IS NULL OR p_mus IS NULL OR p_sigmas IS NULL THEN
        RAISE EXCEPTION 'Junction arrays cannot be null';
    END IF;

    IF cardinality(p_entity_hashes) <> cardinality(p_ref_ids)
        OR cardinality(p_entity_hashes) <> cardinality(p_mus)
        OR cardinality(p_entity_hashes) <> cardinality(p_sigmas) THEN
        RAISE EXCEPTION 'Junction array lengths must match: hashes %, refs %, mus %, sigmas %',
            cardinality(p_entity_hashes), cardinality(p_ref_ids), cardinality(p_mus), cardinality(p_sigmas);
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    IF v_table_name = 'entity_pos' AND v_ref_column = 'pos_id' THEN
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, attestation_type_id, mu, sigma)
        SELECT src.entity_hash, src.ref_id, v_attestation_type_id, src.mu, src.sigma
          FROM unnest(p_entity_hashes, p_ref_ids, p_mus, p_sigmas) AS src(entity_hash, ref_id, mu, sigma)
        ON CONFLICT (entity_hash, pos_id, attestation_type_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'pattern_deprel' AND v_ref_column = 'deprel_id' THEN
        INSERT INTO substrate.pattern_deprel (entity_hash, deprel_id, attestation_type_id, mu, sigma)
        SELECT src.entity_hash, src.ref_id, v_attestation_type_id, src.mu, src.sigma
          FROM unnest(p_entity_hashes, p_ref_ids, p_mus, p_sigmas) AS src(entity_hash, ref_id, mu, sigma)
        ON CONFLICT (entity_hash, deprel_id, attestation_type_id) DO NOTHING;
        RETURN;
    END IF;

    RAISE EXCEPTION 'Unsupported Glicko junction target %.%', v_table_name, v_ref_column;
END $$;

COMMENT ON PROCEDURE substrate.write_glicko_junction(TEXT, TEXT, BYTEA[], INT[], DOUBLE PRECISION[], DOUBLE PRECISION[], TEXT) IS
    'Bulk insert allowlisted Glicko-bearing junction rows. Routing is SQL-side and explicit. attestation_type defaults to lexical_curated_relation (POS/deprel curated lexicons); model-derived junction priors should pass model_attention_pattern or similar.';

-- ── sql/schema/procedures/write_plain_junction.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE substrate.write_plain_junction(
    p_table_name TEXT,
    p_ref_column TEXT,
    p_entity_hashes BYTEA[],
    p_ref_ids INT[]
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT := lower(CASE WHEN left(p_table_name, 10) = 'substrate.' THEN substring(p_table_name FROM 11) ELSE p_table_name END);
    v_ref_column TEXT := lower(p_ref_column);
BEGIN
    IF p_entity_hashes IS NULL OR p_ref_ids IS NULL THEN
        RAISE EXCEPTION 'Junction arrays cannot be null';
    END IF;

    IF cardinality(p_entity_hashes) <> cardinality(p_ref_ids) THEN
        RAISE EXCEPTION 'Junction array lengths must match: hashes %, refs %',
            cardinality(p_entity_hashes), cardinality(p_ref_ids);
    END IF;

    IF v_table_name = 'entity_language' AND v_ref_column = 'language_id' THEN
        INSERT INTO substrate.entity_language (entity_hash, language_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, language_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'entity_morph_feature' AND v_ref_column = 'morph_feature_id' THEN
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, morph_feature_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'entity_lexname' AND v_ref_column = 'lexname_id' THEN
        INSERT INTO substrate.entity_lexname (entity_hash, lexname_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, lexname_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'model_architecture_class' AND v_ref_column = 'architecture_class_id' THEN
        INSERT INTO substrate.model_architecture_class (entity_hash, architecture_class_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, architecture_class_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'tensor_tensor_role' AND v_ref_column = 'tensor_role_id' THEN
        INSERT INTO substrate.tensor_tensor_role (entity_hash, tensor_role_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, tensor_role_id) DO NOTHING;
        RETURN;
    END IF;

    RAISE EXCEPTION 'Unsupported plain junction target %.%', v_table_name, v_ref_column;
END $$;

COMMENT ON PROCEDURE substrate.write_plain_junction(TEXT, TEXT, BYTEA[], INT[]) IS
    'Bulk insert allowlisted plain junction rows. Routing is SQL-side and explicit.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Monitor write procedures

-- ── sql/schema/procedures/monitor_archive_session.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.archive_session(p_session_id UUID)
LANGUAGE plpgsql
AS $$
BEGIN
    -- Archival is currently a no-op; the session row stays in monitor.session
    -- with ended_at populated by close_session. This procedure exists so the
    -- C# CLI's session management surface has somewhere to call.
    UPDATE monitor.session SET ended_at = COALESCE(ended_at, NOW())
     WHERE id = p_session_id;
END $$;
COMMENT ON PROCEDURE monitor.archive_session(UUID) IS
    'Mark a session as ended (idempotent). Future revisions may move rows to a cold-storage table.';

-- ── sql/schema/procedures/monitor_update_phase_status.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.update_phase_status(
    p_phase_code    TEXT,
    p_status        TEXT,
    p_error_message TEXT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO monitor.phase_status (phase_code, status, started_at, completed_at, error_message)
    VALUES (
        p_phase_code,
        p_status,
        CASE WHEN p_status IN ('started','running') THEN NOW() ELSE NULL END,
        CASE WHEN p_status IN ('completed','failed','skipped') THEN NOW() ELSE NULL END,
        p_error_message
    )
    ON CONFLICT (phase_code) DO UPDATE
        SET status        = EXCLUDED.status,
            started_at    = CASE
                                WHEN EXCLUDED.status IN ('started','running') THEN EXCLUDED.started_at
                                ELSE monitor.phase_status.started_at
                            END,
            completed_at  = CASE
                                WHEN EXCLUDED.status IN ('started','running') THEN NULL
                                ELSE EXCLUDED.completed_at
                            END,
            error_message = CASE
                                WHEN EXCLUDED.status IN ('started','running','completed') THEN NULL
                                ELSE EXCLUDED.error_message
                            END;
END $$;
COMMENT ON PROCEDURE monitor.update_phase_status(TEXT, TEXT, TEXT) IS
    'Upsert the last-known status of a phase. Status: running, completed, failed, skipped.';

-- ── sql/schema/procedures/monitor_report_progress.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.report_progress(
    p_provenance_code TEXT,
    p_pass_name       TEXT,
    p_batch_number    INT,
    p_entities_total  BIGINT,
    p_edges_total     BIGINT,
    p_current_file    TEXT DEFAULT NULL,
    p_p1              TEXT DEFAULT NULL,  -- reserved
    p_p2              TEXT DEFAULT NULL,
    p_p3              TEXT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO monitor.ingestion_progress
        (provenance_code, pass_name, batch_number, entities_total, edges_total, current_file)
    VALUES
        (p_provenance_code, p_pass_name, p_batch_number, p_entities_total, p_edges_total, p_current_file);
END $$;
COMMENT ON PROCEDURE monitor.report_progress(TEXT, TEXT, INT, BIGINT, BIGINT, TEXT, TEXT, TEXT, TEXT) IS
    'Append a per-batch ingestion-progress row.';

-- ── sql/schema/procedures/monitor_snapshot_health.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.snapshot_health()
LANGUAGE plpgsql
AS $$
DECLARE
    v_entities BIGINT;
    v_edges    BIGINT;
BEGIN
    SELECT count(*) INTO v_entities FROM substrate.entity;
    SELECT count(*) INTO v_edges    FROM substrate.edge;

    INSERT INTO monitor.substrate_health (metric_code, metric_value, recorded_at)
    VALUES ('entity_count', v_entities, NOW()),
           ('edge_count',   v_edges,    NOW());
END $$;
COMMENT ON PROCEDURE monitor.snapshot_health() IS
    'Capture coarse substrate-state metrics (entity count, edge count) into monitor.substrate_health.';

-- ── sql/schema/procedures/monitor_reset_phase_checkpoint.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.reset_phase_checkpoint(p_phase_code TEXT)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM monitor.phase_status WHERE phase_code = p_phase_code;
    TRUNCATE TABLE substrate.model_pass_checkpoint;
END $$;

COMMENT ON PROCEDURE monitor.reset_phase_checkpoint(TEXT) IS
    'Reset a phase status row and clear model pass checkpoints for CLI phase reruns.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 15: views ──────────────────────────────────────────────────

-- ── sql/schema/views/substrate_dashboard.sql ───────────────────────────────────────
-- High-level "is the substrate healthy" rollup for the CLI's status command.
CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    (SELECT count(*) FROM substrate.entity)              AS total_entities,
    (SELECT count(*) FROM substrate.edge)                AS total_edges,
    (SELECT count(*) FROM substrate.physicality)         AS total_physicalities,
    ((SELECT count(*) FROM substrate.entity_significance)
     + (SELECT count(*) FROM substrate.edge_significance)) AS total_significance_records,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'completed') AS phases_completed,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'failed')    AS phases_failed,
    (SELECT max(recorded_at) FROM monitor.substrate_health)                AS last_health_snapshot;
COMMENT ON VIEW monitor.substrate_dashboard IS
    'Single-row rollup of substrate state for the CLI''s status command.';

-- ── sql/schema/views/entity_type_counts.sql ───────────────────────────────────────
-- Classification-aware entity and edge counts by structural entity type.
CREATE OR REPLACE VIEW monitor.entity_type_counts AS
SELECT
    et.code AS entity_type,
    count(DISTINCT ec.entity_hash)::BIGINT AS entity_count,
    (count(DISTINCT (em.edge_type_id, em.edge_hash))
        FILTER (WHERE em.edge_hash IS NOT NULL))::BIGINT AS edge_count
FROM substrate.entity_classification ec
JOIN substrate.entity_type et ON et.id = ec.entity_type_id
LEFT JOIN substrate.edge_member em ON em.entity_hash = ec.entity_hash
GROUP BY et.code;

COMMENT ON VIEW monitor.entity_type_counts IS
    'Counts classified entities and distinct incident edges per structural entity type using substrate.entity_classification.';

-- ── sql/schema/views/session_summaries.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.session_summaries AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s;

COMMENT ON VIEW monitor.session_summaries IS
    'List projection for monitor sessions with comparison-event counts.';

-- ── sql/schema/views/session_details.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.session_details AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.notes,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s;

COMMENT ON VIEW monitor.session_details IS
    'Detail projection for monitor sessions with notes and comparison-event counts.';

-- ── sql/schema/views/active_sessions.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.active_sessions AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s
WHERE s.ended_at IS NULL
ORDER BY s.started_at DESC;

COMMENT ON VIEW monitor.active_sessions IS
    'Open monitor sessions with comparison-event counts.';

-- ── sql/schema/views/phase_status_overview.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.phase_status_overview AS
SELECT
    ps.phase_code,
    ps.status,
    COALESCE(sum(ip.entities_total), 0)::BIGINT AS entity_count,
    COALESCE(sum(ip.edges_total), 0)::BIGINT AS edge_count,
    EXTRACT(EPOCH FROM (ps.completed_at - ps.started_at))::INT AS duration_seconds
FROM monitor.phase_status ps
LEFT JOIN monitor.ingestion_progress ip ON ip.pass_name = ps.phase_code
GROUP BY ps.phase_code, ps.status, ps.started_at, ps.completed_at
ORDER BY ps.started_at NULLS LAST;

COMMENT ON VIEW monitor.phase_status_overview IS
    'Phase status rows enriched with ingestion-progress totals and duration for status surfaces.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Monitor read functions that wrap the views above.

-- ── sql/schema/functions/monitor_list_sessions.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.list_sessions()
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), started_at TIMESTAMPTZ, ended_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.session_id, s.user_label, s.started_at, s.ended_at, s.comparison_count
      FROM monitor.session_summaries s
     ORDER BY s.started_at DESC;
$f$;

COMMENT ON FUNCTION monitor.list_sessions() IS
    'Return session summary rows for CLI/API session listings.';

-- ── sql/schema/functions/monitor_session_detail.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.session_detail(p_session_id UUID)
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), notes TEXT, started_at TIMESTAMPTZ, ended_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT d.session_id, d.user_label, d.notes, d.started_at, d.ended_at, d.comparison_count
      FROM monitor.session_details d
     WHERE d.session_id = p_session_id;
$f$;

COMMENT ON FUNCTION monitor.session_detail(UUID) IS
    'Return one monitor session detail row by UUID.';

-- ── sql/schema/functions/monitor_phase_status_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.phase_status_map()
RETURNS TABLE (phase_code VARCHAR(64), status VARCHAR(32))
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ps.phase_code, ps.status
      FROM monitor.phase_status ps;
$f$;

COMMENT ON FUNCTION monitor.phase_status_map() IS
    'Return phase_code/status pairs for phase orchestration resume checks.';

-- ── sql/schema/functions/monitor_phase_status_overview_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.phase_status_overview_rows()
RETURNS TABLE (phase_code VARCHAR(64), status VARCHAR(32), entity_count BIGINT, edge_count BIGINT, duration_seconds INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT p.phase_code, p.status, p.entity_count, p.edge_count, p.duration_seconds
      FROM monitor.phase_status_overview p;
$f$;

COMMENT ON FUNCTION monitor.phase_status_overview_rows() IS
    'Return monitor.phase_status_overview rows for status surfaces.';

-- ── sql/schema/functions/monitor_substrate_totals.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.substrate_totals()
RETURNS TABLE (total_entities BIGINT, total_edges BIGINT, total_physicalities BIGINT, total_significance_records BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT d.total_entities, d.total_edges, d.total_physicalities, d.total_significance_records
      FROM monitor.substrate_dashboard d;
$f$;

COMMENT ON FUNCTION monitor.substrate_totals() IS
    'Return the single-row substrate dashboard totals used by status surfaces.';

-- ── sql/schema/functions/monitor_active_session_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.active_session_rows()
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), started_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT a.session_id, a.user_label, a.started_at, a.comparison_count
      FROM monitor.active_sessions a;
$f$;

COMMENT ON FUNCTION monitor.active_session_rows() IS
    'Return currently open monitor sessions.';

-- ── sql/schema/functions/monitor_entity_type_count_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.entity_type_count_rows()
RETURNS TABLE (entity_type TEXT, entity_count BIGINT, edge_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.entity_type, c.entity_count, c.edge_count
      FROM monitor.entity_type_counts c
     ORDER BY c.entity_count DESC, c.entity_type;
$f$;

COMMENT ON FUNCTION monitor.entity_type_count_rows() IS
    'Return classification-aware entity and incident-edge counts by structural entity type.';

-- ── sql/schema/functions/monitor_ingestion_status_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.ingestion_status_rows()
RETURNS TABLE (
    decomposer_code VARCHAR(64),
    entities_created BIGINT,
    edges_created BIGINT,
    entities_per_second DOUBLE PRECISION,
    is_stuck BOOLEAN,
    last_report TIMESTAMPTZ
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        ip.provenance_code AS decomposer_code,
        COALESCE(max(ip.entities_total), 0)::BIGINT AS entities_created,
        COALESCE(max(ip.edges_total), 0)::BIGINT AS edges_created,
        COALESCE(max(ip.entities_total), 0)::DOUBLE PRECISION
            / GREATEST(EXTRACT(EPOCH FROM (max(ip.recorded_at) - min(ip.recorded_at))), 1.0) AS entities_per_second,
        max(ip.recorded_at) < now() - interval '5 minutes' AS is_stuck,
        max(ip.recorded_at) AS last_report
      FROM monitor.ingestion_progress ip
     GROUP BY ip.provenance_code;
$f$;

COMMENT ON FUNCTION monitor.ingestion_status_rows() IS
    'Return current ingestion status rows derived from monitor.ingestion_progress.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (No Phase 16 hartonomous CREATE EXTENSION. The hartonomous-pg/sql/
--  hartonomous--1.0.sql.in template — containing all C-binding type
--  declarations + substrate.cp_*, ucd_*, text_decompose etc. — is
--  spliced into the assembled extension SQL at build time, BEFORE the
--  Phase 13 functions block. See scripts/build/concat_extension_sql.py.)
