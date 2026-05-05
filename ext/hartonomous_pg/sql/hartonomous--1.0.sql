/* GENERATED — do not edit by hand. Source: sql/schema/**/*.sql + ext/hartonomous_pg/sql/hartonomous--1.0.sql.in.
   Build via: pwsh scripts/build/ExtensionSql.ps1
 * Concatenated by: scripts/build/concat_extension_sql.py
 * Order: sql/schema/bootstrap.sql @include directives.
 *
 * Prerequisite extensions (postgis, btree_gist, pg_trgm) are
 * declared in hartonomous.control's `requires` and installed
 * automatically by CREATE EXTENSION. */

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

-- ── sql/schema/domains/ordinal_position.sql ───────────────────────────────────────
CREATE DOMAIN substrate.ordinal_position AS INTEGER
    CONSTRAINT position_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.ordinal_position IS
    '0-indexed ordinal position in a parent composition (substrate.sequence).';

-- ── sql/schema/domains/rle_count.sql ───────────────────────────────────────
CREATE DOMAIN substrate.rle_count AS INTEGER
    CONSTRAINT rle_at_least_one CHECK (VALUE >= 1);
COMMENT ON DOMAIN substrate.rle_count IS
    'Run-length count for repeated children at the same ordinal position in substrate.sequence.';

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
CREATE INDEX idx_entity_type_modality ON substrate.entity_type(modality);
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
    'Geometry interpretation. What the GeometryZM value in substrate.physicality represents (s3_position, contour, weight_distribution, etc.).';

-- ── sql/schema/tables/reference/significance_context.sql ───────────────────────────────────────
CREATE TABLE substrate.significance_context (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.significance_context IS
    'Open-vocabulary arena definitions. Codes can be added at runtime; significance must auto-prime against every existing arena (rule 45 AP-1).';

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
    -- Modalities this source is authoritative in. Empty array → text default.
    modality_codes       TEXT[]      NOT NULL DEFAULT '{}',
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
    scope_entity_hash    BYTEA,
    -- Self-referential lineage FK; deferred so seeding can insert in any order.
    CONSTRAINT provenance_derives_from_fkey
        FOREIGN KEY (derives_from) REFERENCES substrate.provenance(code)
        DEFERRABLE INITIALLY DEFERRED
);

COMMENT ON TABLE substrate.provenance IS
    'Source of an entity or edge with trust prior. Carries the trust topology axes (modality, lineage, scope) the substrate combines into per-arena Glicko-2 priors via COALESCE(provenance_edge_authority.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay).';
COMMENT ON COLUMN substrate.provenance.curator_class IS
    'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';
COMMENT ON COLUMN substrate.provenance.initial_mu IS
    'Glicko-2 prior μ. Wide-band ladder: 20K (user_session) → 100K (authoritative_standard). Edge-time prior is multiplied by edge_type.semantic_weight × derivation_decay (with optional provenance_edge_authority override).';
COMMENT ON COLUMN substrate.provenance.modality_codes IS
    'Modalities this source is authoritative in (text, audio, image, video, model_weights). Cross-modal claims fall back to default.';
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
CREATE INDEX idx_block_range ON substrate.block(range_start, range_end);
COMMENT ON TABLE substrate.block IS
    'Unicode Block ranges. 300+ blocks. range_start/range_end enable O(log n) block lookup by codepoint integer.';

-- ── sql/schema/tables/reference/break_property.sql ───────────────────────────────────────
CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    UNIQUE(code, category)
);
CREATE INDEX idx_break_property_category ON substrate.break_property(category);
COMMENT ON TABLE substrate.break_property IS
    'UAX #29 break properties for segmentation. Four categories: GCB (grapheme), WB (word), SB (sentence), LB (line).';

-- ── sql/schema/tables/reference/language.sql ───────────────────────────────────────
CREATE TABLE substrate.language (
    id    SERIAL PRIMARY KEY,
    code  CHAR(3) NOT NULL UNIQUE,
    name  VARCHAR(128) NOT NULL,
    scope CHAR(1) NOT NULL,
    type  CHAR(1) NOT NULL
);
CREATE INDEX idx_language_scope ON substrate.language(scope);
CREATE INDEX idx_language_type ON substrate.language(type);
COMMENT ON TABLE substrate.language IS
    'ISO 639-3 language inventory. ~7,928 languages. Populated by ISO 639 seed.';
COMMENT ON COLUMN substrate.language.scope IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type  IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';

-- ── sql/schema/tables/reference/general_category.sql ───────────────────────────────────────
CREATE TABLE substrate.general_category (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(4) NOT NULL UNIQUE,
    group_code  VARCHAR(1) NOT NULL,
    description VARCHAR(64) NOT NULL
);
CREATE INDEX idx_general_category_group ON substrate.general_category(group_code);
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
CREATE INDEX idx_morph_feature_key ON substrate.morph_feature(key);
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
CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);
COMMENT ON TABLE substrate.edge_type IS
    'Operational edge typing with domain/range entity type constraints + structural-value tier (semantic_weight) for the trust-prior formula. Categories: structural, semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, unicode.';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS
    'FK to entity_type — constrains which entity types can be source. NULL means polymorphic source.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS
    'FK to entity_type — constrains which entity types can be target. NULL means polymorphic target.';
COMMENT ON COLUMN substrate.edge_type.semantic_weight IS
    'Structural-value tier 0.5..1.0. POS/sense/antonym/structural carry full weight (1.0); looser semantic relations (synonym, related, coordinate_term) carry less. Multiplied into the COALESCE prior μ at edge_significance lookup time.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 6: reference seed (entity_type before edge_type — FK code lookup) ─
-- provenance_edge_authority seed is deferred to Phase 8b (after the
-- junction table is created) since it INSERTs against substrate.provenance_edge_authority.

-- ── sql/schema/seed/entity_type.sql ───────────────────────────────────────
-- Entity types: 54 rows in canonical insertion order. SERIAL ids 1..42 are
-- pinned to the entity_model named partition (tables/core/entity_model.sql).
-- IDs 43..54 (per-role-unit families added for the analysis-pass DAG —
-- attention components, conv/codec filters, MoE expert neurons + route
-- directions, vision/conformer/diffusion/LoRA/modality/object-query/bbox/
-- class-head per-row units) currently route through entity_default. A
-- follow-up migration can detach/extend entity_model's FOR VALUES list to
-- bring them under the named partition for index locality.
--
-- Content-kind only. No dataset-named, algorithm-named, or relation-shaped
-- types. The substrate's identity model (BLAKE3 over content) requires that
-- the same content collapses to the same hash regardless of source.
--
-- Track 1 — text/audio/image/video content:
--   codepoint, grapheme_cluster, word_form, morpheme, lemma,
--   text_composition, paragraph, document, synset,
--   collation_element, language_name,
--   pixel_region, audio_recording, audio_chunk, video_frame.
--
-- Track 2 — model decomposition. Per .claude/rules/35-inference-and-godel.md
-- the per-role unit entities are what carry a model's learned function. The
-- substrate doesn't store every weight verbatim — it extracts the activated
-- semantic paths (lottery-ticket subnetwork) and discards gradient noise per
-- Law #11 (sparsity is honest recording, not approximation).
--
--   tensor                   — one safetensors entry (a whole weight matrix)
--   model_architecture       — a model's architecture identity
--   tokenizer_model          — a tokenizer instance (vocab + merges + config)
--   attention_pattern        — Q/K/V/O contribution pattern across heads
--   attention_head           — one head of multi-head attention
--   attention_archetype      — canonical pattern an attention layer encodes
--   embedding_position       — one row of the token embedding matrix (firefly)
--   ffn_neuron               — one output neuron of an FFN layer
--   logit_projection         — one row of the output unembedding matrix
--   moe_route                — a single MoE router decision
--   moe_routing_profile      — aggregate routing distribution for an MoE layer
--   residual_direction       — a principal direction in residual-stream geometry
--   archetype                — a canonical pattern that a tensor encodes
--
-- Per-tensor analysis surfaces (each is a content-addressed reduction of a
-- whole tensor's structure; identical reductions across models dedup):
--   sparsity_profile         — log-magnitude bucket histogram + near-zero fraction
--   weight_distribution      — distribution shape (mean, std, percentiles, etc.)
--   eigenvalue_spectrum      — top-K eigenvalues of weight covariance
--   svd_spectrum             — singular value spectrum
--   svd_rank_component       — one rank-1 component (u_i ⊗ v_i⊤)
--   activation_range         — observed min/max/mean activation per row/col
--   layer_norm_scale         — per-feature layer-norm γ scale parameter set
--   layer_similarity_pair    — pair of tensors with measured similarity
--   rope_freq_table          — RoPE frequency table for an attention layer
--   codec_codebook           — quantization codebook (AWQ/GPTQ etc.)
--   codec_codevector         — single centroid in a quantization codebook
--   vocab_coverage_profile   — tokenizer vocab coverage of the lemma seed
--
-- Removed (vs the original 25-row schema, none re-added):
--   ud_sentence, ud_token, tatoeba_sentence, word_sense, wikt_sense,
--   bpe_token, inflected_form. See prior version comments.
INSERT INTO substrate.entity_type (code, modality) VALUES
    ('codepoint',                'text'),           --  1
    ('grapheme_cluster',         'text'),           --  2
    ('word_form',                'text'),           --  3
    ('morpheme',                 'text'),           --  4
    ('lemma',                    'text'),           --  5
    ('text_composition',         'text'),           --  6
    ('paragraph',                'text'),           --  7
    ('document',                 'text'),           --  8
    ('synset',                   'text'),           --  9
    ('collation_element',        'text'),           -- 10
    ('language_name',            'text'),           -- 11
    ('pixel_region',             'image'),          -- 12
    ('audio_recording',          'audio'),          -- 13
    ('audio_chunk',              'audio'),          -- 14
    ('video_frame',              'video'),          -- 15
    ('tensor',                   'model_weights'),  -- 16
    ('model_architecture',       'model_weights'),  -- 17
    ('tokenizer_model',          'model_weights'),  -- 18
    ('attention_pattern',        'model_weights'),  -- 19
    ('attention_head',           'model_weights'),  -- 20
    ('attention_archetype',      'model_weights'),  -- 21
    ('embedding_position',       'model_weights'),  -- 22
    ('ffn_neuron',               'model_weights'),  -- 23
    ('logit_projection',         'model_weights'),  -- 24
    ('moe_route',                'model_weights'),  -- 25
    ('moe_routing_profile',      'model_weights'),  -- 26
    ('residual_direction',       'model_weights'),  -- 27
    ('archetype',                'model_weights'),  -- 28
    ('sparsity_profile',         'model_weights'),  -- 29
    ('weight_distribution',      'model_weights'),  -- 30
    ('eigenvalue_spectrum',      'model_weights'),  -- 31
    ('svd_spectrum',             'model_weights'),  -- 32
    ('svd_rank_component',       'model_weights'),  -- 33
    ('activation_range',         'model_weights'),  -- 34
    ('layer_norm_scale',         'model_weights'),  -- 35
    ('layer_similarity_pair',    'model_weights'),  -- 36
    ('rope_freq_table',          'model_weights'),  -- 37
    ('codec_codebook',           'model_weights'),  -- 38
    ('codec_codevector',         'model_weights'),  -- 39
    ('vocab_coverage_profile',   'model_weights'),  -- 40
    ('codebook',                 'model_weights'),  -- 41
    ('codevector',               'model_weights'),  -- 42
    -- Per-role-unit entity types for analysis passes (see edge_type.sql for
    -- the matching has_* edge codes that bind a tensor to its rows). Each
    -- row is content-hashed via PerRowContentPass canonical f64 encoding so
    -- identical row content across models / shards collapses to ONE entity.
    -- (AttentionComponentPass reuses attention_pattern (id 19) as its entity
    -- type — has_attention_component is the edge that bears the binding.)
    ('audio_codec_filter',       'model_weights'),  -- 43 (audio codec filter row)
    ('bbox_projection',          'model_weights'),  -- 44 (detection bbox-head projection)
    ('class_projection',         'model_weights'),  -- 45 (classification head projection)
    ('conformer_component',      'model_weights'),  -- 46 (conformer block component row)
    ('conv_filter',              'model_weights'),  -- 47 (per-channel conv filter)
    ('diffusion_component',      'model_weights'),  -- 48 (diffusion U-Net component row)
    ('lora_component',           'model_weights'),  -- 49 (LoRA adapter rank-1 component)
    ('modality_basis_vector',    'model_weights'),  -- 50 (cross-modal basis direction)
    ('moe_expert_neuron',        'model_weights'),  -- 51 (per-expert FFN neuron)
    ('moe_route_direction',      'model_weights'),  -- 52 (router gate direction)
    ('object_query_slot',        'model_weights'),  -- 53 (object-detection query slot)
    ('vision_feature_direction', 'model_weights');  -- 54 (vision-feature row direction)

-- ── sql/schema/seed/physicality_type.sql ───────────────────────────────────────
-- Physicality types: 13 rows, ids 1..13 must match partition declarations.
INSERT INTO substrate.physicality_type (code) VALUES
    ('s3_position'),
    ('hilbert_value'),
    ('waveform'),
    ('fft_spectrum'),
    ('stft_spectrogram'),
    ('pitch_contour'),
    ('formant_trajectory'),
    ('spectral_centroid'),
    ('mfcc_frame'),
    ('chromagram'),
    ('svd_spectrum'),
    ('weight_distribution'),
    ('contour');

-- ── sql/schema/seed/physicality_type_embedding_firefly.sql ───────────────────────────────────────
-- V1 stage 0035 — physicality type extensions.
--
-- KEEP: embedding_firefly. The existing EmbeddingFireflyPass calls
-- AddPhysicalityPoint4d(token_entity, "embedding_firefly", ...) and that
-- physicality_type was missing from the seed, leaving every firefly
-- insert dangling on a non-existent type_id. This is the load-bearing
-- addition.
--
-- REMOVED: firefly_consensus_traj, embedding_native, firefly_at_*_tier.
-- None are emitted by any pass. Adding them registers vocabulary the
-- substrate doesn't use. Bring them back when the matching pass exists.

INSERT INTO substrate.physicality_type (code) VALUES
    ('embedding_firefly')
ON CONFLICT (code) DO NOTHING;

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
    ('morphological_productivity');

-- ── sql/schema/seed/provenance.sql ───────────────────────────────────────
-- substrate.provenance seed — wide-band tier ladder.
--
-- Glicko-2 priors span 20K (user_session) to 100K (authoritative_standard).
-- modality_codes enumerates which modalities each source is authoritative
-- in. derives_from + derivation_decay model lineage (OMW = 0.92 × WordNet).
--
-- Tier ladder rationale: cross-modal cross-source comparison only works
-- when a source's prior reflects its actual epistemic status. Flat 1500
-- priors made A* over arenas degenerate to uniform-cost BFS — the
-- topology was structurally absent from the substrate.
INSERT INTO substrate.provenance
    (code, curator_class, initial_mu, initial_sigma, modality_codes, derives_from, derivation_decay)
VALUES
    ('unicode_consortium',     'authoritative_standard', 100000,  50, ARRAY['text'],                                                NULL,                1.00),
    ('sil_international',      'authoritative_standard', 100000,  50, ARRAY['text'],                                                NULL,                1.00),
    ('princeton_wordnet',      'academic_curated',        90000, 100, ARRAY['text'],                                                NULL,                1.00),
    ('omwn_consortium',        'academic_consortium',     85000, 100, ARRAY['text'],                                                'princeton_wordnet', 0.92),
    ('universaldependencies',  'academic_consortium',     85000, 100, ARRAY['text'],                                                NULL,                1.00),
    ('wiktextract',            'community_curated',       70000, 200, ARRAY['text'],                                                NULL,                1.00),
    ('tatoeba',                'community_contributed',   50000, 350, ARRAY['text','audio'],                                        NULL,                1.00),
    ('huggingface_model',      'model_derived',           60000, 350, ARRAY['text','model_weights'],                                NULL,                1.00),
    ('system_computed',        'system_computed',         40000, 350, ARRAY['text','image','audio','video','model_weights'],        NULL,                1.00),
    ('user_session',           'user_input',              20000, 500, ARRAY['text','image','audio','video','model_weights'],        NULL,                1.00);

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
-- 111 edge types. Codes 1..39 land in named partitions
-- (tables/core/edge_structural.sql, edge_cross_lingual.sql, edge_cross_modal.sql,
-- edge_unicode.sql, edge_model.sql); codes 40..111 land in edge_default.
-- Single INSERT...SELECT pattern: tuples in a VALUES CTE, resolved against
-- substrate.entity_type via JOIN once, semantic_weight derived in CASE.
-- NULL source/target codes mean polymorphic.

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
    -- Structural ──────────────────────────────────────────────────────
    ('has_sense',                'structural',    'lemma',              'synset'),
    ('has_form',                 'structural',    'lemma',              'word_form'),
    ('has_lemma',                'structural',    'word_form',          'lemma'),
    ('has_morpheme',             'structural',    'word_form',          'morpheme'),
    ('has_gloss',                'structural',    'synset',             'text_composition'),
    ('has_example',              'structural',    'synset',             'text_composition'),
    ('has_name',                 'structural',    'model_architecture', 'text_composition'),
    ('inflection_of',            'structural',    'word_form',          'lemma'),
    ('has_etymology',            'structural',    'lemma',              'text_composition'),
    ('has_pronunciation',        'structural',    'lemma',              'text_composition'),
    ('has_hyphenation',          'structural',    'lemma',              'text_composition'),
    ('has_wikidata',             'structural',    'lemma',              'text_composition'),
    ('lexicalized_compound',     'structural',    'word_form',          'word_form'),
    ('has_frame',                'structural',    'lemma',              'text_composition'),
    ('has_wordnet_offset',       'structural',    'synset',             'text_composition'),
    -- Cross-lingual ───────────────────────────────────────────────────
    ('aligned_to_synset',        'cross_lingual', 'lemma',              'synset'),
    ('translation_of',           'cross_lingual', 'lemma',              'lemma'),
    ('translation_link',         'cross_lingual', 'text_composition',   'text_composition'),
    ('macrolanguage_contains',   'cross_lingual', 'language_name',      'language_name'),
    ('has_alternate_name',       'cross_lingual', 'language_name',      'language_name'),
    ('superseded_by',            'cross_lingual', 'language_name',      'language_name'),
    ('etym_inherited_from',      'cross_lingual', 'lemma',              'lemma'),
    ('etym_derived_from',        'cross_lingual', 'lemma',              'lemma'),
    ('etym_borrowed_from',       'cross_lingual', 'lemma',              'lemma'),
    ('etym_cognate_with',        'cross_lingual', 'lemma',              'lemma'),
    ('etym_calque_of',           'cross_lingual', 'lemma',              'lemma'),
    ('etym_mention',             'cross_lingual', 'lemma',              'lemma'),
    ('etym_link',                'cross_lingual', 'lemma',              'text_composition'),
    ('etym_etymon',              'cross_lingual', 'lemma',              'lemma'),
    -- Cross-modal ─────────────────────────────────────────────────────
    ('recording_of',             'cross_modal',   'audio_recording',    'text_composition'),
    ('has_contributor',          'cross_modal',   'audio_recording',    'text_composition'),
    -- Unicode ─────────────────────────────────────────────────────────
    ('maps_to_lowercase',        'unicode',       'codepoint',          'codepoint'),
    ('case_folds_to',            'unicode',       'codepoint',          'codepoint'),
    ('has_collation_weight',     'unicode',       'codepoint',          'collation_element'),
    -- Model-derived: architecture metadata ────────────────────────────
    ('in_model',                 'model_derived', 'tensor',             'model_architecture'),
    ('in_layer',                 'model_derived', 'tensor',             'model_architecture'),
    ('has_dtype',                'model_derived', 'tensor',             'text_composition'),
    ('has_shape',                'model_derived', 'tensor',             'text_composition'),
    ('has_hidden_size',          'model_derived', 'model_architecture', 'text_composition'),
    ('has_num_layers',           'model_derived', 'model_architecture', 'text_composition'),
    ('has_num_attention_heads',  'model_derived', 'model_architecture', 'text_composition'),
    ('has_vocab_size',           'model_derived', 'model_architecture', 'text_composition'),
    ('has_token_id',             'model_derived', 'word_form',          'text_composition'),
    ('in_vocabulary',            'model_derived', 'word_form',          'model_architecture'),
    ('co_occurrence',            'model_derived', NULL,                 NULL),
    ('has_tensor',               'model_derived', 'model_architecture', 'tensor'),
    ('has_architecture_name',    'model_derived', 'model_architecture', 'text_composition'),
    -- Model-derived: tensor analysis surfaces ─────────────────────────
    ('has_tensor_name',          'model_derived', 'tensor',             'text_composition'),
    ('has_tokenizer_model',      'model_derived', 'model_architecture', 'text_composition'),
    ('has_token_in_tokenizer',   'model_derived', 'model_architecture', 'word_form'),
    ('has_weight_distribution',  'model_derived', 'tensor',             'weight_distribution'),
    ('has_spectrum',             'model_derived', 'tensor',             'svd_spectrum'),
    ('has_eigenvalue_spectrum',  'model_derived', 'tensor',             'eigenvalue_spectrum'),
    ('has_sparsity_profile',     'model_derived', 'tensor',             'sparsity_profile'),
    ('has_activation_range',     'model_derived', 'tensor',             'activation_range'),
    ('has_layer_norm_scale',     'model_derived', 'tensor',             'layer_norm_scale'),
    ('has_codebook',             'model_derived', 'tensor',             'codec_codebook'),
    ('contains_codevector',      'model_derived', 'codec_codebook',     'codec_codevector'),
    ('encodes_archetype',        'model_derived', 'tensor',             'archetype'),
    ('has_layer_similarity',     'model_derived', 'tensor',             'layer_similarity_pair'),
    ('has_rope_freqs',           'model_derived', 'tensor',             'rope_freq_table'),
    ('has_rank_component',       'model_derived', 'tensor',             'svd_rank_component'),
    ('has_moe_routing',          'model_derived', 'tensor',             'moe_routing_profile'),
    ('has_embedding_position',   'model_derived', 'tensor',             'embedding_position'),
    ('has_ffn_neuron',           'model_derived', 'tensor',             'ffn_neuron'),
    ('has_logit_projection',     'model_derived', 'tensor',             'logit_projection'),
    ('covers_lemma',             'model_derived', 'word_form',          'lemma'),
    ('has_vocab_coverage',       'model_derived', 'tokenizer_model',    'vocab_coverage_profile'),
    -- Model-derived: per-role-unit binding edges ─────────────────────
    ('has_attention_component',  'model_derived', 'tensor',             'attention_pattern'),
    ('has_codec_filter',         'model_derived', 'tensor',             'audio_codec_filter'),
    ('has_bbox_projection',      'model_derived', 'tensor',             'bbox_projection'),
    ('has_class_projection',     'model_derived', 'tensor',             'class_projection'),
    ('has_conformer_component',  'model_derived', 'tensor',             'conformer_component'),
    ('has_conv_filter',          'model_derived', 'tensor',             'conv_filter'),
    ('has_diffusion_component',  'model_derived', 'tensor',             'diffusion_component'),
    ('has_lora_component',       'model_derived', 'tensor',             'lora_component'),
    ('has_modality_basis',       'model_derived', 'tensor',             'modality_basis_vector'),
    ('has_moe_neuron',           'model_derived', 'tensor',             'moe_expert_neuron'),
    ('has_route_direction',      'model_derived', 'tensor',             'moe_route_direction'),
    ('has_object_query',         'model_derived', 'tensor',             'object_query_slot'),
    ('has_vision_feature',       'model_derived', 'tensor',             'vision_feature_direction'),
    -- Semantic: WordNet pointers (synset ↔ synset) ────────────────────
    ('hypernym',                 'semantic',      'synset', 'synset'),
    ('hyponym',                  'semantic',      'synset', 'synset'),
    ('instance_hypernym',        'semantic',      'synset', 'synset'),
    ('instance_hyponym',         'semantic',      'synset', 'synset'),
    ('member_holonym',           'semantic',      'synset', 'synset'),
    ('substance_holonym',        'semantic',      'synset', 'synset'),
    ('part_holonym',             'semantic',      'synset', 'synset'),
    ('member_meronym',           'semantic',      'synset', 'synset'),
    ('substance_meronym',        'semantic',      'synset', 'synset'),
    ('part_meronym',             'semantic',      'synset', 'synset'),
    ('attribute',                'semantic',      'synset', 'synset'),
    ('derivationally_related',   'semantic',      'synset', 'synset'),
    ('antonym',                  'semantic',      'synset', 'synset'),
    ('similar_to',               'semantic',      'synset', 'synset'),
    ('also_see',                 'semantic',      'synset', 'synset'),
    ('verb_group',               'semantic',      'synset', 'synset'),
    ('entailment',               'semantic',      'synset', 'synset'),
    ('cause',                    'semantic',      'synset', 'synset'),
    ('participle_of_verb',       'semantic',      'synset', 'synset'),
    ('pertainym',                'semantic',      'synset', 'synset'),
    ('domain_of_synset_topic',   'semantic',      'synset', 'synset'),
    ('member_of_domain_topic',   'semantic',      'synset', 'synset'),
    ('domain_of_synset_region',  'semantic',      'synset', 'synset'),
    ('member_of_domain_region',  'semantic',      'synset', 'synset'),
    ('domain_of_synset_usage',   'semantic',      'synset', 'synset'),
    ('member_of_domain_usage',   'semantic',      'synset', 'synset'),
    -- Semantic: Wiktionary lemma ↔ lemma ──────────────────────────────
    ('synonym',                  'semantic',      'lemma',  'lemma'),
    ('coordinate_term',          'semantic',      'lemma',  'lemma'),
    ('derived',                  'semantic',      'lemma',  'lemma'),
    ('related',                  'semantic',      'lemma',  'lemma')
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
            ('substrate.entity_type',           54),
            ('substrate.physicality_type',      14),
            ('substrate.edge_role',              7),
            ('substrate.significance_context',  10),
            ('substrate.provenance',            10),
            ('substrate.lexname',               45),
            ('substrate.pos',                   17),
            ('substrate.edge_type',            111)
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
-- The composite (entity_type_id, hash) PK that previously fragmented
-- "dog the lemma" and "dog the word_form" into TWO rows is gone. One hash
-- = one row. Period.
--
-- No partitioning by type. The entity table is a single index of hashes;
-- B-tree on the PK gives O(log N) lookup. Per-type query patterns now
-- JOIN substrate.entity_classification instead of partition-pruning.
CREATE TABLE substrate.entity (
    hash substrate.hash_value PRIMARY KEY
);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom OR composition. Identity = BLAKE3 hash of content. Classifications via substrate.entity_classification. Single table — no LIST partition by type.';

-- ── sql/schema/tables/core/edge.sql ───────────────────────────────────────
-- Edge identity = BLAKE3 of (edge_type_id, ordered participant hashes).
-- No surrogate id. PK (edge_type_id, hash). Partitioned by edge_type_id.
-- geom is populated post-insert from participant centroids in role order
-- via substrate.populate_edge_trajectories.
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
-- Edge types 1..13: has_sense, has_form, has_lemma, has_morpheme, has_gloss,
-- has_example, has_name, has_text, inflection_of, has_etymology,
-- has_pronunciation, has_hyphenation, has_wikidata. Plus 37 lexicalized_compound.
CREATE TABLE substrate.edge_structural
    PARTITION OF substrate.edge FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 37);

-- ── sql/schema/tables/core/edge_cross_lingual.sql ───────────────────────────────────────
-- Edge types 14..16: aligned_to_synset, translation_of, translation_link.
-- Plus 34..36: macrolanguage_contains, has_alternate_name, superseded_by.
CREATE TABLE substrate.edge_cross_lingual
    PARTITION OF substrate.edge FOR VALUES IN (14, 15, 16, 34, 35, 36);

-- ── sql/schema/tables/core/edge_cross_modal.sql ───────────────────────────────────────
-- Edge types 17..18: recording_of, has_contributor.
CREATE TABLE substrate.edge_cross_modal
    PARTITION OF substrate.edge FOR VALUES IN (17, 18);

-- ── sql/schema/tables/core/edge_unicode.sql ───────────────────────────────────────
-- Edge types 19..21: maps_to_lowercase, case_folds_to, has_collation_weight.
CREATE TABLE substrate.edge_unicode
    PARTITION OF substrate.edge FOR VALUES IN (19, 20, 21);

-- ── sql/schema/tables/core/edge_model.sql ───────────────────────────────────────
-- Edge types 22..33: in_model, in_layer, has_dtype, has_shape, has_hidden_size,
-- has_num_layers, has_num_attention_heads, has_vocab_size, has_token_string,
-- has_token_id, in_vocabulary, co_occurrence. Plus 38..39: has_tensor, has_architecture_name.
CREATE TABLE substrate.edge_model
    PARTITION OF substrate.edge FOR VALUES IN (22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 38, 39);

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
CREATE TABLE substrate.edge_member (
    edge_type_id INT  NOT NULL,
    edge_hash    substrate.hash_value NOT NULL,
    entity_hash  substrate.hash_value NOT NULL,
    edge_role_id INT  NOT NULL REFERENCES substrate.edge_role(id),
    role_position INT NOT NULL DEFAULT 0,
    PRIMARY KEY (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    -- FKs application-enforced. Pipeline batch ordering guarantees entity
    -- and edge rows precede edge_member rows.
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge_member IS
    'N-ary edge participants with roles. Edge identity: (edge_type_id, edge_hash). Entity reference: hash only (no type_id). Partitioned by edge_type_id. FKs application-enforced.';

-- ── sql/schema/tables/core/edge_member_structural.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_structural
    PARTITION OF substrate.edge_member FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 37);

-- ── sql/schema/tables/core/edge_member_cross_lingual.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_cross_lingual
    PARTITION OF substrate.edge_member FOR VALUES IN (14, 15, 16, 34, 35, 36);

-- ── sql/schema/tables/core/edge_member_cross_modal.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_cross_modal
    PARTITION OF substrate.edge_member FOR VALUES IN (17, 18);

-- ── sql/schema/tables/core/edge_member_unicode.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_unicode
    PARTITION OF substrate.edge_member FOR VALUES IN (19, 20, 21);

-- ── sql/schema/tables/core/edge_member_model.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_model
    PARTITION OF substrate.edge_member FOR VALUES IN (22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 38, 39);

-- ── sql/schema/tables/core/edge_member_default.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_default
    PARTITION OF substrate.edge_member DEFAULT;

-- ── sql/schema/tables/core/physicality.sql ───────────────────────────────────────
-- 4D geometric realization of an entity. PostGIS-native GeometryZM
-- (POINTZM for atoms, LINESTRINGZM for compositions, M as a real spatial
-- axis). Per-partition CHECK constraints enforce the dimensionality each
-- physicality_type expects. content_hash distinguishes multiple physicalities
-- of the same type for the same entity (e.g., multiple firefly samples).
--
-- Hash-only entity reference (Phase C of unification refactor):
-- substrate.entity has a hash-only PK, so physicality references entities
-- by hash alone. No entity_type_id column.
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry(GeometryZM) NOT NULL,
    PRIMARY KEY (physicality_type_id, entity_hash, content_hash)
    -- FK to substrate.entity(hash) application-enforced — pipeline batch
    -- ordering writes entities before physicalities. (PG18.3 partitionwise-FK
    -- SEGV pattern conservatively avoided.)
) PARTITION BY LIST (physicality_type_id);

COMMENT ON TABLE substrate.physicality IS
    'Geometric realizations of entities. PostGIS GeometryZM. Hash-only entity reference (no type_id). Partitioned by physicality_type_id. FK to substrate.entity application-enforced.';

-- ── sql/schema/tables/core/physicality_s3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_s3
    PARTITION OF substrate.physicality FOR VALUES IN (1);
ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);

-- ── sql/schema/tables/core/physicality_hilbert.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_hilbert
    PARTITION OF substrate.physicality FOR VALUES IN (2);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);

-- ── sql/schema/tables/core/physicality_audio.sql ───────────────────────────────────────
-- Physicality types 3..10: waveform, fft_spectrum, stft_spectrogram,
-- pitch_contour, formant_trajectory, spectral_centroid, mfcc_frame, chromagram.
-- Mixed geometry shapes (POINTZM for spectral_centroid, LINESTRINGZM for
-- contours/trajectories, MULTILINESTRINGZM for spectrograms) — no single
-- partition CHECK; per-row geometry validated by PostGIS internals.
CREATE TABLE substrate.physicality_audio
    PARTITION OF substrate.physicality FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);

-- ── sql/schema/tables/core/physicality_model.sql ───────────────────────────────────────
-- Physicality types 11..12: svd_spectrum, weight_distribution.
-- Both 4D (POINTZM or LINESTRINGZM); enforced per-row.
CREATE TABLE substrate.physicality_model
    PARTITION OF substrate.physicality FOR VALUES IN (11, 12);

-- ── sql/schema/tables/core/physicality_contour.sql ───────────────────────────────────────
-- Physicality type 13: contour. LINESTRINGZM trajectories through codepoint
-- S3 positions. The dominant text-side physicality.
CREATE TABLE substrate.physicality_contour
    PARTITION OF substrate.physicality FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestringzm
    CHECK (ST_GeometryType(geom) = 'ST_LineString' AND ST_NDims(geom) = 4);

-- ── sql/schema/tables/core/physicality_default.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default
    PARTITION OF substrate.physicality DEFAULT;

-- ── sql/schema/tables/core/sequence.sql ───────────────────────────────────────
-- substrate.sequence — the indexed parent → ordered children record.
--
-- Hash-as-PK throughout. Composite (parent_hash, ordinal) is the natural
-- key — repetition (refrain in Green Eggs and Ham, noreply@example.com
-- appearing 47 times in one email body) is preserved by distinct ordinals
-- pointing to the SAME content-addressed child entity. The child entity
-- stays one row in substrate.entity (content dedup); the sequence rows
-- are how we record where that one entity sits inside each parent.
--
-- rle_count compresses contiguous runs of the same child: three identical
-- sentences in a row collapse to one row with ordinal = first position
-- and rle_count = 3. Lookup at ordinal N walks
-- WHERE ordinal <= N AND ordinal + rle_count > N — still indexed,
-- still microseconds.
--
-- Per-entity-type partitioning DROPPED: substrate.entity is no longer
-- partitioned by type (Phase C of unification refactor — entity is
-- content-only; types are junction metadata). Sequence is similarly
-- single-table now. Index on (parent_hash, ordinal) provides O(log N)
-- random access; inverse index on (child_hash) provides parent lookup.
CREATE TABLE substrate.sequence (
    parent_hash substrate.hash_value NOT NULL,
    ordinal     INT  NOT NULL,
    child_hash  substrate.hash_value NOT NULL,
    rle_count   INT  NOT NULL DEFAULT 1,
    PRIMARY KEY (parent_hash, ordinal)
    -- FK to substrate.entity intentionally omitted — application-layer
    -- batch ordering guarantees parent + child entity rows exist before
    -- their sequence rows. (Same PG18.3 partitionwise-FK SEGV pattern
    -- documented elsewhere; conservatively kept omitted post-collapse.)
);

CREATE INDEX idx_sequence_child ON substrate.sequence(child_hash, parent_hash);

COMMENT ON TABLE substrate.sequence IS
    'Parent → ordered children with RLE for refrain compression. Hash-only references — entity type is irrelevant to ordinal lookup. Btree-indexed on (parent_hash, ordinal) for microsecond random access; inverse index on (child_hash) for parent lookup.';

-- ── sql/schema/tables/core/entity_significance.sql ───────────────────────────────────────
-- Glicko-2 ratings on entities, per arena. Hash-only entity reference
-- (Phase C of unification refactor — substrate.entity has hash-only PK,
-- no entity_type_id).
CREATE TABLE substrate.entity_significance (
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_hash     substrate.hash_value NOT NULL,
    mu              substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma           substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility      substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, entity_hash)
    -- FK to substrate.entity(hash) application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.entity_significance IS
    'Glicko-2 trust per (entity, arena). Hash-only entity reference. Partitioned by context_type_id.';

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
-- Glicko-2 ratings on edges, per arena. Split from entity_significance.
-- Edge cost during A* traversal = 1 / mu in the requested arena. New arenas
-- (open vocabulary) must auto-prime against every existing edge — see
-- substrate.prime_edge_significance.
CREATE TABLE substrate.edge_significance (
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    edge_type_id    INT NOT NULL,
    edge_hash       substrate.hash_value NOT NULL,
    mu              substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma           substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility      substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash)
    -- FK to substrate.edge application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.edge_significance IS
    'Glicko-2 trust per (edge, arena). Hash-addressable via (edge_type_id, edge_hash). Partitioned by context_type_id. FK application-enforced.';

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

-- ── Phase 8: junction tables ─────────────────────────────────────────

-- ── sql/schema/tables/junctions/entity_pos.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_pos (
    entity_hash substrate.hash_value NOT NULL,
    pos_id      INT  NOT NULL REFERENCES substrate.pos(id),
    mu          FLOAT8 NOT NULL DEFAULT 1500,
    sigma       FLOAT8 NOT NULL DEFAULT 350,
    volatility  FLOAT8 NOT NULL DEFAULT 0.06,
    games       INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, pos_id)
);
CREATE INDEX idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_hash);
COMMENT ON TABLE substrate.entity_pos IS
    'Entity → POS with Glicko-2. Hash-only entity reference. Multiple POS per entity supported.';

-- ── sql/schema/tables/junctions/entity_lexname.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_lexname (
    entity_hash substrate.hash_value NOT NULL,
    lexname_id  INT  NOT NULL REFERENCES substrate.lexname(id),
    PRIMARY KEY (entity_hash, lexname_id)
);
CREATE INDEX idx_entity_lexname_lexname ON substrate.entity_lexname(lexname_id, entity_hash);
COMMENT ON TABLE substrate.entity_lexname IS
    'Entity → lexname. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/entity_language.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_language (
    entity_hash substrate.hash_value NOT NULL,
    language_id INT  NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_hash, language_id)
);
CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_hash);
COMMENT ON TABLE substrate.entity_language IS
    'Entity → language. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/entity_morph_feature.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_morph_feature (
    entity_hash      substrate.hash_value NOT NULL,
    morph_feature_id INT  NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_hash, morph_feature_id)
);
CREATE INDEX idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_hash);
COMMENT ON TABLE substrate.entity_morph_feature IS
    'Entity → morphological feature. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/codepoint_property.sql ───────────────────────────────────────
-- Codepoint properties indexed by entity hash. Phase C unification:
-- hash-only entity reference (substrate.entity has hash-only PK).
CREATE TABLE substrate.codepoint_property (
    entity_hash              substrate.hash_value PRIMARY KEY,
    codepoint_value          INT  NOT NULL,
    general_category_id      INT  NOT NULL REFERENCES substrate.general_category(id),
    script_id                INT  NOT NULL REFERENCES substrate.script(id),
    block_id                 INT  NOT NULL REFERENCES substrate.block(id),
    gcb_id                   INT  REFERENCES substrate.break_property(id),
    wb_id                    INT  REFERENCES substrate.break_property(id),
    sb_id                    INT  REFERENCES substrate.break_property(id),
    lb_id                    INT  REFERENCES substrate.break_property(id),
    is_extended_pictographic BOOLEAN NOT NULL DEFAULT FALSE,
    ccc                      SMALLINT NOT NULL DEFAULT 0,
    decomposition_type       VARCHAR(16),
    decomposition_mapping    INT[],
    simple_case_fold         INT,
    full_case_fold           INT[]
);
CREATE INDEX idx_codepoint_property_codepoint ON substrate.codepoint_property(codepoint_value);
CREATE INDEX idx_codepoint_property_gc        ON substrate.codepoint_property(general_category_id);
CREATE INDEX idx_codepoint_property_script    ON substrate.codepoint_property(script_id);
CREATE INDEX idx_codepoint_property_block     ON substrate.codepoint_property(block_id);
COMMENT ON TABLE substrate.codepoint_property IS
    'Codepoint → Unicode properties. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/model_architecture_class.sql ───────────────────────────────────────
CREATE TABLE substrate.model_architecture_class (
    entity_hash           substrate.hash_value NOT NULL,
    architecture_class_id INT  NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_hash, architecture_class_id)
);
CREATE INDEX idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_hash);
COMMENT ON TABLE substrate.model_architecture_class IS
    'Model entity → architecture class. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/tensor_tensor_role.sql ───────────────────────────────────────
CREATE TABLE substrate.tensor_tensor_role (
    entity_hash    substrate.hash_value NOT NULL,
    tensor_role_id INT  NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_hash, tensor_role_id)
);
CREATE INDEX idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_hash);
COMMENT ON TABLE substrate.tensor_tensor_role IS
    'Tensor entity → role. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/pattern_deprel.sql ───────────────────────────────────────
CREATE TABLE substrate.pattern_deprel (
    entity_hash substrate.hash_value NOT NULL,
    deprel_id   INT  NOT NULL REFERENCES substrate.deprel(id),
    mu          FLOAT8 NOT NULL DEFAULT 1200,
    sigma       FLOAT8 NOT NULL DEFAULT 350,
    volatility  FLOAT8 NOT NULL DEFAULT 0.06,
    games       INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, deprel_id)
);
CREATE INDEX idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_hash);
COMMENT ON TABLE substrate.pattern_deprel IS
    'Attention pattern → deprel with Glicko-2. Hash-only entity reference.';

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

CREATE INDEX IF NOT EXISTS idx_entity_classification_type
    ON substrate.entity_classification(entity_type_id, entity_hash);
CREATE INDEX IF NOT EXISTS idx_entity_classification_provenance
    ON substrate.entity_classification(provenance_id);

COMMENT ON TABLE substrate.entity_classification IS
    'Per-entity classification metadata. Content (entity_hash) is identity; classification (entity_type_id) is metadata. Multiple decomposers can independently assert classifications on the same content; provenance distinguishes them.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

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
CREATE INDEX idx_model_source_model     ON substrate.model_source(model_id);
CREATE INDEX idx_model_source_publisher ON substrate.model_source(publisher_id);
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
CREATE INDEX idx_model_pass_checkpoint_source ON substrate.model_pass_checkpoint(model_source_id);
COMMENT ON TABLE substrate.model_pass_checkpoint IS
    'Per-pass progress for safetensors decomposition. Lets a multi-pass ingestion resume after interruption.';

-- ── sql/schema/tables/models/entity_model_source.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_model_source (
    entity_hash     substrate.hash_value NOT NULL,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    PRIMARY KEY (entity_hash, model_source_id),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash) ON DELETE CASCADE
);
CREATE INDEX idx_entity_model_source_source ON substrate.entity_model_source(model_source_id, entity_hash);
COMMENT ON TABLE substrate.entity_model_source IS
    'Entity → model_source provenance. Hash-only entity reference. Same tensor in N model revisions has 1 entity row + N entity_model_source rows.';

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
CREATE INDEX idx_ingestion_progress_recent ON monitor.ingestion_progress(recorded_at DESC);
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
CREATE INDEX idx_error_log_recent ON monitor.error_log(occurred_at DESC);
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
CREATE INDEX idx_substrate_health_recent ON monitor.substrate_health(recorded_at DESC);
CREATE INDEX idx_substrate_health_code   ON monitor.substrate_health(metric_code, recorded_at DESC);
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
CREATE INDEX idx_inference_metrics_recent  ON monitor.inference_metrics(recorded_at DESC);
CREATE INDEX idx_inference_metrics_session ON monitor.inference_metrics(session_id, recorded_at DESC);
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
CREATE INDEX idx_session_started ON monitor.session(started_at DESC);
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
CREATE INDEX idx_comparison_event_session ON monitor.comparison_event(session_id, recorded_at DESC);
CREATE INDEX idx_comparison_event_arena   ON monitor.comparison_event(arena_code, recorded_at DESC);
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
CREATE INDEX idx_significance_snapshot_target ON monitor.significance_snapshot(target_kind, target_type_id, target_hash, recorded_at DESC);
COMMENT ON TABLE monitor.significance_snapshot IS
    'Periodic snapshots of significance state for time-series analysis.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 11: meta tables ────────────────────────────────────────────

-- ── sql/schema/tables/meta/arena_priming_state.sql ───────────────────────────────────────
-- Per-arena progress watermark for substrate.prime_unprimed_edges_chunk.
-- The backfill primer scans substrate.edge starting from
-- (last_edge_type_id, last_hash) using the (edge_type_id, hash) PK index.
-- This replaces the LEFT JOIN/IS NULL anti-join shape that triggered
-- PG18's batched-HashJoin slot mismatch (nodeHashjoin.c:1099-1115 vs
-- ExecJustOuterVarVirt) → SIGSEGV/SIGABRT.
CREATE TABLE IF NOT EXISTS substrate.arena_priming_state (
    context_type_id   INT  PRIMARY KEY
        REFERENCES substrate.significance_context(id) ON DELETE CASCADE,
    last_edge_type_id INT  NOT NULL DEFAULT 0,
    last_hash         BYTEA NOT NULL DEFAULT '\x'::BYTEA,
    completed         BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (Phase 12 deleted post-W2E refactor: substrate.staging_* tables and the
--  drain_staging_*_chunk / drain_all_staging functions are gone. The
--  StreamingIngestionPipeline writes DIRECTLY into substrate core tables
--  via session-local pg_temp.X_inflight tables created per drain-task
--  connection. ON CONFLICT DO NOTHING guards within-session and cross-
--  session duplicates. The post-pass populate_edge_trajectories +
--  prime_unprimed_edges_chunk run once per phase from FlushAsync; no
--  background drain worker, no background significance primer.)

-- ── Phase 13: functions ──────────────────────────────────────────────
-- Reference / utility helpers

-- ── ext/hartonomous_pg/sql/hartonomous--1.0.sql.in ───────────────────────────────────────

-- ════════════════════════════════════════════════════════════════════
-- Native C-binding declarations (from hartonomous--1.0.sql.in)
-- ════════════════════════════════════════════════════════════════════
-- hartonomous--1.0.sql
--
-- Per docs/specs/native/4d-type-and-index.md, declarations are ordered:
--   (1) shell types  → (2) I/O fns  → (3) full CREATE TYPE
--   (4) constructors and scalar fns
--   (5) operators
--   (6) GiST/SP-GiST opclasses (P1a.3 — declared empty here, populated later)
--   (7) aggregates
--   (8) BLAKE3 + traversal (preserved from prior version)
--
-- All wrappers are `PARALLEL SAFE` because the underlying native code is
-- pure (no shared mutable state) and the substrate-side functions only
-- read tables.


-- ── (1) Shell types ────────────────────────────────────────────────
CREATE TYPE point4d;
CREATE TYPE box4d;

-- ── (2) I/O functions ──────────────────────────────────────────────
CREATE FUNCTION point4d_in(cstring) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_point4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_out(point4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_point4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_recv(internal) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_point4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_send(point4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_point4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION box4d_in(cstring) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_out(box4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_box4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_recv(internal) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_send(box4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_box4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (3) Full CREATE TYPE ───────────────────────────────────────────
CREATE TYPE point4d (
    INTERNALLENGTH = 32,
    INPUT          = point4d_in,
    OUTPUT         = point4d_out,
    RECEIVE        = point4d_recv,
    SEND           = point4d_send,
    ALIGNMENT      = double,
    STORAGE        = plain
);

CREATE TYPE box4d (
    INTERNALLENGTH = 64,
    INPUT          = box4d_in,
    OUTPUT         = box4d_out,
    RECEIVE        = box4d_recv,
    SEND           = box4d_send,
    ALIGNMENT      = double,
    STORAGE        = plain
);

-- ── (4) Constructors and scalar functions ──────────────────────────

-- point4d(x1, x2, x3, x4)
CREATE FUNCTION point4d(double precision, double precision, double precision, double precision)
    RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_point4d_constructor'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- bbox(point4d) — degenerate box at a point
CREATE FUNCTION bbox(point4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_bbox_from_point'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION bbox_expand(box4d, point4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_expand_point'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION bbox_union(box4d, box4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_union'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Distances and S³ helpers (point4d-typed, no PostGIS bridge).
CREATE FUNCTION distance_4d(point4d, point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_distance_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION distance_s3(point4d, point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_distance_s3'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION dot_4d(point4d, point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_dot_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION norm_4d(point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_norm_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION normalize_4d(point4d) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_normalize_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- substrate.ls4d_from_centroids — build a PostGIS LINESTRINGZM from an
-- ordered point4d[] participant array. Used by edge emission paths to
-- materialize edge.geom from participant centroids in role order. The
-- inner C function returns EWKB; ST_GeomFromWKB lifts it to geometry.
-- Producers can also build the same EWKB directly in C# and skip this
-- round-trip when emitting via binary COPY.
CREATE FUNCTION substrate.ls4d_from_centroids_wkb(point4d[]) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_ls4d_from_centroids_wkb'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE OR REPLACE FUNCTION substrate.ls4d_from_centroids(point4d[])
RETURNS geometry(LINESTRINGZM)
LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT ST_GeomFromWKB(substrate.ls4d_from_centroids_wkb($1), 0)::geometry(LINESTRINGZM);
$$;

COMMENT ON FUNCTION substrate.ls4d_from_centroids(point4d[]) IS
    'Build a LINESTRINGZM from an ordered participant centroid array. Used to compute edge.geom inline from participants in role order, avoiding any post-insert geometry-population pass. SRID 0 (substrate is not georeferenced).';

CREATE FUNCTION slerp(point4d, point4d, double precision) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_slerp'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION antipode(point4d) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_antipode'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Super-Fibonacci S³ sample point and Hilbert index (4D).
CREATE FUNCTION super_fibonacci_4d(bigint, bigint) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_super_fibonacci_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION hilbert_4d(point4d, int) RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_hilbert_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION hilbert_4d_inverse(bigint, int) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_hilbert_4d_inverse'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Equality and hash for point4d.
CREATE FUNCTION point4d_eq(point4d, point4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_point4d_eq'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_ne(point4d, point4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_point4d_ne'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_hash(point4d) RETURNS integer
    AS 'MODULE_PATHNAME', 'pg_point4d_hash'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Box4D predicates and equality.
CREATE FUNCTION box4d_overlaps(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_overlaps'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_contains_point(box4d, point4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_contains_point'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point_contained_by_box4d(point4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_point_contained_by_box4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_contains_box(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_contains_box'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_contained_by_box(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_contained_by_box'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_eq(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_eq'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (5) Operators ──────────────────────────────────────────────────

CREATE OPERATOR <-> (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = distance_4d,
    COMMUTATOR = <->
);
CREATE OPERATOR <=> (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = distance_s3,
    COMMUTATOR = <=>
);

CREATE OPERATOR = (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = point4d_eq,
    COMMUTATOR = =, NEGATOR = <>, HASHES, MERGES
);
CREATE OPERATOR <> (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = point4d_ne,
    COMMUTATOR = <>, NEGATOR = =
);

CREATE OPERATOR && (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_overlaps,
    COMMUTATOR = &&
);
CREATE OPERATOR @> (
    LEFTARG = box4d, RIGHTARG = point4d, FUNCTION = box4d_contains_point
);
CREATE OPERATOR <@ (
    LEFTARG = point4d, RIGHTARG = box4d, FUNCTION = point_contained_by_box4d
);
CREATE OPERATOR @> (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_contains_box,
    COMMUTATOR = <@
);
CREATE OPERATOR <@ (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_contained_by_box,
    COMMUTATOR = @>
);
CREATE OPERATOR = (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_eq,
    COMMUTATOR = =
);

-- ── (6) Hash op family for point4d (covers the HASHES / MERGES properties) ──
CREATE OPERATOR FAMILY point4d_hash_ops USING hash;
CREATE OPERATOR CLASS point4d_hash_ops
    DEFAULT FOR TYPE point4d USING hash FAMILY point4d_hash_ops AS
        OPERATOR 1 = (point4d, point4d),
        FUNCTION 1 point4d_hash(point4d);

-- ── (6b) GiST opclass for point4d (R-tree-style, STORAGE box4d) ────────
CREATE FUNCTION gist_point4d_consistent(internal, point4d, smallint, oid, internal)
    RETURNS bool
    AS 'MODULE_PATHNAME', 'gist_point4d_consistent'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_union(internal, internal) RETURNS box4d
    AS 'MODULE_PATHNAME', 'gist_point4d_union'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_compress(internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_compress'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_decompress(internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_decompress'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_penalty(internal, internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_penalty'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_picksplit(internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_picksplit'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_same(box4d, box4d, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_same'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_distance(internal, point4d, smallint, oid, internal)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'gist_point4d_distance'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE OPERATOR CLASS point4d_gist_ops
    DEFAULT FOR TYPE point4d USING gist AS
        OPERATOR  1  <@ (point4d, box4d),
        OPERATOR  2  <-> (point4d, point4d) FOR ORDER BY float_ops,
        OPERATOR  3  <=> (point4d, point4d) FOR ORDER BY float_ops,
        FUNCTION  1  gist_point4d_consistent(internal, point4d, smallint, oid, internal),
        FUNCTION  2  gist_point4d_union(internal, internal),
        FUNCTION  3  gist_point4d_compress(internal),
        FUNCTION  4  gist_point4d_decompress(internal),
        FUNCTION  5  gist_point4d_penalty(internal, internal, internal),
        FUNCTION  6  gist_point4d_picksplit(internal, internal),
        FUNCTION  7  gist_point4d_same(box4d, box4d, internal),
        FUNCTION  8  (point4d, point4d) gist_point4d_distance(internal, point4d, smallint, oid, internal),
        STORAGE   box4d;

-- ── (6c) SP-GiST opclass for point4d (16-way quad-tree) ────────────────
CREATE FUNCTION spg_point4d_config(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_config'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_choose(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_choose'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_picksplit(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_picksplit'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_inner_consistent(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_inner_consistent'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_leaf_consistent(internal, internal) RETURNS bool
    AS 'MODULE_PATHNAME', 'spg_point4d_leaf_consistent'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE OPERATOR CLASS point4d_spgist_ops
    DEFAULT FOR TYPE point4d USING spgist AS
        OPERATOR  1  <@ (point4d, box4d),
        FUNCTION  1  spg_point4d_config(internal, internal),
        FUNCTION  2  spg_point4d_choose(internal, internal),
        FUNCTION  3  spg_point4d_picksplit(internal, internal),
        FUNCTION  4  spg_point4d_inner_consistent(internal, internal),
        FUNCTION  5  spg_point4d_leaf_consistent(internal, internal);

-- ── (7) Aggregates ─────────────────────────────────────────────────

-- centroid_4d (Euclidean mean) — uses internal-state aggregate with combine
-- and serialize/deserialize for parallel-safe execution.
CREATE FUNCTION centroid_4d_sfunc(internal, point4d) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_sfunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_4d_combine(internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_combine'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_4d_serialize(internal) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_serialize'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION centroid_4d_deserialize(bytea, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_deserialize'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION centroid_4d_ffunc(internal) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_ffunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_s3_ffunc(internal) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_centroid_s3_ffunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE AGGREGATE centroid_4d(point4d) (
    SFUNC      = centroid_4d_sfunc,
    STYPE      = internal,
    FINALFUNC  = centroid_4d_ffunc,
    COMBINEFUNC = centroid_4d_combine,
    SERIALFUNC = centroid_4d_serialize,
    DESERIALFUNC = centroid_4d_deserialize,
    PARALLEL = SAFE
);

CREATE AGGREGATE centroid_s3(point4d) (
    SFUNC      = centroid_4d_sfunc,
    STYPE      = internal,
    FINALFUNC  = centroid_s3_ffunc,
    COMBINEFUNC = centroid_4d_combine,
    SERIALFUNC = centroid_4d_serialize,
    DESERIALFUNC = centroid_4d_deserialize,
    PARALLEL = SAFE
);

-- bbox_4d uses box4d as state directly — no internal/serialize needed.
CREATE FUNCTION bbox_4d_sfunc(box4d, point4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_bbox_4d_sfunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION bbox_4d_combine(box4d, box4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_bbox_4d_combine'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE AGGREGATE bbox_4d(point4d) (
    SFUNC      = bbox_4d_sfunc,
    STYPE      = box4d,
    COMBINEFUNC = bbox_4d_combine,
    PARALLEL = SAFE
);

-- ── (8) Version, BLAKE3, traversal (preserved verbatim) ────────────

CREATE FUNCTION hartonomous_version()
RETURNS text
AS 'MODULE_PATHNAME', 'pg_hartonomous_version'
LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Returns runtime introspection: MKL version, thread pool sizes, the active
-- CBWR branch, and whether strict-determinism was requested at load. Lets
-- callers verify the determinism contract (Law #6) without parsing logs.
CREATE FUNCTION hartonomous_runtime_info(
    OUT mkl_version text,
    OUT mkl_max_threads int,
    OUT omp_max_threads int,
    OUT cbwr_branch int,
    OUT strict_determinism boolean
)
RETURNS record
AS 'MODULE_PATHNAME', 'pg_hartonomous_runtime_info'
LANGUAGE C VOLATILE PARALLEL RESTRICTED;

CREATE FUNCTION blake3_hash(bytea) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_blake3_hash'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION blake3_hash_text(text) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_blake3_hash_text'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

-- Hash-only result types (Phase C unification). substrate.entity has a
-- hash-only PK; classifications are junction metadata. Neighbors and
-- traversal_path carry hash-only handles. Edge identity stays composite —
-- edge_type IS structural.
CREATE TYPE neighbors_result AS (
    target_entity_hash bytea,
    edge_type_id       int,
    edge_hash          bytea,
    depth              int,
    path_ehashes       bytea[]
);

CREATE TYPE traversal_path AS (
    target_entity_hash bytea,
    depth              int,
    total_mu           double precision,
    path_ehashes       bytea[]
);

-- BFS expansion. Required: seed_entity_hash. Optional: edge_type_filter
-- (NULL = any edge type), max_hops (default 1).
CREATE FUNCTION neighbors(
    seed_entity_hash bytea,
    edge_type_filter int DEFAULT NULL,
    max_hops         int DEFAULT 1
)
    RETURNS SETOF neighbors_result
    AS 'MODULE_PATHNAME', 'pg_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

-- Glicko-2-rated A* over typed edges. Edge cost = 1 / edge_mu where edge_mu
-- is read via the COALESCE prior formula
--   mu = COALESCE(
--          edge_significance.mu,
--          provenance_edge_authority.initial_mu,
--          provenance.initial_mu * edge_type.semantic_weight * provenance.derivation_decay
--        )
-- total_mu in the result is 1/sum(1/mu_i), the path's aggregate trust score
-- in the requested arena.
CREATE FUNCTION traverse_astar(
    seed_entity_hash bytea,
    edge_type_filter int,
    arena_id         int,
    max_depth        int              DEFAULT 5,
    max_results      int              DEFAULT 100,
    p_min_mu         double precision DEFAULT NULL
)
    RETURNS SETOF traversal_path
    AS 'MODULE_PATHNAME', 'pg_traverse_astar'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

-- ── substrate.similarity_topk ───────────────────────────────────────────
-- Bounded-K nearest-neighbor scan over an arbitrary candidate query.
-- Distance kind dispatches by name to a substrate-side wrapper:
--   '4d'      → substrate.dist_4d(geometry, geometry)
--   's3'      → substrate.dist_s3(geometry, geometry)
--   'frechet' → substrate.frechet_4d_geom(geometry, geometry)
-- The candidate query MUST yield (entity_type_id int, entity_hash bytea, geom geometry).
-- Optional distance threshold filters per-candidate before the top-K cut.
CREATE OR REPLACE FUNCTION substrate.similarity_topk(
    p_seed_geom          geometry,
    p_k                  int,
    p_distance_kind      text,
    p_candidate_query    text,
    p_distance_threshold double precision DEFAULT NULL
) RETURNS TABLE (entity_type_id int, entity_hash bytea, distance double precision)
    AS 'MODULE_PATHNAME', 'pg_similarity_topk'
    LANGUAGE C STABLE STRICT;

-- ── substrate.recompose_walk ────────────────────────────────────────────
-- Iterative DFS over substrate.sequence starting at p_root_hash. Emits the
-- root first then descendants in left-to-right depth-first order. content_label
-- is always NULL — substrate.entity is hash-only; the C# layer joins content
-- (codepoint_value, classification, etc.) out-of-band.
CREATE OR REPLACE FUNCTION substrate.recompose_walk(
    p_root_hash bytea,
    p_max_depth int DEFAULT 16
) RETURNS TABLE (entity_hash bytea, ordinal_position int, content_label text, depth int)
    AS 'MODULE_PATHNAME', 'pg_recompose_walk'
    LANGUAGE C STABLE STRICT;


-- ═══════════════════════════════════════════════════════════════════════
-- (9) linestring4d — varlena polyline type for 4D trajectories
-- ═══════════════════════════════════════════════════════════════════════

CREATE TYPE linestring4d;

CREATE FUNCTION linestring4d_in(cstring) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_out(linestring4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_linestring4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_recv(internal) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_send(linestring4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_linestring4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE TYPE linestring4d (
    INTERNALLENGTH = variable,
    INPUT          = linestring4d_in,
    OUTPUT         = linestring4d_out,
    RECEIVE        = linestring4d_recv,
    SEND           = linestring4d_send,
    ALIGNMENT      = double,
    STORAGE        = extended
);

CREATE FUNCTION npoints(linestring4d) RETURNS integer
    AS 'MODULE_PATHNAME', 'pg_linestring4d_npoints'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point_n(linestring4d, integer) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_point_n'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION bbox(linestring4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_bbox'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_append(linestring4d, point4d) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_append'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION length_4d(linestring4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_linestring4d_length'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Bulk constructor: flat float8[] of length 4n → linestring4d with n vertices.
-- Canonical batch-insert path for the C# ingestion pipeline.
CREATE FUNCTION array_to_linestring4d(double precision[]) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_array_to_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Per-row binary constructor: bytea holding the linestring4d wire format
-- (int32 npoints BE, then 4n float8 BE) → linestring4d. Used by the C#
-- ingestion pipeline to write batches of variable-length linestrings via
-- INSERT ... SELECT FROM unnest($n::bytea[]) without flattening multidim
-- float8 arrays. Decode mirrors pg_linestring4d_recv exactly.
CREATE FUNCTION bytea_to_linestring4d(bytea) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_bytea_to_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ═══════════════════════════════════════════════════════════════════════
-- (10) Trajectory distances (Frechet, Hausdorff)
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION frechet_4d(linestring4d, linestring4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_frechet_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION hausdorff_4d(linestring4d, linestring4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_hausdorff_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ═══════════════════════════════════════════════════════════════════════
-- (11) Glicko-2 bulk update wrapper
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION glicko2_bulk_update(
    mu        double precision[],
    sigma     double precision[],
    vol       double precision[],
    opp_mu    double precision[],
    opp_sigma double precision[],
    score     double precision[],
    OUT new_mu        double precision[],
    OUT new_sigma     double precision[],
    OUT new_vol       double precision[]
) RETURNS record
    AS 'MODULE_PATHNAME', 'pg_glicko2_bulk_update'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ═══════════════════════════════════════════════════════════════════════
-- (12) Casts: point4d <-> double precision[4]
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION point4d_to_array(point4d) RETURNS double precision[]
    AS 'MODULE_PATHNAME', 'pg_point4d_to_array'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION array_to_point4d(double precision[]) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_array_to_point4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE CAST (point4d AS double precision[])
    WITH FUNCTION point4d_to_array(point4d) AS ASSIGNMENT;
CREATE CAST (double precision[] AS point4d)
    WITH FUNCTION array_to_point4d(double precision[]) AS ASSIGNMENT;

-- ═══════════════════════════════════════════════════════════════════════
-- (13) Domains: typed constraints for substrate columns
--   - unit_quaternion enforces ||q||=1 (S^3 membership)
--   - s3_arc_length enforces [0, pi]
--   - glicko_mu/sigma/vol enforce sane Glicko-2 parameter ranges
-- ═══════════════════════════════════════════════════════════════════════

CREATE DOMAIN unit_quaternion AS point4d
    CHECK (abs(norm_4d(VALUE) - 1.0) < 1e-9);

CREATE DOMAIN s3_arc_length AS double precision
    CHECK (VALUE >= 0.0 AND VALUE <= 3.14159265358979323846);

CREATE DOMAIN glicko_mu AS double precision
    DEFAULT 1500.0
    CHECK (VALUE >= 0.0 AND VALUE <= 4000.0);
CREATE DOMAIN glicko_sigma AS double precision
    DEFAULT 350.0
    CHECK (VALUE > 0.0 AND VALUE <= 700.0);
CREATE DOMAIN glicko_volatility AS double precision
    DEFAULT 0.06
    CHECK (VALUE > 0.0 AND VALUE <= 1.0);

-- ═══════════════════════════════════════════════════════════════════════
-- (14) Diagnostic views
-- ═══════════════════════════════════════════════════════════════════════

CREATE VIEW point4d_index_stats AS
SELECT
    n.nspname     AS schema_name,
    c.relname     AS index_name,
    t.relname     AS table_name,
    am.amname     AS index_type,
    c.relpages    AS pages,
    c.reltuples   AS approx_rows
FROM pg_class c
JOIN pg_index i ON c.oid = i.indexrelid
JOIN pg_class t ON i.indrelid = t.oid
JOIN pg_am am   ON c.relam = am.oid
JOIN pg_namespace n ON c.relnamespace = n.oid
WHERE am.amname IN ('gist', 'spgist')
  AND EXISTS (
      SELECT 1
      FROM pg_attribute a
      JOIN pg_type ty ON a.atttypid = ty.oid
      WHERE a.attrelid = i.indrelid
        AND ty.typname IN ('point4d', 'box4d')
  );

-- ═══════════════════════════════════════════════════════════════════════
-- (15) Concurrent reindex helper
-- ═══════════════════════════════════════════════════════════════════════

CREATE PROCEDURE reindex_point4d_concurrent(idx_name regclass)
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('REINDEX INDEX CONCURRENTLY %s', idx_name);
END;
$$;

-- hartonomous_geometry4d.sql — appended to hartonomous--1.0.sql by build.
--
-- Umbrella 4D geometry type and 10 SQL subtype DOMAINs. Each DOMAIN
-- pins a specific tag; automatic cast-to-umbrella is inherited from
-- the DOMAIN→base relationship. See pg_geometry4d.c for wire layout.

-- ── (16) geometry4d umbrella ────────────────────────────────────────
CREATE TYPE geometry4d;

CREATE FUNCTION geometry4d_in(cstring) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_out(geometry4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_geometry4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_recv(internal) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_send(geometry4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_geometry4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE TYPE geometry4d (
    INTERNALLENGTH = variable,
    INPUT          = geometry4d_in,
    OUTPUT         = geometry4d_out,
    RECEIVE        = geometry4d_recv,
    SEND           = geometry4d_send,
    ALIGNMENT      = double,
    STORAGE        = extended
);

-- Accessors & predicates
CREATE FUNCTION ST_TypeTag4D(geometry4d) RETURNS int4
    AS 'MODULE_PATHNAME', 'pg_geometry4d_tag' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_TypeName4D(geometry4d) RETURNS text
    AS 'MODULE_PATHNAME', 'pg_geometry4d_tag_name' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_SRID4D(geometry4d) RETURNS int4
    AS 'MODULE_PATHNAME', 'pg_geometry4d_srid' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_BBox4D(geometry4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_bbox' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_NumGeometries4D(geometry4d) RETURNS int4
    AS 'MODULE_PATHNAME', 'pg_geometry4d_num_geoms' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_NumPoints4D(geometry4d) RETURNS int8
    AS 'MODULE_PATHNAME', 'pg_geometry4d_num_points' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION geometry4d_eq(geometry4d, geometry4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_geometry4d_eq' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_ne(geometry4d, geometry4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_geometry4d_ne' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR = (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_eq,
    COMMUTATOR = =, NEGATOR = <>
);
CREATE OPERATOR <> (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_ne,
    COMMUTATOR = <>, NEGATOR = =
);

-- Constructors
CREATE FUNCTION ST_MakePoint4D(double precision, double precision, double precision, double precision)
    RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_makepoint'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION ST_MakeLine4D(point4d[]) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_makeline'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Casts to/from existing fixed-structure subtypes
CREATE FUNCTION cast_point4d_to_geometry4d(point4d) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_from_point4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION cast_geometry4d_to_point4d(geometry4d) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_to_point4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION cast_linestring4d_to_geometry4d(linestring4d) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_from_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION cast_geometry4d_to_linestring4d(geometry4d) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_to_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE CAST (point4d AS geometry4d)      WITH FUNCTION cast_point4d_to_geometry4d(point4d)      AS IMPLICIT;
CREATE CAST (geometry4d AS point4d)      WITH FUNCTION cast_geometry4d_to_point4d(geometry4d)   AS ASSIGNMENT;
CREATE CAST (linestring4d AS geometry4d) WITH FUNCTION cast_linestring4d_to_geometry4d(linestring4d) AS IMPLICIT;
CREATE CAST (geometry4d AS linestring4d) WITH FUNCTION cast_geometry4d_to_linestring4d(geometry4d)   AS ASSIGNMENT;

-- ── (17) 10 subtype DOMAINs ────────────────────────────────────────
-- Each DOMAIN is a column-usable distinct SQL type pinned to one tag and
-- automatically cast-equivalent with geometry4d via the DOMAIN → base
-- relationship. See docs/specs/native/4d-type-and-index.md §subtype-domains.

CREATE DOMAIN point4d_g             AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 1);
CREATE DOMAIN linestring4d_g        AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 2);
CREATE DOMAIN polygon4d             AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 3);
CREATE DOMAIN multipoint4d          AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 4);
CREATE DOMAIN multilinestring4d     AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 5);
CREATE DOMAIN multipolygon4d        AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 6);
CREATE DOMAIN triangle4d            AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 7);
CREATE DOMAIN tin4d                 AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 8);
CREATE DOMAIN polyhedralsurface4d   AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 9);
CREATE DOMAIN geometrycollection4d  AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 10);

COMMENT ON DOMAIN point4d_g IS
  'geometry4d pinned to tag POINT4D. Distinct column type; casts implicitly to/from geometry4d.';
COMMENT ON DOMAIN linestring4d_g IS
  'geometry4d pinned to tag LINESTRING4D.';
COMMENT ON DOMAIN polygon4d IS
  'geometry4d pinned to tag POLYGON4D; stored as one outer ring plus zero or more inner rings, each closed.';

-- ── (18) GiST opclass for geometry4d ───────────────────────────────
CREATE FUNCTION gist_geometry4d_consistent(internal, geometry4d, smallint, oid, internal) RETURNS boolean
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_union(internal, internal) RETURNS box4d
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_compress(internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_decompress(internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_penalty(internal, internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_picksplit(internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_same(box4d, box4d, internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- bbox-based operators between two geometry4d values. Operators reuse
-- box4d operator infrastructure: each performs g4d_compute_bbox on both
-- sides and delegates to the box4d primitive.
CREATE FUNCTION geometry4d_overlaps_geometry4d(geometry4d, geometry4d) RETURNS boolean
    AS $$ SELECT box4d_overlaps(ST_BBox4D($1), ST_BBox4D($2)) $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_contains_geometry4d(geometry4d, geometry4d) RETURNS boolean
    AS $$ SELECT box4d_contains_box(ST_BBox4D($1), ST_BBox4D($2)) $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_contained_by_geometry4d(geometry4d, geometry4d) RETURNS boolean
    AS $$ SELECT box4d_contains_box(ST_BBox4D($2), ST_BBox4D($1)) $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR && (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_overlaps_geometry4d,
    COMMUTATOR = &&
);
CREATE OPERATOR @> (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_contains_geometry4d,
    COMMUTATOR = <@
);
CREATE OPERATOR <@ (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_contained_by_geometry4d,
    COMMUTATOR = @>
);

CREATE OPERATOR CLASS geometry4d_gist_ops
    DEFAULT FOR TYPE geometry4d USING gist AS
        OPERATOR        1       && ,
        OPERATOR        2       @> ,
        OPERATOR        3       <@ ,
        OPERATOR        4       =  ,
        FUNCTION        1       gist_geometry4d_consistent (internal, geometry4d, smallint, oid, internal),
        FUNCTION        2       gist_geometry4d_union (internal, internal),
        FUNCTION        3       gist_geometry4d_compress (internal),
        FUNCTION        4       gist_geometry4d_decompress (internal),
        FUNCTION        5       gist_geometry4d_penalty (internal, internal, internal),
        FUNCTION        6       gist_geometry4d_picksplit (internal, internal),
        FUNCTION        7       gist_geometry4d_same (box4d, box4d, internal),
        STORAGE         box4d ;

-- ── (19) SP-GiST quadtree opclass for geometry4d ───────────────────
CREATE FUNCTION spg_geometry4d_config(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_choose(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_picksplit(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_inner_consistent(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_leaf_consistent(internal, internal) RETURNS boolean
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR CLASS geometry4d_spgist_ops
    FOR TYPE geometry4d USING spgist AS
        OPERATOR        1       && ,
        OPERATOR        2       @> ,
        OPERATOR        3       <@ ,
        OPERATOR        4       =  ,
        FUNCTION        1       spg_geometry4d_config(internal, internal),
        FUNCTION        2       spg_geometry4d_choose(internal, internal),
        FUNCTION        3       spg_geometry4d_picksplit(internal, internal),
        FUNCTION        4       spg_geometry4d_inner_consistent(internal, internal),
        FUNCTION        5       spg_geometry4d_leaf_consistent(internal, internal);

-- ═══════════════════════════════════════════════════════════════════════
-- (20) Native text decomposition — UAX #29 + BLAKE3 chains + 4D centroids
--
-- Replaces the per-codepoint C# loop in CanonicalTextDecomposer.Emit with
-- a single C function that does the whole decomposition tree in one
-- compiled pass: UTF-8 decode, codepoint property lookup (cached per
-- backend), UAX #29 grapheme + word boundary detection, batched BLAKE3
-- chain hashing via libhartonomous, S^3 centroid math, and SPI INSERTs
-- into substrate.staging_*.
--
-- text_decompose_batch processes N texts concurrently across CPU cores
-- via #pragma omp parallel for. Determinism via MKL CBWR + UCD-table
-- property lookups (Law #6).
-- ═══════════════════════════════════════════════════════════════════════
-- 9-field summary: 7 counts + root composition hash + root entity_type_id.
-- The root fields let C# callers immediately wire downstream edges
-- (has_text, has_gloss, has_example, has_name, has_token_string, etc.)
-- without recomputing the BLAKE3 themselves. Empty-input → root NULL.
CREATE TYPE substrate.text_decompose_summary AS (
    entity_count          BIGINT,
    edge_count            BIGINT,
    edge_member_count     BIGINT,
    physicality_count     BIGINT,
    sequence_count        BIGINT,
    significance_count    BIGINT,
    classification_count  BIGINT,
    root_hash             bytea,
    root_entity_type_id   INT
);

-- text_decompose now writes DIRECTLY to substrate.entity / entity_classification
-- / physicality / sequence / entity_significance with ON CONFLICT DO NOTHING.
-- No staging detour. p_model_source_id is OPTIONAL: when supplied, the root
-- composition entity gets an entity_model_source row pointing at that source.
CREATE FUNCTION substrate.text_decompose(
    p_utf8                  bytea,
    p_top_entity_type_code  text,
    p_trust_mu              double precision,
    p_provenance_code       text,
    p_model_source_id       int DEFAULT NULL
) RETURNS substrate.text_decompose_summary
    AS 'MODULE_PATHNAME', 'pg_text_decompose'
    LANGUAGE C VOLATILE;

CREATE FUNCTION substrate.text_decompose_batch(
    p_utf8s                  bytea[],
    p_top_entity_type_codes  text[],
    p_trust_mus              double precision[],
    p_provenance_codes       text[],
    p_model_source_ids       int[] DEFAULT NULL
) RETURNS substrate.text_decompose_summary
    AS 'MODULE_PATHNAME', 'pg_text_decompose_batch'
    LANGUAGE C VOLATILE;

COMMENT ON FUNCTION substrate.text_decompose(bytea, text, double precision, text, int) IS
    'Native UAX #29 + BLAKE3 + 4D centroid pipeline. Decodes UTF-8, runs grapheme + word boundary detection from the embedded UCD blob, emits codepoint/grapheme_cluster/word_form/composition entities + sequence + physicality + significance rows DIRECTLY into substrate core tables (no staging) via SPI with ON CONFLICT DO NOTHING. When p_model_source_id is non-NULL, the root composition is also linked via substrate.entity_model_source. Returns counts + root_hash + root_entity_type_id so callers can wire downstream edges without recomputing BLAKE3.';

COMMENT ON FUNCTION substrate.text_decompose_batch(bytea[], text[], double precision[], text[], int[]) IS
    'Batched variant: processes N texts in one SQL invocation, recursing into text_decompose per row. Per-row optional p_model_source_ids[i] parameter — NULL element skips linkage. Returns aggregated counts only; root_hash/root_entity_type_id are always NULL for the batch form (call text_decompose() one at a time when per-row roots are needed).';

-- ═══════════════════════════════════════════════════════════════════════
-- (21) Tier-0 codepoint atoms — embedded UCD/UCA, O(1) array lookups
--
-- All Unicode property data for the 1,114,112 codepoints is baked into
-- the extension at build time from UCD 17.0.0. Lookups are flat array
-- accesses — no SPI, no DB JOIN, no runtime computation. Codepoint
-- BLAKE3 hashes, S^3 centroids, and Hilbert indices are precomputed.
-- substrate.cp_from_hash provides the inverse mapping for hash
-- deconstruction during inference / recompose.
--
-- Determinism (Law #6): UCD version pinned at extension build time.
-- substrate.ucd_version() returns the pinned version string.
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION substrate.cp_hash(cp int) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_cp_hash' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_centroid(cp int) RETURNS public.point4d
    AS 'MODULE_PATHNAME', 'pg_cp_centroid' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_hilbert(cp int) RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_cp_hilbert' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_from_hash(h bytea) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_from_hash' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.cp_gcb(cp int)  RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_gcb'  LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_wb(cp int)   RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_wb'   LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_sb(cp int)   RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_sb'   LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_lb(cp int)   RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_lb'   LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_incb(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_incb' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_extended_pictographic(cp int) RETURNS bool
    AS 'MODULE_PATHNAME', 'pg_cp_extended_pictographic' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_general_category(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_general_category' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_ccc(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_ccc' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_script(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_script' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_block(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_block' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_uppercase(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_uppercase' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_lowercase(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_lowercase' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_titlecase(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_titlecase' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_case_fold(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_case_fold' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_index(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_uca_index' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_total() RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_uca_total' LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION substrate.ucd_version() RETURNS text
    AS 'MODULE_PATHNAME', 'pg_ucd_version' LANGUAGE C IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION substrate.cp_hash(int) IS
    'O(1) precomputed BLAKE3 hash of the codepoint (big-endian 4-byte rune). Tier-0 atom — frozen at extension build time, UCD-version-pinned.';
COMMENT ON FUNCTION substrate.cp_centroid(int) IS
    'O(1) precomputed 4D Super-Fibonacci centroid on S^3 anchored by UCA-sorted index. Tier-0 atom.';
COMMENT ON FUNCTION substrate.cp_from_hash(bytea) IS
    'Inverse of substrate.cp_hash — given a 32-byte BLAKE3 hash, return the codepoint value, or NULL if no codepoint produces that hash. O(log N) binary search over the embedded sorted-by-hash table.';
COMMENT ON FUNCTION substrate.ucd_version() IS
    'UCD version pinned into the extension at build time. Determinism gate: same UCD version → byte-identical tier-0 atoms forever.';

CREATE FUNCTION substrate.cp_x(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_x' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_y(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_y' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_z(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_z' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_m(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_m' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION substrate.cp_x(int) IS 'Codepoint S^3 X coordinate. Combine with cp_y/z/m + ST_MakePoint to build POINTZM.';

-- ── (22) Extended UCD/UCA accessors — full catalog from generated tables ──
-- Bidi class, East-Asian width, Hangul syllable type, numeric type,
-- decomposition type. All O(1) array loads.
CREATE FUNCTION substrate.cp_bidi(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_bidi' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_eaw(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_eaw' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_hsy(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_hsy' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_num_type(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_num_type' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_decomp_type(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_decomp_type' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Variable-length per-codepoint payloads. Empty arrays (NOT NULL) for the
-- common case; pg_cp_name returns NULL for unnamed codepoints.
CREATE FUNCTION substrate.cp_decomp(cp int) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_decomp' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_full_case_fold(cp int) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_full_case_fold' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_weights(cp int) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_uca_weights' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_name(cp int) RETURNS text
    AS 'MODULE_PATHNAME', 'pg_cp_name' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (23) SETOF inventory accessors — drive reference-table population ────
-- Return shapes match the per-inventory struct in pg_unicode_inventory.h.
CREATE FUNCTION substrate.ucd_general_categories(
    OUT id int, OUT code text, OUT description text, OUT group_code text
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_general_categories'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_scripts(
    OUT id int, OUT code text
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_scripts'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_blocks(
    OUT id int, OUT code text, OUT range_start int, OUT range_end int
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_blocks'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_break_properties(
    OUT id int, OUT category text, OUT code text, OUT enum_id int
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_break_properties'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION substrate.ucd_general_categories() IS
    'Inventory of 30 UCD General_Category values from the embedded extension catalog (code, long description, top-level group L/M/N/P/S/Z/C). Drives substrate.populate_general_categories_from_ext().';
COMMENT ON FUNCTION substrate.ucd_scripts() IS
    'Inventory of 175 UCD Script values from the embedded extension catalog. Drives substrate.populate_scripts_from_ext().';
COMMENT ON FUNCTION substrate.ucd_blocks() IS
    'Inventory of 347 UCD Block values from the embedded extension catalog with explicit range_start/range_end. Drives substrate.populate_blocks_from_ext().';
COMMENT ON FUNCTION substrate.ucd_break_properties() IS
    'Inventory of 101 break-property enums (GCB/WB/SB/LB) from the embedded extension catalog with explicit category column. Drives substrate.populate_break_properties_from_ext().';

-- ── (24) Codepoint domain + composite atom type + bulk SRFs ──────────────
-- The codepoint domain bounds-checks at the type-system level so callers
-- get a clear constraint violation instead of an in-function ereport, and
-- so the planner can use the CHECK for partition pruning when columns are
-- typed substrate.codepoint instead of plain INT.
CREATE DOMAIN substrate.codepoint AS int
    CHECK (VALUE >= 0 AND VALUE <= 1114111);

-- 30-column composite covering the entire per-codepoint record,
-- including variable-length payloads (decomposition_mapping,
-- full_case_fold). Bulk consumers SELECT FROM substrate.ucd_codepoints()
-- and read the array columns directly — never call substrate.cp_decomp /
-- substrate.cp_full_case_fold per row, which scales as 2.2M scalar SPI
-- C invocations and is fragile under heavy executor pressure.
CREATE TYPE substrate.codepoint_atom AS (
    cp                    int,
    hash                  bytea,
    x                     double precision,
    y                     double precision,
    z                     double precision,
    m                     double precision,
    hilbert               bigint,
    gcb                   int,
    wb                    int,
    sb                    int,
    lb                    int,
    incb                  int,
    general_category      int,
    ccc                   int,
    script                int,
    block                 int,
    simple_uppercase      int,
    simple_lowercase      int,
    simple_titlecase      int,
    simple_case_fold      int,
    uca_index             int,
    bidi                  int,
    eaw                   int,
    hsy                   int,
    num_type              int,
    decomp_type           int,
    extended_pictographic boolean,
    name                  text,
    decomposition_mapping int[],
    full_case_fold        int[]
);

CREATE FUNCTION substrate.cp_atom(cp int) RETURNS substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_cp_atom' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Bulk SRF over the entire UCD plane, or a slice. Default args emit all
-- 1,114,112 codepoints. Use this for INSERT INTO substrate.entity from
-- the extension catalog — single C call, no per-cp function invocation.
CREATE FUNCTION substrate.ucd_codepoints(
    "start" int DEFAULT 0,
    "count" int DEFAULT 1114112
) RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

-- Predicate-pushdown SRFs. The predicate is evaluated inside C against
-- the embedded array — no SQL-side filter, no row materialization for
-- non-matches.
CREATE FUNCTION substrate.ucd_codepoints_in_block(block_id int)
    RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints_in_block'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_codepoints_in_script(script_id int)
    RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints_in_script'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_codepoints_with_gc(gc_id int)
    RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints_with_gc'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (25) Bulk hash array helpers ─────────────────────────────────────────
CREATE FUNCTION substrate.cp_hashes(cps int[]) RETURNS bytea[]
    AS 'MODULE_PATHNAME', 'pg_cp_hashes'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_from_hashes(hashes bytea[]) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_from_hashes'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION substrate.cp_hashes(int[]) IS
    'Vectorized per-cp hash lookup. One C call per call regardless of array length; out-of-range elements are NULL.';
COMMENT ON FUNCTION substrate.cp_from_hashes(bytea[]) IS
    'Vectorized hash → codepoint reverse. NULL for unknown hashes. Uses the embedded sorted-by-hash table.';

-- ── (26) UCA sort key + collation operator class ─────────────────────────
-- substrate.uca_sort_key(text) returns a binary key suitable for ORDER BY.
-- Replaces ICU COLLATE for substrate-internal ordering — pure C array walk
-- against the embedded UCA 17.0.0 weight blob.
CREATE FUNCTION substrate.uca_sort_key(s text) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_uca_sort_key'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Codepoint-level UCA comparator and btree opclass. Lets SQL do
--   ORDER BY cp USING OPERATOR(substrate.uca_lt)
-- without dragging COLLATE through every query. The opclass is btree-only
-- and keyed on int (so a substrate.codepoint column slots in directly).
CREATE FUNCTION substrate.cp_uca_compare(a int, b int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_uca_compare'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.cp_uca_lt(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) <  0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_le(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) <= 0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_eq(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) =  0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_ge(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) >= 0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_gt(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) >  0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR substrate.<#  (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_lt, COMMUTATOR = >#);
CREATE OPERATOR substrate.<=# (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_le, COMMUTATOR = >=#);
CREATE OPERATOR substrate.=#  (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_eq, COMMUTATOR = =#);
CREATE OPERATOR substrate.>=# (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_ge, COMMUTATOR = <=#);
CREATE OPERATOR substrate.>#  (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_gt, COMMUTATOR = <#);

CREATE OPERATOR CLASS substrate.cp_uca_ops
    FOR TYPE int USING btree AS
        OPERATOR 1 substrate.<#,
        OPERATOR 2 substrate.<=#,
        OPERATOR 3 substrate.=#,
        OPERATOR 4 substrate.>=#,
        OPERATOR 5 substrate.>#,
        FUNCTION 1 substrate.cp_uca_compare(int, int);

COMMENT ON OPERATOR CLASS substrate.cp_uca_ops USING btree IS
    'Btree opclass keyed on int (or substrate.codepoint) that sorts by UCA-derived position from the embedded catalog. Use as ORDER BY cp USING OPERATOR(substrate.<#) or via index opclass on a codepoint column.';

-- ── (27) Inventory views over the SRFs ───────────────────────────────────
CREATE VIEW substrate.v_general_category   AS SELECT * FROM substrate.ucd_general_categories();
CREATE VIEW substrate.v_script             AS SELECT * FROM substrate.ucd_scripts();
CREATE VIEW substrate.v_block              AS SELECT * FROM substrate.ucd_blocks();
CREATE VIEW substrate.v_break_property     AS SELECT * FROM substrate.ucd_break_properties();
CREATE VIEW substrate.v_codepoint_atom     AS SELECT * FROM substrate.ucd_codepoints();

COMMENT ON VIEW substrate.v_codepoint_atom IS
    '1,114,112-row view over the embedded UCD/UCA 17.0.0 catalog. Each row is a complete codepoint atom (id, hash, 4D centroid, hilbert, all enum/case properties, name). Materialized at query time via a single C SRF call.';

-- ── (28) Case folding via embedded full-case-fold blob ──────────────────
CREATE FUNCTION substrate.case_fold_text(s text) RETURNS text
    AS 'MODULE_PATHNAME', 'pg_case_fold_text'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION substrate.case_fold_text(text) IS
    'Full Unicode case fold using the embedded UCD CaseFolding.txt mapping. Multi-codepoint expansions (German ß → ss, etc.) handled correctly. Drop-in for lower(text COLLATE "und-x-icu") in substrate-internal paths.';


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

-- ── sql/schema/functions/resolve_entity_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[], TEXT[]);
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.resolve_entity_handles(
    p_hashes BYTEA[]
) RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash FROM unnest(p_hashes) AS in_(h) JOIN substrate.entity e ON e.hash = in_.h;
$f$;

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
    INSERT INTO substrate.language (code, name, scope, type)
    SELECT
        code,
        name,
        scope::CHAR(1),
        type::CHAR(1)
    FROM unnest(p_codes, p_names, p_scopes, p_types) AS t(code, name, scope, type)
    ON CONFLICT (code) DO UPDATE
        SET name  = EXCLUDED.name,
            scope = EXCLUDED.scope,
            type  = EXCLUDED.type;
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

-- ── sql/schema/functions/populate_senses.sql ───────────────────────────────────────
-- substrate.populate_senses — DEPRECATED no-op.
--
-- The substrate.sense reference table was removed (sense_keys are content,
-- not bounded vocabulary). word_sense rows live in substrate.entity now,
-- content-hashed via BLAKE3 of (lemma_hash || synset_hash || lexname_id ||
-- lex_id), and lemma↔sense binding is the has_sense edge in the substrate.
--
-- This stub remains because src/Hartonomous.Engine/Data/NpgsqlReferenceDataWriter.cs
-- still calls populate_senses against PG and a missing function would
-- break the WordNet decomposer. The stub accepts the same arguments and
-- silently returns; the actual sense_key content travels via has_sense
-- edge emission in WordNetDecomposer.
CREATE OR REPLACE FUNCTION substrate.populate_senses(
    p_codes       TEXT[],
    p_glosses     TEXT[],
    p_lexname_ids INT[],
    p_pos_ids     INT[]
) RETURNS VOID
LANGUAGE sql IMMUTABLE
AS $$
    SELECT NULL::void;
$$;

COMMENT ON FUNCTION substrate.populate_senses(TEXT[], TEXT[], INT[], INT[]) IS
    'No-op: substrate.sense was removed (Phase C). Function retained as a stub for legacy callers in NpgsqlReferenceDataWriter pending C# AP-2 cleanup.';

-- ── sql/schema/functions/load_wordnet_offset_synset_map.sql ───────────────────────────────────────
-- Bridge function for OMW (and any cross-lexicon decomposer) to resolve
-- WordNet synsets by their authoring offset string. Returns one row per
-- has_wordnet_offset edge: (offset_doc_hash, synset_hash). Callers compute
-- the offset_doc_hash via BLAKE3 of the canonical offset string ("XXXXXXXX-p")
-- and look up the substrate's content-pure synset hash from the result map.
--
-- Why this exists: synset identity is content-pure (BLAKE3 Merkle of sorted
-- member lemma hashes + gloss byte hash). The WordNet offset is placement
-- metadata recorded as substrate content via has_wordnet_offset edges, NOT
-- baked into the synset's identity hash. This function exposes the bridge
-- in one round-trip so downstream decomposers can resolve synsets by their
-- external authoring identifier without recomputing content hashes.
CREATE OR REPLACE FUNCTION substrate.load_wordnet_offset_synset_map()
RETURNS TABLE(offset_doc_hash BYTEA, synset_hash BYTEA)
LANGUAGE sql
AS $$
    SELECT
        em_target.entity_hash AS offset_doc_hash,
        em_source.entity_hash AS synset_hash
    FROM substrate.edge_member em_source
    JOIN substrate.edge e
        ON  e.edge_type_id = em_source.edge_type_id
        AND e.hash         = em_source.edge_hash
    JOIN substrate.edge_member em_target
        ON  em_target.edge_type_id = em_source.edge_type_id
        AND em_target.edge_hash    = em_source.edge_hash
    JOIN substrate.edge_role rs
        ON rs.id = em_source.edge_role_id AND rs.code = 'source'
    JOIN substrate.edge_role rt
        ON rt.id = em_target.edge_role_id AND rt.code = 'target'
    WHERE e.edge_type_id = (
        SELECT id FROM substrate.edge_type WHERE code = 'has_wordnet_offset'
    );
$$;

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

-- ── sql/schema/functions/geom_bridge_4d.sql ───────────────────────────────────────
-- ============================================================================
-- Substrate 4D operator surface — subtype-aware bridge between PostGIS
-- GeometryZM storage and libhartonomous native compute.
-- ============================================================================
-- Storage is universal: substrate.physicality.geom is geometry(GeometryZM),
-- accepting the full GeometryZM subtype family (POINTZM, LINESTRINGZM,
-- MULTILINESTRINGZM, POLYGONZM, MULTIPOLYGONZM, MULTIPOINTZM,
-- GEOMETRYCOLLECTIONZM). Per-partition CHECK constraints declare which
-- subtype(s) and which axis semantics each physicality_type uses.
--
-- Compute lives in libhartonomous via the C extension. Two native primitives
-- carry all the load:
--   public.distance_4d(point4d, point4d) → 4D Euclidean
--   public.frechet_4d(linestring4d, linestring4d) → discrete Fréchet
--   public.hausdorff_4d(linestring4d, linestring4d) → symmetric Hausdorff
-- (point4d / linestring4d are internal native compute primitives, NOT
-- substrate-level types. They exist so the C kernels can take a flat
-- (x,y,z,m) sequence with zero PostGIS marshalling overhead.)
--
-- The substrate-side operators below dispatch on GeometryType and route to
-- the appropriate native primitive while preserving subtype structure:
--   * POINT-vs-POINT     → distance_4d
--   * LINESTRING-vs-LINESTRING → frechet_4d / hausdorff_4d on the linestring
--   * MULTILINESTRING    → minimum across pairwise component frechet
--   * POLYGON            → exterior ring as the structural trajectory
--   * MULTIPOLYGON       → minimum across pairwise component frechet
--   * GEOMETRYCOLLECTION → minimum across all component pairs
--   * MULTIPOINT         → Hausdorff (Fréchet undefined on unordered sets)
--   * Cross-shape pairs  → representative-point or vertex-stream fallback
--
-- This is explicitly NOT "ST_DumpPoints flatten everything" — that approach
-- loses subtype structural distinction (ring concatenation in polygons,
-- branch concatenation in multilinestrings, etc.) and produces wrong answers
-- for non-trivial subtype combinations.

-- ────────────────────────────────────────────────────────────────────────────
-- Helper: walk one geometry's vertex stream into a native linestring4d.
-- Used by dispatch arms that genuinely DO want the flat sequence (LINESTRING
-- treated as a single trajectory, MULTIPOINT treated as an unordered set for
-- Hausdorff). Callers that need subtype structure preserved must dispatch
-- on GeometryType BEFORE building the linestring4d.
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.geom_to_linestring4d(geometry);
CREATE OR REPLACE FUNCTION substrate.geom_to_linestring4d(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.array_to_linestring4d(
        ARRAY(
            SELECT v
            FROM ST_DumpPoints(g) AS d,
                 LATERAL (
                     VALUES
                         (COALESCE(ST_X(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_Y(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_Z(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_M(d.geom), 0)::DOUBLE PRECISION)
                 ) AS f(v)
            ORDER BY d.path, f.v   -- depth-first vertex order, 4 floats per vertex
        )
    );
$$;

COMMENT ON FUNCTION substrate.geom_to_linestring4d(geometry) IS
    'Walk one geometry depth-first into a flat (x,y,z,m) sequence packed as a native linestring4d. Used by dispatch arms that legitimately want the flat sequence (LINESTRINGZM trajectory, MULTIPOINTZM scatter). Callers needing subtype structure (POLYGON rings, MULTILINESTRING branches) must dispatch BEFORE calling this — flattening loses structure.';

-- ────────────────────────────────────────────────────────────────────────────
-- Helper: extract POLYGON exterior ring as a linestring4d. The exterior ring
-- IS the polygon's structural trajectory for Fréchet purposes. Holes (interior
-- rings) are placement metadata, not part of the boundary shape.
-- ────────────────────────────────────────────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.polygon_exterior_linestring4d(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT substrate.geom_to_linestring4d(ST_ExteriorRing(g));
$$;

COMMENT ON FUNCTION substrate.polygon_exterior_linestring4d(geometry) IS
    'Extract a POLYGONZM''s exterior ring as a linestring4d for boundary-shape comparison. Interior rings (holes) are excluded — they are placement metadata, not boundary structure.';

-- ────────────────────────────────────────────────────────────────────────────
-- substrate.dist_4d(g1, g2) — primary subtype-dispatching distance.
-- Returns a meaningful number for every subtype × subtype pair. NULL only
-- when at least one operand is empty.
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.dist_4d(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    -- Fast path: POINT-vs-POINT pure 4D Euclidean.
    IF t1 = 'ST_Point' AND t2 = 'ST_Point' THEN
        RETURN public.distance_4d(
            public.point4d(ST_X(g1), ST_Y(g1), COALESCE(ST_Z(g1), 0), COALESCE(ST_M(g1), 0)),
            public.point4d(ST_X(g2), ST_Y(g2), COALESCE(ST_Z(g2), 0), COALESCE(ST_M(g2), 0)));
    END IF;

    -- Same-shape LINESTRING: discrete Fréchet on the trajectory.
    IF t1 = 'ST_LineString' AND t2 = 'ST_LineString' THEN
        RETURN public.frechet_4d(
            substrate.geom_to_linestring4d(g1),
            substrate.geom_to_linestring4d(g2));
    END IF;

    -- Same-shape POLYGON: Fréchet on the exterior rings (boundary shape).
    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.frechet_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    -- Same-shape MULTILINESTRING / MULTIPOLYGON: minimum component-pair
    -- Fréchet. Each branch / ring is a separate trajectory; cross-branch
    -- vertex concatenation would invent shape that isn't there.
    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MIN(public.frechet_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- MULTIPOINT-vs-MULTIPOINT: Hausdorff (Fréchet is undefined on unordered
    -- sets). Treats both inputs as scatter clouds.
    IF t1 = 'ST_MultiPoint' AND t2 = 'ST_MultiPoint' THEN
        RETURN public.hausdorff_4d(
            substrate.geom_to_linestring4d(g1),
            substrate.geom_to_linestring4d(g2));
    END IF;

    -- Cross-shape with at least one POINT: minimum 4D distance from the
    -- point to every vertex of the other geometry. Not Fréchet — that's
    -- not defined point-to-trajectory.
    IF t1 = 'ST_Point' THEN
        RETURN (
            SELECT MIN(public.distance_4d(
                       public.point4d(ST_X(g1), ST_Y(g1), COALESCE(ST_Z(g1), 0), COALESCE(ST_M(g1), 0)),
                       public.point4d(ST_X(d.geom), ST_Y(d.geom), COALESCE(ST_Z(d.geom), 0), COALESCE(ST_M(d.geom), 0))))
              FROM ST_DumpPoints(g2) d
        );
    END IF;
    IF t2 = 'ST_Point' THEN
        RETURN (
            SELECT MIN(public.distance_4d(
                       public.point4d(ST_X(d.geom), ST_Y(d.geom), COALESCE(ST_Z(d.geom), 0), COALESCE(ST_M(d.geom), 0)),
                       public.point4d(ST_X(g2), ST_Y(g2), COALESCE(ST_Z(g2), 0), COALESCE(ST_M(g2), 0))))
              FROM ST_DumpPoints(g1) d
        );
    END IF;

    -- GEOMETRYCOLLECTION on either side: dispatch component-by-component
    -- and return the minimum pairwise distance.
    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MIN(substrate.dist_4d(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- Fallback: vertex-stream Fréchet. Triggered for combinations like
    -- LINESTRING-vs-POLYGON, MULTILINESTRING-vs-POLYGON, etc., where the
    -- structural answer is "compare boundary trajectories." Caller can
    -- dispatch differently if it needs a stricter shape semantic.
    RETURN public.frechet_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry, geometry) IS
    'Subtype-dispatching 4D distance over GeometryZM. POINT/LINESTRING/POLYGON/MULTI*/COLLECTION pairs each route to the structurally appropriate native primitive (distance_4d, frechet_4d, hausdorff_4d, or component-wise minimum). Cross-shape pairs are explicitly handled. Substrate-side does no compute itself; libhartonomous via the C extension does the math.';

-- ────────────────────────────────────────────────────────────────────────────
-- substrate.frechet_4d_geom(g1, g2) — explicit Fréchet, subtype-aware.
-- Same dispatch principles as dist_4d but always returns a Fréchet value
-- (errors on subtype combinations where Fréchet is undefined, e.g. MULTIPOINT
-- — caller should use hausdorff_4d_geom instead).
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.frechet_4d_geom(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.frechet_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    IF t1 = 'ST_MultiPoint' OR t2 = 'ST_MultiPoint' THEN
        RAISE EXCEPTION 'frechet_4d_geom: Fréchet is undefined on MULTIPOINTZM (unordered set). Use substrate.hausdorff_4d_geom for scatter-cloud comparison.';
    END IF;

    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.frechet_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MIN(public.frechet_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MIN(substrate.frechet_4d_geom(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
              WHERE ST_GeometryType(c1.geom) <> 'ST_MultiPoint'
                AND ST_GeometryType(c2.geom) <> 'ST_MultiPoint'
        );
    END IF;

    RETURN public.frechet_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.frechet_4d_geom(geometry, geometry) IS
    'Subtype-aware discrete Fréchet over GeometryZM. POLYGONZM uses exterior-ring trajectory; MULTI* uses minimum across component pairs; GEOMETRYCOLLECTIONZM dispatches per-component. Errors on MULTIPOINTZM (Fréchet undefined on unordered sets — use hausdorff_4d_geom).';

-- ────────────────────────────────────────────────────────────────────────────
-- substrate.hausdorff_4d_geom(g1, g2) — symmetric Hausdorff. Defined for all
-- subtype combinations including MULTIPOINTZM.
-- ────────────────────────────────────────────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.hausdorff_4d_geom(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.hausdorff_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    -- POLYGON: compare exterior rings.
    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.hausdorff_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    -- MULTI* same-shape: maximum across components (Hausdorff is a max-metric).
    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MAX(public.hausdorff_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- GEOMETRYCOLLECTION: dispatch per-component, take the maximum.
    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MAX(substrate.hausdorff_4d_geom(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    -- Default (POINT, LINESTRING, MULTIPOINT, cross-shape): flatten and run
    -- native hausdorff_4d. Hausdorff tolerates flattening better than Fréchet
    -- because it's max-distance-of-min-distance over both sets.
    RETURN public.hausdorff_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.hausdorff_4d_geom(geometry, geometry) IS
    'Subtype-aware symmetric Hausdorff over GeometryZM. POLYGONZM uses exterior-ring; MULTI* takes maximum across component pairs (Hausdorff is a max-metric); GEOMETRYCOLLECTIONZM dispatches per-component. Defined for all subtypes including MULTIPOINTZM scatter clouds.';

-- ── sql/schema/functions/entity_centroid_4d.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_hash BYTEA
) RETURNS geometry(GeometryZM)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT geom FROM substrate.physicality
     WHERE entity_hash = p_entity_hash
     ORDER BY physicality_type_id LIMIT 1;
$f$;

-- ── sql/schema/functions/populate_edge_trajectories_v2.sql ───────────────────────────────────────
-- substrate.populate_edge_trajectories(p_limit INT)
--
-- Walks edges with NULL geom and populates each edge's geom column with a
-- LINESTRINGZM through its participants' 4D centroids in role order. For
-- edges with only one valid centroid, geom is the centroid POINTZM.
--
-- Set-based UPDATE — no plpgsql FOR LOOP, no per-row roundtrip. The
-- per-edge centroid aggregation runs as a single GROUP BY scan; PG's
-- executor parallelises across partitions of substrate.edge_member where
-- safe. substrate.entity_centroid_4d (the per-entity centroid lookup) is
-- itself a SQL function that calls native compute.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT;
BEGIN
    WITH candidates AS (
        SELECT e.edge_type_id, e.hash
          FROM substrate.edge e
         WHERE e.geom IS NULL
         LIMIT p_limit
    ),
    per_edge_pts AS (
        SELECT em.edge_type_id, em.edge_hash,
               em.edge_role_id, em.entity_hash,
               substrate.entity_centroid_4d(em.entity_hash) AS cgeom
          FROM candidates c
          JOIN substrate.edge_member em
            ON em.edge_type_id = c.edge_type_id
           AND em.edge_hash    = c.hash
    ),
    aggregated AS (
        SELECT edge_type_id, edge_hash,
               ST_MakeLine(cgeom ORDER BY edge_role_id, entity_hash) AS line_geom,
               (array_agg(cgeom ORDER BY edge_role_id, entity_hash))[1] AS first_geom,
               count(*) FILTER (WHERE cgeom IS NOT NULL) AS valid_count
          FROM per_edge_pts
         WHERE cgeom IS NOT NULL
         GROUP BY edge_type_id, edge_hash
    )
    UPDATE substrate.edge e
       SET geom = CASE
                      WHEN a.line_geom IS NOT NULL AND ST_NumPoints(a.line_geom) >= 2 THEN a.line_geom
                      WHEN a.first_geom IS NOT NULL                                  THEN a.first_geom
                      ELSE NULL
                   END
      FROM aggregated a
     WHERE e.edge_type_id = a.edge_type_id
       AND e.hash         = a.edge_hash
       AND e.geom IS NULL
       AND a.valid_count >= 1;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Populate substrate.edge.geom with LINESTRINGZM through participant centroids in role order. One set-based UPDATE — no plpgsql LOOP. substrate.entity_centroid_4d is the per-entity centroid lookup (native-backed).';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Read helpers

-- ── sql/schema/functions/health_summary.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.health_summary();
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS TABLE (metric TEXT, value BIGINT)
LANGUAGE plpgsql STABLE AS $f$
BEGIN
    RETURN QUERY
        SELECT 'entities'::TEXT, count(*)::BIGINT FROM substrate.entity
      UNION ALL SELECT 'edges',           count(*) FROM substrate.edge
      UNION ALL SELECT 'sequences',       count(*) FROM substrate.sequence
      UNION ALL SELECT 'physicalities',   count(*) FROM substrate.physicality
      UNION ALL SELECT 'classifications', count(*) FROM substrate.entity_classification;
END
$f$;

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

-- ── sql/schema/functions/get_entity_info_by_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_hashes BYTEA[]
) RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash FROM unnest(p_hashes) AS in_(h) JOIN substrate.entity e ON e.hash = in_.h;
$f$;

-- ── sql/schema/functions/get_edge_info_by_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
    p_type_ids INT[], p_hashes BYTEA[]
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, provenance_id INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id, e.hash, e.provenance_id
      FROM unnest(p_type_ids, p_hashes) AS in_(t, h)
      JOIN substrate.edge e ON e.edge_type_id = in_.t AND e.hash = in_.h;
$f$;

-- ── sql/schema/functions/get_outbound_edge_targets.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(
    p_src_hash BYTEA, p_edge_type_code TEXT
) RETURNS TABLE (target_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em_t.entity_hash
      FROM substrate.edge_type et
      JOIN substrate.edge_member em_s
        ON em_s.edge_type_id = et.id AND em_s.entity_hash = p_src_hash
      JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
      JOIN substrate.edge_member em_t
        ON em_t.edge_type_id = em_s.edge_type_id AND em_t.edge_hash = em_s.edge_hash
      JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
     WHERE et.code = p_edge_type_code;
$f$;

-- ── sql/schema/functions/get_composition_children.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash BYTEA
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.ordinal, s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
     ORDER BY s.ordinal;
$f$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Composition / sequence

-- ── sql/schema/functions/composition_at.sql ───────────────────────────────────────
-- composition_at(parent_hash, ordinal) - hash-only.
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash BYTEA,
    p_ordinal     INT
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
       AND p_ordinal >= s.ordinal
       AND p_ordinal <  s.ordinal + s.rle_count
     LIMIT 1;
$f$;

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
DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.ordinal, s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
       AND s.ordinal + s.rle_count > p_start
       AND s.ordinal <= p_end
     ORDER BY s.ordinal;
$f$;

-- ── sql/schema/functions/composition_subtrajectory.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT g.n AS ordinal, s.child_hash
      FROM substrate.sequence s
      CROSS JOIN LATERAL generate_series(s.ordinal, s.ordinal + s.rle_count - 1) AS g(n)
     WHERE s.parent_hash = p_parent_hash
       AND g.n BETWEEN p_start AND p_end
     ORDER BY g.n;
$f$;

-- ── sql/schema/functions/composition_parents.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash BYTEA
) RETURNS TABLE (parent_hash BYTEA, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.parent_hash, s.ordinal, s.rle_count
      FROM substrate.sequence s
     WHERE s.child_hash = p_child_hash;
$f$;

-- ── sql/schema/functions/recompose_text_v2.sql ───────────────────────────────────────
-- substrate.recompose_text(parent_hash, max_depth)
--
-- Byte-for-byte text reconstruction by recursive walk of substrate.sequence
-- to codepoint leaves, each codepoint decoded via codepoint_property.
--
-- Phase C unification: hash-only signature. The recursion checks whether
-- a hash refers to a codepoint by joining substrate.entity_classification
-- (the type "codepoint" is metadata, not part of identity).
--
-- The sequence walk respects RLE: a row with rle_count=3 expands to three
-- codepoint emissions in a row. Microsecond per parent at small depth;
-- the btree on (parent_hash, ordinal) makes each step a single index dive.
CREATE OR REPLACE FUNCTION substrate.recompose_text(
    p_entity_hash BYTEA,
    p_max_depth   INT DEFAULT 100000
)
RETURNS TEXT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH RECURSIVE walk(entity_hash, ord_path, depth) AS (
        SELECT p_entity_hash, ARRAY[]::int[], 0
        UNION ALL
        SELECT
            s.child_hash,
            walk.ord_path || gs.n,
            walk.depth + 1
          FROM walk
          JOIN substrate.sequence s
            ON s.parent_hash = walk.entity_hash
          CROSS JOIN LATERAL generate_series(
              s.ordinal, s.ordinal + s.rle_count - 1
          ) AS gs(n)
         WHERE walk.depth < p_max_depth
    ),
    codepoint_leaves AS (
        SELECT walk.ord_path, walk.entity_hash
          FROM walk
          JOIN substrate.codepoint_property cp ON cp.entity_hash = walk.entity_hash
    )
    SELECT COALESCE(
        string_agg(
            chr(cp.codepoint_value),
            ''
            ORDER BY codepoint_leaves.ord_path
        ),
        ''
    )
      FROM codepoint_leaves
      JOIN substrate.codepoint_property cp ON cp.entity_hash = codepoint_leaves.entity_hash;
$$;

COMMENT ON FUNCTION substrate.recompose_text(BYTEA, INT) IS
    'Byte-for-byte text reconstruction via substrate.sequence walk. RLE-expanded. Hash-only signature (Phase C unification).';

-- Backward compat: drop old signature if it exists.
DROP FUNCTION IF EXISTS substrate.recompose_text(INT, BYTEA, INT);

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Significance machinery (prime_edge_significance_per_arena removed —
-- it referenced substrate.staging_edge which no longer exists. The
-- per-arena chunked primer below is what the C# pipeline calls at end of
-- phase via PrimeAllSignificanceAsync.)

-- ── sql/schema/functions/prime_unprimed_edges_chunk.sql ───────────────────────────────────────
-- substrate.prime_unprimed_edges_chunk(p_arena_id, p_chunk_size)
--
-- Backfill primer for arenas that didn't get inline cross-product at
-- edge-insert time (drain_staging_edge_chunk handles steady state per AP-1).
-- This function is for: (a) edges inserted before the AP-1 inline
-- cross-product was added, and (b) new arenas added after some edges
-- already exist.
--
-- Watermark-based forward scan over substrate.edge's PK index
-- (edge_type_id, hash). Per-arena state lives in
-- substrate.arena_priming_state. NO anti-join, NO merge join, NO spill —
-- the previous LEFT JOIN/IS NULL/LIMIT shape over partitioned tables is
-- exactly what triggered PG18's batched-HashJoin slot mismatch
-- (nodeHashjoin.c:1099-1115 vs ExecJustOuterVarVirt) → SIGSEGV/SIGABRT.
--
-- Compound formula matches prime_edge_significance_for_staging:
--   μ₀ = COALESCE(pea.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay)
--   σ₀ = COALESCE(pea.initial_sigma, p.initial_sigma)
CREATE OR REPLACE FUNCTION substrate.prime_unprimed_edges_chunk(
    p_arena_id   INT,
    p_chunk_size INT DEFAULT 4096
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_last_etid   INT;
    v_last_hash   BYTEA;
    v_inserted    BIGINT;
    v_max_etid    INT;
    v_max_hash    BYTEA;
    v_chunk_count INT;
BEGIN
    INSERT INTO substrate.arena_priming_state (context_type_id)
    VALUES (p_arena_id)
    ON CONFLICT (context_type_id) DO NOTHING;

    SELECT last_edge_type_id, last_hash
      INTO v_last_etid, v_last_hash
      FROM substrate.arena_priming_state
     WHERE context_type_id = p_arena_id
       FOR UPDATE;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    SELECT
        p_arena_id,
        nc.edge_type_id,
        nc.hash,
        COALESCE(
            pea.initial_mu,
            p.initial_mu * et.semantic_weight * p.derivation_decay
        ),
        COALESCE(pea.initial_sigma, p.initial_sigma),
        0.06,
        0
      FROM (
            SELECT e.edge_type_id, e.hash, e.provenance_id
              FROM substrate.edge e
             WHERE (e.edge_type_id, e.hash) > (v_last_etid, v_last_hash)
             ORDER BY e.edge_type_id, e.hash
             LIMIT p_chunk_size
           ) AS nc
      JOIN substrate.edge_type   et ON et.id = nc.edge_type_id
      JOIN substrate.provenance  p  ON p.id  = nc.provenance_id
      LEFT JOIN substrate.provenance_edge_authority pea
        ON pea.provenance_id = p.id
       AND pea.edge_type_id  = nc.edge_type_id
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    SELECT sub.edge_type_id, sub.hash, sub.cnt
      INTO v_max_etid, v_max_hash, v_chunk_count
      FROM (
            SELECT edge_type_id,
                   hash,
                   COUNT(*) OVER () AS cnt
              FROM substrate.edge
             WHERE (edge_type_id, hash) > (v_last_etid, v_last_hash)
             ORDER BY edge_type_id, hash
             LIMIT p_chunk_size
           ) sub
     ORDER BY edge_type_id DESC, hash DESC
     LIMIT 1;

    IF v_max_etid IS NULL THEN
        UPDATE substrate.arena_priming_state
           SET completed  = TRUE,
               updated_at = now()
         WHERE context_type_id = p_arena_id;
    ELSE
        UPDATE substrate.arena_priming_state
           SET last_edge_type_id = v_max_etid,
               last_hash         = v_max_hash,
               completed         = (v_chunk_count < p_chunk_size),
               updated_at        = now()
         WHERE context_type_id = p_arena_id;
    END IF;

    RETURN v_inserted;
END $$;

COMMENT ON FUNCTION substrate.prime_unprimed_edges_chunk(INT, INT) IS
    'Per-arena backfill primer. Watermark-driven forward scan over substrate.edge PK index. Replaces the anti-join shape that triggered PG18 batched-HashJoin slot mismatch.';

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

-- ── sql/schema/functions/record_comparison.sql ───────────────────────────────────────
-- substrate.record_comparison(
--     p_arena_id              INT,
--     p_winner_edge_type_id   INT,
--     p_winner_edge_hash      BYTEA,
--     p_loser_edge_type_id    INT,
--     p_loser_edge_hash       BYTEA)
--
-- Record a head-to-head outcome between two edges in the same arena. Step 6
-- of inference (docs/specs/engine/inference.md): when an outcome arrives
-- (user accept/reject, downstream task succeed/fail), comparison events
-- between selected and rejected paths fire Glicko-2 on the corresponding
-- edge_significance rows. Winners' μ rises, losers' μ falls. The substrate
-- learns from every interaction — closed-loop without training, without
-- gradient descent, without labeled data.
--
-- Algorithm: Glickman 2012 (http://www.glicko.net/glicko/glicko2.pdf), tau=0.5.
-- Implementation: ONE call to public.glicko2_bulk_update (native C —
-- ext/libhartonomous/src/glicko_bulk.c via ext/hartonomous_pg/src/pg_glicko_bulk.c)
-- with n=2 — row 0 is the winner-side update (player=winner, opponent=loser,
-- score=1.0); row 1 is the loser-side update (player=loser, opponent=winner,
-- score=0.0). Both new ratings come back in one bulk call; both rows are
-- updated set-based via UPDATE ... FROM unnest.
--
-- Determinism: the formula lives in C with IEEE-754 round-to-nearest-even,
-- fixed evaluation order, no PRNG. Same inputs → bit-identical outputs across
-- C, SQL, and C# (Law #6). Do NOT add a plpgsql or C# reimplementation.
--
-- Hash-addressable: both edges are addressed by (edge_type_id, edge_hash)
-- against substrate.edge_significance, scoped to p_arena_id (the
-- substrate.significance_context.id resolved upstream via
-- substrate.resolve_context_id).

DROP FUNCTION IF EXISTS substrate._glicko2_volatility(DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_comparison(
    p_arena_id            INT,
    p_winner_edge_type_id INT,
    p_winner_edge_hash    BYTEA,
    p_loser_edge_type_id  INT,
    p_loser_edge_hash     BYTEA
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    -- Current state for both edges (public scale, 1500-anchored).
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;

    -- Bulk-Glicko output (n=2: row 0 = winner update, row 1 = loser update).
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    -- Auto-create rows at default rating if missing (priming may have lagged
    -- for this arena × edge). Matches the engine contract.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_winner_edge_type_id, p_winner_edge_hash, 1500.0, 350.0, 0.06, 0),
        (p_arena_id, p_loser_edge_type_id,  p_loser_edge_hash,  1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_winner_edge_type_id
       AND edge_hash       = p_winner_edge_hash;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_loser_edge_type_id
       AND edge_hash       = p_loser_edge_hash;

    -- One bulk-Glicko call covers both updates.
    --   row 0: player=winner, opponent=loser, score=1.0
    --   row 1: player=loser,  opponent=winner, score=0.0
    SELECT g.new_mu, g.new_sigma, g.new_volatility
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
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_winner_edge_type_id
       AND edge_hash       = p_winner_edge_hash;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[2],
           sigma      = new_sigma[2],
           volatility = new_vol[2],
           games      = l_games + 1
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_loser_edge_type_id
       AND edge_hash       = p_loser_edge_hash;
END $$;

COMMENT ON FUNCTION substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA) IS
    'Glicko-2 head-to-head update on substrate.edge_significance for a (winner, loser) pair within an arena. Calls public.glicko2_bulk_update once with n=2 — the formula lives in C (ext/libhartonomous/src/glicko_bulk.c), not in plpgsql. Auto-creates missing rows at default rating before updating. games += 1 on both rows.';

-- ── sql/schema/functions/record_corroboration.sql ───────────────────────────────────────
-- substrate.record_corroboration(
--     p_arena_id     INT,
--     p_edge_type_id INT,
--     p_edge_hash    BYTEA,
--     p_strength     DOUBLE PRECISION)
--
-- Record a positive corroboration event without head-to-head comparison.
-- Algebraically: a Glicko-2 draw against a synthetic opponent equal to this
-- edge itself, scaled by p_strength ∈ (0, 1]. The case p_strength = 1 is
-- the "draw against self" specialization (re-encounter on identical content
-- from another source), and reduces to:
--
--   g(σ)        = 1 / sqrt(1 + 3·σ²/π²)
--   E           = 1 / (1 + exp(-g·(μ - μ)))      = 0.5            (draw)
--   v           = 1 / (g² · E·(1−E)) = 1 / (g² · 0.25)            = 4/g²
--   new_σ²      = 1 / (1/σ² + 1/v)               = 1 / (1/σ² + g²/4)
--   new_μ       = μ + new_σ² · g · (0.5 − 0.5)   = μ   (unchanged)
--   volatility  = unchanged (one-step approximation; full iterative
--                 volatility update is reserved for active comparison
--                 events between distinct entities — see record_comparison)
--
-- For p_strength < 1, sigma narrows by a fraction of the full-strength
-- amount (linear interpolation between current σ and the post-draw σ);
-- p_strength = 0 is a no-op. Light-touch update — no μ shift, no
-- volatility change, just sigma tightening proportional to corroboration
-- strength. games += 1 on every call.
--
-- Hash-addressable: edge identified by (edge_type_id, edge_hash) within
-- arena (significance_context.id resolved upstream). Auto-creates the row
-- at default rating if missing.

CREATE OR REPLACE FUNCTION substrate.record_corroboration(
    p_arena_id     INT,
    p_edge_type_id INT,
    p_edge_hash    BYTEA,
    p_strength     DOUBLE PRECISION
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
        RETURN;  -- no-op for non-positive strength
    END IF;

    -- Auto-create the row at default rating if missing.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, 1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    SELECT sigma
      INTO cur_sigma
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_edge_type_id
       AND edge_hash       = p_edge_hash;

    -- Spec-correct Glicko-2 draw-against-self specialization (public scale
    -- because g and σ both live there: σ² appears in both numerator and
    -- denominator so the c_scale²-by-c_scale² cancellation lets us compute
    -- directly in public scale).
    --
    --   g(σ)   = 1 / sqrt(1 + 3·σ²/π²)
    --   v      = 4 / g²
    --   new_σ² = 1 / (1/σ² + g²/4)
    g_val          := 1.0 / sqrt(1.0 + 3.0 * cur_sigma * cur_sigma / c_pi_sq);
    new_sigma_full := 1.0 / sqrt(
                          1.0 / (cur_sigma * cur_sigma)
                          + (g_val * g_val) / 4.0
                      );

    -- Linear interpolation between current σ and post-full-draw σ by
    -- p_strength. Strength = 1 → full draw-against-self update.
    -- Strength < 1 → partial sigma narrowing.
    UPDATE substrate.edge_significance
       SET sigma = cur_sigma + (new_sigma_full - cur_sigma) * LEAST(p_strength, 1.0),
           games = games + 1
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_edge_type_id
       AND edge_hash       = p_edge_hash;
END $$;

COMMENT ON FUNCTION substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION) IS
    'Glicko-2 corroboration update on substrate.edge_significance: lightweight sigma narrowing (μ unchanged) for the algebraic specialization of a draw against self. p_strength scales the σ narrowing; 1.0 = full draw-against-self update, 0 = no-op. games += 1.';

-- ── sql/schema/functions/record_outcome.sql ───────────────────────────────────────
-- substrate.record_outcome(arena_id, winner_target_hash, loser_target_hashes[])
--
-- Engine spec Step 6 (inference.md): Glicko-2 comparison events update
-- significance ratings on edges that supported selected vs rejected
-- paths. For each (winner, loser) pair: identify strongest edge
-- incident to each target in the arena, then update both sides.
--
-- Set-based + native bulk-Glicko. No FOREACH, no per-row PERFORM.
--   * unnest + LATERAL LIMIT 1 finds the strongest edge per loser.
--   * public.glicko2_bulk_update (native C) applies winner-side
--     (score=1) and loser-side (score=0) Glicko-2 updates in one call
--     each, returning new mu/sigma/volatility arrays.
--   * UPDATE ... FROM unnest writes the new ratings back set-based.
--
-- Returns the number of (winner_edge × loser_edge) pairs recorded.
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.record_outcome(
    p_arena_id            INT,
    p_winner_target_hash  BYTEA,
    p_loser_target_hashes BYTEA[]
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

    -- 1. Strongest edge incident to winner in arena (single set-based SELECT).
    SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
      INTO v_w_etid, v_w_hash, v_w_mu, v_w_sigma, v_w_vol
      FROM substrate.edge_member em
      JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id
       AND es.edge_hash    = em.edge_hash
       AND es.context_type_id = p_arena_id
     WHERE em.entity_hash = p_winner_target_hash
     ORDER BY es.mu DESC NULLS LAST
     LIMIT 1;

    IF v_w_etid IS NULL THEN RETURN 0; END IF;

    -- 2. Strongest edge per loser, set-based via unnest + LATERAL LIMIT 1,
    --    aggregated into parallel arrays for one bulk-Glicko call.
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
              ON es.edge_type_id = em.edge_type_id
             AND es.edge_hash    = em.edge_hash
             AND es.context_type_id = p_arena_id
           WHERE em.entity_hash = lt.loser_hash
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) le
     WHERE lt.loser_hash IS NOT NULL
       AND lt.loser_hash <> p_winner_target_hash;

    v_pair_count := COALESCE(array_length(v_l_etid_arr, 1), 0);
    IF v_pair_count = 0 THEN RETURN 0; END IF;

    -- 3. Winner-side parallel arrays (same μ/σ/vol repeated N times).
    v_w_mu_arr    := array_fill(v_w_mu,    ARRAY[v_pair_count]);
    v_w_sigma_arr := array_fill(v_w_sigma, ARRAY[v_pair_count]);
    v_w_vol_arr   := array_fill(v_w_vol,   ARRAY[v_pair_count]);
    v_score_w_arr := array_fill(1.0::double precision, ARRAY[v_pair_count]);
    v_score_l_arr := array_fill(0.0::double precision, ARRAY[v_pair_count]);

    -- 4. Bulk Glicko-2 in native C — two calls (winner side / loser side).
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

    -- 5. Winner is rated against N opponents; collapse to single value
    --    using the most-uncertain (largest σ) result so games-played is
    --    monotonic but uncertainty stays honest.
    SELECT mu, sigma, volatility
      INTO v_w_final_mu, v_w_final_sigma, v_w_final_vol
      FROM unnest(v_w_new_mu, v_w_new_sigma, v_w_new_vol) AS u(mu, sigma, volatility)
     ORDER BY sigma DESC LIMIT 1;

    UPDATE substrate.edge_significance
       SET mu         = v_w_final_mu,
           sigma      = v_w_final_sigma,
           volatility = v_w_final_vol,
           games      = games + v_pair_count
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = v_w_etid
       AND edge_hash       = v_w_hash;

    -- 6. Loser updates via UPDATE...FROM unnest — set-based apply.
    UPDATE substrate.edge_significance es
       SET mu         = u.new_mu,
           sigma      = u.new_sigma,
           volatility = u.new_volatility,
           games      = es.games + 1
      FROM unnest(v_l_etid_arr, v_l_hash_arr, v_l_new_mu, v_l_new_sigma, v_l_new_vol)
        AS u(etype_id, ehash, new_mu, new_sigma, new_volatility)
     WHERE es.context_type_id = p_arena_id
       AND es.edge_type_id    = u.etype_id
       AND es.edge_hash       = u.ehash;

    RETURN v_pair_count;
END $$;

COMMENT ON FUNCTION substrate.record_outcome(INT, BYTEA, BYTEA[]) IS
    'Engine Step 6 outcome update — set-based + native bulk-Glicko. unnest + LATERAL gather pairs; public.glicko2_bulk_update (C) computes new ratings; UPDATE ... FROM unnest applies them. No FOREACH, no per-pair PERFORM.';

-- ── sql/schema/functions/create_arena.sql ───────────────────────────────────────
-- substrate.create_arena(code TEXT, backfill BOOLEAN DEFAULT TRUE)
--
-- Adds a new arena to substrate.significance_context (the open-vocabulary
-- arena registry). When backfill=TRUE, registers the arena as "needs
-- priming" via substrate.arena_priming_state. Post-W2E the chunked
-- backfill is driven by the StreamingIngestionPipeline's
-- PrimeAllSignificanceAsync end-of-phase pass — it iterates the arena
-- list at call time and loops substrate.prime_unprimed_edges_chunk
-- per arena until it returns 0. No background primer process; no
-- continuous loop. Adding a new arena mid-corpus means it gets primed
-- on the next FlushAsync cycle.
--
-- Why this shape:
--   * The arena CREATE is a single INSERT (set-based, transactional).
--   * The chunked BACKFILL — looping until prime_unprimed_edges_chunk
--     returns 0 — is a "while loop" over expensive set-based work.
--     That loop lives in C# (StreamingIngestionPipeline.
--     PrimeAllSignificanceAsync), not in plpgsql. Per architectural
--     rule: SQL is thin, heavy lifting and control flow live in
--     C/C++ extensions or the C# Compute Facade.
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
    v_id      INT;
    v_existed BOOLEAN := FALSE;
BEGIN
    IF p_code IS NULL OR length(trim(p_code)) = 0 THEN
        RAISE EXCEPTION 'p_code must be a non-empty arena code';
    END IF;

    SELECT id INTO v_id
      FROM substrate.significance_context
     WHERE code = p_code;

    IF v_id IS NOT NULL THEN
        v_existed := TRUE;
    ELSE
        INSERT INTO substrate.significance_context (code)
        VALUES (p_code)
        RETURNING id INTO v_id;
    END IF;

    IF p_backfill AND NOT v_existed THEN
        -- Register the arena as "needs priming". The C# pipeline's
        -- PrimeAllSignificanceAsync end-of-phase pass iterates the arena
        -- list at call time and primes via prime_unprimed_edges_chunk;
        -- this row is the watermark anchor for that loop. INSERT ON
        -- CONFLICT keeps it idempotent against concurrent create_arena
        -- callers.
        INSERT INTO substrate.arena_priming_state (context_type_id)
        VALUES (v_id)
        ON CONFLICT (context_type_id) DO NOTHING;
    END IF;

    RETURN v_id;
END $$;

COMMENT ON FUNCTION substrate.create_arena(TEXT, BOOLEAN) IS
    'Add an arena to substrate.significance_context. With backfill=TRUE, registers it for priming via substrate.arena_priming_state — the C# pipeline''s PrimeAllSignificanceAsync end-of-phase pass picks it up and primes via prime_unprimed_edges_chunk in chunks. SQL stays thin; the chunking loop lives in C#. Returns the arena id; idempotent.';

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

-- ── sql/schema/functions/populate_codepoint_atoms.sql ───────────────────────────────────────
-- substrate.populate_codepoint_atoms(provenance_code TEXT, trust_mu FLOAT8)
--
-- Replaces the C# UCD/UCA decomposer's per-codepoint emission loop with
-- a substrate-side bulk INSERT driven by the extension's embedded UCD
-- 17.0.0 tables. Inserts ~1,114,112 codepoint entities + classifications
-- + S^3 physicalities + significance rows — same substrate state,
-- ~30× the speed of XML parsing.
--
-- Pre-requisites:
--   * substrate.entity, substrate.entity_classification, substrate.physicality,
--     substrate.entity_significance tables exist (bootstrap satisfied).
--   * Extension hartonomous installed (CREATE EXTENSION hartonomous).
--   * Reference rows seeded for: provenance, entity_type=codepoint,
--     physicality_type=s3_position, significance_context=source_authority.
--
-- Determinism (Law #6): substrate.cp_hash(cp) is the BLAKE3 of the rune's
-- big-endian 4-byte encoding, precomputed at extension build time;
-- substrate.cp_centroid(cp) is the Super-Fibonacci S^3 point anchored by
-- UCA-sorted index, also precomputed. Same UCD version → byte-identical
-- substrate state across runs.
--
-- IMPLEMENTATION NOTE — single SRF, zero per-row C calls.
--
-- The four bulk INSERTs all read from substrate.ucd_codepoints(), which
-- is a single C call returning all 1,114,112 rows with hash, x, y, z, m,
-- hilbert and every UCD property pre-computed. We do NOT call the scalar
-- substrate.cp_hash(cp) / cp_x(cp) / cp_y(cp) / cp_z(cp) / cp_m(cp)
-- accessors over generate_series — that is 5.6M scalar C invocations
-- per function call, which is fragile under executor pressure and
-- pointless when the SRF already materializes the same payload once.
--
-- Returns the count of codepoints processed.
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_atoms(
    p_provenance_code TEXT   DEFAULT 'unicode_consortium',
    p_trust_mu        FLOAT8 DEFAULT NULL
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_provenance_id    INT;
    v_codepoint_etype  INT;
    v_s3_phys_type     INT;
    v_source_auth_ctx  INT;
    v_initial_mu       FLOAT8;
BEGIN

    SELECT id, COALESCE(p_trust_mu, initial_mu)
      INTO v_provenance_id, v_initial_mu
      FROM substrate.provenance
     WHERE code = p_provenance_code;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'unknown provenance code: %', p_provenance_code;
    END IF;

    SELECT id INTO v_codepoint_etype
      FROM substrate.entity_type WHERE code = 'codepoint';
    IF v_codepoint_etype IS NULL THEN
        RAISE EXCEPTION 'entity_type code=''codepoint'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_s3_phys_type
      FROM substrate.physicality_type WHERE code = 's3_position';
    IF v_s3_phys_type IS NULL THEN
        RAISE EXCEPTION 'physicality_type code=''s3_position'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_source_auth_ctx
      FROM substrate.significance_context WHERE code = 'source_authority';
    IF v_source_auth_ctx IS NULL THEN
        RAISE EXCEPTION 'significance_context code=''source_authority'' missing — bootstrap not applied?';
    END IF;

    -- Warm up the composite tupdesc cache before plpgsql plans the SRF.
    PERFORM 1 FROM substrate.ucd_codepoints(0, 1);

    -- 1. Insert all 1,114,112 codepoint entities.
    INSERT INTO substrate.entity (hash)
    SELECT a.hash FROM substrate.ucd_codepoints() a
    ON CONFLICT (hash) DO NOTHING;

    -- 2. Classify each as 'codepoint' under the given provenance.
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT a.hash, v_codepoint_etype, v_provenance_id
      FROM substrate.ucd_codepoints() a
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- 3. S^3 physicality built from SRF-supplied (x,y,z,m).
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           a.hash,
           a.hash,
           ST_MakePoint(a.x, a.y, a.z, a.m)
      FROM substrate.ucd_codepoints() a
    ON CONFLICT DO NOTHING;

    -- 4. Source-authority significance prior.
    INSERT INTO substrate.entity_significance (context_type_id, entity_hash, mu, sigma, volatility, games)
    SELECT v_source_auth_ctx,
           a.hash,
           v_initial_mu,
           350.0,
           0.06,
           0
      FROM substrate.ucd_codepoints() a
    ON CONFLICT DO NOTHING;

    RETURN 1114112;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms(TEXT, FLOAT8) IS
  'Bulk-fill substrate.entity + entity_classification + physicality(s3_position) + entity_significance(source_authority) for all 1,114,112 codepoints from the hartonomous extension''s embedded UCD 17.0.0 tables using one SRF call (substrate.ucd_codepoints) per INSERT. Zero per-row scalar C invocations. Idempotent via ON CONFLICT.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Extension-driven UCD/UCA reference + property population (replaces the
-- C# UCD decomposer's per-codepoint round-trips with five SQL calls). The
-- functions below depend on the hartonomous extension being loaded —
-- bootstrap.sql loads it last (Phase 16), so these are declared here but
-- only callable post-bootstrap. Seed phases (scripts/seed/Ucd.ps1) invoke
-- them in this exact order.

-- ── sql/schema/functions/populate_general_categories_from_ext.sql ───────────────────────────────────────
-- substrate.populate_general_categories_from_ext()
--
-- Drives substrate.general_category from the embedded UCD catalog. The
-- inventory SETOF carries (id, code, description, group_code) directly
-- from pg_unicode_inventory.c — no derivation needed.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_general_categories_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.general_category (code, group_code, description)
    SELECT v.code, v.group_code, v.description
    FROM substrate.ucd_general_categories() AS v
    ON CONFLICT (code) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_general_categories_from_ext() IS
    'Bulk-loads substrate.general_category from the embedded UCD catalog. Idempotent. Returns the number of rows inserted on this call.';

-- ── sql/schema/functions/populate_scripts_from_ext.sql ───────────────────────────────────────
-- substrate.populate_scripts_from_ext()
--
-- Drives substrate.script from the embedded UCD catalog. The extension's
-- ucd_scripts() SETOF returns just (id, code) — substrate.script's only
-- distinguishing column is `code`, so we map directly.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_scripts_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.script (code)
    SELECT v.code
    FROM substrate.ucd_scripts() AS v
    WHERE v.code IS NOT NULL AND length(v.code) > 0
    ON CONFLICT (code) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_scripts_from_ext() IS
    'Bulk-loads substrate.script from the embedded UCD catalog. Idempotent. Returns the number of rows inserted on this call.';

-- ── sql/schema/functions/populate_blocks_from_ext.sql ───────────────────────────────────────
-- substrate.populate_blocks_from_ext()
--
-- Drives substrate.block from the embedded UCD catalog. range_start and
-- range_end come straight from pg_unicode_inventory.c — no aggregation
-- against the bulk codepoint SRF needed.
--
-- Idempotent — ON CONFLICT (code) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_blocks_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.block (code, range_start, range_end)
    SELECT v.code, v.range_start, v.range_end
    FROM substrate.ucd_blocks() AS v
    ON CONFLICT (code) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_blocks_from_ext() IS
    'Bulk-loads substrate.block (with range_start/range_end direct from the embedded UCD catalog) — no aggregation pass over the codepoint SRF. Idempotent.';

-- ── sql/schema/functions/populate_break_properties_from_ext.sql ───────────────────────────────────────
-- substrate.populate_break_properties_from_ext()
--
-- Drives substrate.break_property from the embedded UCD catalog. The
-- inventory SETOF returns (id, category, code, enum_id) where category
-- is the UAX #29 category (GCB/WB/SB/LB) — no parsing required.
--
-- Idempotent — ON CONFLICT (code, category) DO NOTHING.

CREATE OR REPLACE FUNCTION substrate.populate_break_properties_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.break_property (code, category)
    SELECT v.code, v.category
    FROM substrate.ucd_break_properties() AS v
    ON CONFLICT (code, category) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_break_properties_from_ext() IS
    'Bulk-loads substrate.break_property from the embedded UCD catalog. Each row is a (category, code) pair — GCB/WB/SB/LB enums tagged at generation time. Idempotent.';

-- ── sql/schema/functions/populate_codepoint_property_from_ext.sql ───────────────────────────────────────
-- substrate.populate_codepoint_property_from_ext()
--
-- Bulk-populates substrate.codepoint_property from the embedded UCD
-- catalog. Replaces the C# UCD decomposer's per-codepoint round-trips
-- with chunked C-driven scans: substrate.ucd_codepoints(lo, count) emits
-- bounded slices; we JOIN to the reference tables (already populated by
-- populate_general_categories/scripts/blocks/break_properties) to translate
-- the embedded enum ids into FK ids.
--
-- The reference tables MUST already be populated. Call order in
-- scripts/seed/Ucd.ps1:
--   1. populate_general_categories_from_ext()
--   2. populate_scripts_from_ext()
--   3. populate_blocks_from_ext()
--   4. populate_break_properties_from_ext()
--   5. populate_codepoint_property_range_from_ext(lo, count), invoked from
--      the seed script in separate client-side chunks.
--
-- Reference-table FK translation: the embedded catalog's enum ids are
-- 0-based array indices; substrate reference tables use 1-based SERIAL ids.
-- We pre-build small temp lookup tables joining the inventory SRFs to
-- the reference tables on (code) / (code, category) so the bulk SELECT
-- stays planar.
--
-- Idempotent — ON CONFLICT (entity_hash) DO NOTHING. The range function is
-- the real bulk-load primitive. Seed scripts call it from separate client-side
-- chunks so every chunk has its own statement/transaction boundary. Keeping
-- the batching boundary outside PL/pgSQL avoids a single backend accumulating
-- executor state for all 1.1M rows.

CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_range_from_ext(
    p_start INT,
    p_count INT
)
RETURNS int
LANGUAGE sql
VOLATILE
SET max_parallel_workers_per_gather = 0
SET enable_mergejoin = off
AS $$
    WITH
    args AS (
        SELECT
            GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112)) AS slice_start,
            GREATEST(
                0,
                LEAST(
                    COALESCE(p_count, 0),
                    1114112 - GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112))
                )
            ) AS slice_count
    ),
    gc_lookup AS (
        SELECT v.id AS ext_id, gc.id AS ref_id
        FROM substrate.ucd_general_categories() v
        JOIN substrate.general_category gc ON gc.code = v.code
    ),
    script_lookup AS (
        SELECT v.id AS ext_id, s.id AS ref_id
        FROM substrate.ucd_scripts() v
        JOIN substrate.script s ON s.code = v.code
    ),
    block_lookup AS (
        SELECT v.id AS ext_id, b.id AS ref_id
        FROM substrate.ucd_blocks() v
        JOIN substrate.block b ON b.code = v.code
    ),
    bp_lookup_gcb AS (
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'GCB'
        WHERE v.category = 'GCB'
    ),
    bp_lookup_wb AS (
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'WB'
        WHERE v.category = 'WB'
    ),
    bp_lookup_sb AS (
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'SB'
        WHERE v.category = 'SB'
    ),
    bp_lookup_lb AS (
        SELECT v.enum_id AS ext_id, bp.id AS ref_id
        FROM substrate.ucd_break_properties() v
        JOIN substrate.break_property bp ON bp.code = v.code AND bp.category = 'LB'
        WHERE v.category = 'LB'
    ),
    inserted AS (
    INSERT INTO substrate.codepoint_property (
        entity_hash,
        codepoint_value,
        general_category_id,
        script_id,
        block_id,
        gcb_id, wb_id, sb_id, lb_id,
        is_extended_pictographic,
        ccc,
        decomposition_mapping,
        simple_case_fold,
        full_case_fold
    )
    SELECT
        a.hash,
        a.cp,
        gcl.ref_id,
        scrl.ref_id,
        blkl.ref_id,
        gbpl.ref_id,
        wbpl.ref_id,
        sbpl.ref_id,
        lbpl.ref_id,
        a.extended_pictographic,
        a.ccc::SMALLINT,
        a.decomposition_mapping,
        NULLIF(a.simple_case_fold, -1),
        a.full_case_fold
    FROM args
    CROSS JOIN LATERAL substrate.ucd_codepoints(args.slice_start, args.slice_count) a
    LEFT JOIN gc_lookup       gcl  ON gcl.ext_id  = a.general_category
    LEFT JOIN script_lookup   scrl ON scrl.ext_id = a.script
    LEFT JOIN block_lookup    blkl ON blkl.ext_id = a.block
    LEFT JOIN bp_lookup_gcb   gbpl ON gbpl.ext_id = a.gcb
    LEFT JOIN bp_lookup_wb    wbpl ON wbpl.ext_id = a.wb
    LEFT JOIN bp_lookup_sb    sbpl ON sbpl.ext_id = a.sb
    LEFT JOIN bp_lookup_lb    lbpl ON lbpl.ext_id = a.lb
    WHERE gcl.ref_id IS NOT NULL
      AND scrl.ref_id IS NOT NULL
      AND blkl.ref_id IS NOT NULL
        ON CONFLICT (entity_hash) DO NOTHING
        RETURNING 1
        )
        SELECT count(*)::int FROM inserted;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_range_from_ext(INT, INT) IS
    'Populates a bounded codepoint_property slice from the embedded UCD catalog. Intended seed primitive; callers provide client-side chunk boundaries so each chunk has a separate statement/transaction boundary.';

CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'populate_codepoint_property_from_ext() is intentionally disabled for the full UCD load; call populate_codepoint_property_range_from_ext(start,count) from the seed script so each chunk has a real client-side statement boundary';
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_from_ext() IS
    'Disabled compatibility wrapper. Use populate_codepoint_property_range_from_ext(start,count) from client-side chunks so each bounded insert has a real statement/transaction boundary.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

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
--      substrate.sequence + cross-classification matches via
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

    -- Materialize seeds: prompt's word_form-classified sequence children
    -- + the prompt itself + parent compositions of those word_forms.
    CREATE TEMP TABLE IF NOT EXISTS _infer_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _infer_seeds;
    INSERT INTO _infer_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.sequence s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        WHERE s.parent_hash = p_doc_hash
    ),
    -- Inverse-sequence: lemma / synset compositions that contain the
    -- prompt's word_form hashes as children. These are the substrate's
    -- "where else does this word appear" bridges into the rich graph.
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.sequence s ON s.child_hash = d.h
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
-- Hash-only signature throughout. recompose_text walks substrate.sequence
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

    -- Seeds: prompt's word_form-classified sequence children + their
    -- lemma/synset parent compositions. Same seed activation as substrate.infer.
    CREATE TEMP TABLE IF NOT EXISTS _topk_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _topk_seeds;
    INSERT INTO _topk_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.sequence s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        WHERE s.parent_hash = p_doc_hash
    ),
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.sequence s ON s.child_hash = d.h
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

    -- Seed activation: prompt's word_form sequence children + their
    -- lemma/synset parent compositions.
    SELECT array_agg(DISTINCT h)
    INTO v_seeds
    FROM (
        SELECT s.child_hash AS h
        FROM substrate.sequence s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        WHERE s.parent_hash = p_prompt_hash
        UNION
        SELECT s.parent_hash AS h
        FROM substrate.sequence s
        JOIN substrate.sequence sd ON sd.parent_hash = p_prompt_hash AND sd.child_hash = s.child_hash
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
          AND EXISTS (SELECT 1 FROM substrate.sequence sq WHERE sq.parent_hash = em_t.entity_hash LIMIT 1)
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

    -- 3. Sequence parents: compositions containing this entity.
    SELECT
        'sequence_parent'::TEXT,
        s.parent_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.sequence s
    WHERE s.child_hash = p_entity_hash

    UNION ALL

    -- 4. Sequence children: entities this composition contains (if any).
    SELECT
        'sequence_child'::TEXT,
        s.child_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.sequence s
    WHERE s.parent_hash = p_entity_hash

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
        substrate.dist_4d(p_self.geom, p_other.geom),
        NULL::INT
    FROM substrate.physicality p_self
    JOIN substrate.physicality p_other
      ON p_other.entity_hash <> p_self.entity_hash
     AND p_other.physicality_type_id = p_self.physicality_type_id
    WHERE p_self.entity_hash = p_entity_hash
      AND p_frechet_threshold > 0
      AND p_self.geom IS NOT NULL
      AND p_other.geom IS NOT NULL
      AND substrate.dist_4d(p_self.geom, p_other.geom) <= p_frechet_threshold;
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
                AND EXISTS (SELECT 1 FROM substrate.sequence sq WHERE sq.parent_hash = em_t.entity_hash LIMIT 1)
              LIMIT 1
            ) AS gloss_hash
        FROM high_mu_synsets h
    )
    SELECT
        ROW_NUMBER() OVER (ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash)::INT AS rank,
        w.entity_hash AS target_hash,
        w.best_mu     AS confidence,
        substrate.recompose_text(w.gloss_hash, p_max_depth) AS answer
    FROM with_gloss w
    WHERE w.gloss_hash IS NOT NULL
    ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash
    LIMIT p_top_k;
$$;

COMMENT ON FUNCTION substrate.surprise(INT, INT) IS
    'Open-ended fact selector. Picks up to p_top_k high-mu synsets that have associated gloss text, returns each with confidence and recomposed text. Used by the brain when the prompt does not point at a specific entity.';

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
--   '4d'      → substrate.dist_4d (POINTZM short-circuits to native
--               distance_4d; multi-vertex geometries fall through to native
--               frechet_4d via ST_DumpPoints).
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
    'Top-k entities by 4D distance from the seed entity''s stored physicality, filtered to a target entity_type via substrate.entity_classification. Uses the pg_similarity_topk C SRF for the inner scan and heap. Distance kinds: 4d (default; POINTZM fast path) | frechet (always vertex-stream Fréchet) | s3 (reserved, not yet wired).';

-- ── sql/schema/functions/classify.sql ───────────────────────────────────────
-- substrate.classify(seed_hash, junction_kind, k)
--
-- Top-k labels for an entity from a junction table, ranked by Glicko-2 mu
-- desc, sigma asc (tighter confidence wins ties). Junction kinds:
--   'pos'           → substrate.entity_pos          (Glicko-2 native)
--   'sense'         → substrate.entity_sense        (Glicko-2 native)
--   'pattern_deprel'→ substrate.pattern_deprel      (Glicko-2 native)
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
        SELECT p.id, p.code, ep.mu, ep.sigma, ep.games,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_pos ep
        JOIN substrate.pos p ON p.id = ep.pos_id
        WHERE ep.entity_hash = p_seed_hash
        ORDER BY ep.mu DESC, ep.sigma ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'sense' THEN
        RETURN QUERY
        SELECT s.id, s.code, es.mu, es.sigma, es.games,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_sense es
        JOIN substrate.sense s ON s.id = es.sense_id
        WHERE es.entity_hash = p_seed_hash
        ORDER BY es.mu DESC, es.sigma ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'pattern_deprel' THEN
        RETURN QUERY
        SELECT d.id, d.code, pd.mu, pd.sigma, pd.games,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.pattern_deprel pd
        JOIN substrate.deprel d ON d.id = pd.deprel_id
        WHERE pd.entity_hash = p_seed_hash
        ORDER BY pd.mu DESC, pd.sigma ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'language' THEN
        RETURN QUERY
        SELECT l.id, l.code, NULL::DOUBLE PRECISION, NULL::DOUBLE PRECISION, NULL::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_language el
        JOIN substrate.language l ON l.id = el.language_id
        WHERE el.entity_hash = p_seed_hash
        ORDER BY l.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'morph_feature' THEN
        RETURN QUERY
        SELECT mf.id, mf.code, NULL::DOUBLE PRECISION, NULL::DOUBLE PRECISION, NULL::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_morph_feature emf
        JOIN substrate.morph_feature mf ON mf.id = emf.morph_feature_id
        WHERE emf.entity_hash = p_seed_hash
        ORDER BY mf.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'classification' THEN
        RETURN QUERY
        SELECT et.id, et.code, NULL::DOUBLE PRECISION, NULL::DOUBLE PRECISION, NULL::INT,
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
    'Top-k labels from a junction table for an entity, ranked by Glicko-2 mu (where present). Junction kinds: pos, sense, pattern_deprel (Glicko-2 native); language, morph_feature, classification (no Glicko, alphabetical).';

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
        FROM substrate.sequence s
        JOIN substrate.entity_classification c ON c.entity_hash = s.child_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        LEFT JOIN substrate.entity_language el
               ON el.entity_hash = s.child_hash
              AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
        WHERE s.parent_hash = p_seed_hash
          AND et.code IN ('bpe_token', 'word_form')
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
    WITH cands AS (
        SELECT em_t.entity_hash AS target_hash,
               sum(COALESCE(es.mu, 1500.0)) AS total_mu
        FROM substrate.sequence sq
        JOIN substrate.edge_member em_s
          ON em_s.entity_hash = sq.child_hash
        JOIN substrate.edge e
          ON e.edge_type_id = em_s.edge_type_id
         AND e.hash = em_s.edge_hash
        JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
        JOIN substrate.edge_member em_t
          ON em_t.edge_type_id = e.edge_type_id
         AND em_t.edge_hash    = e.hash
        JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
        LEFT JOIN substrate.edge_significance es
               ON es.edge_type_id   = e.edge_type_id
              AND es.edge_hash      = e.hash
              AND es.context_type_id = v_arena_id
        WHERE sq.parent_hash = p_seed_hash
          AND em_t.entity_hash <> p_seed_hash
        GROUP BY em_t.entity_hash
        ORDER BY total_mu DESC
        LIMIT p_max_results
    )
    SELECT count(*), max(total_mu),
           (SELECT target_hash FROM cands ORDER BY total_mu DESC LIMIT 1)
    INTO v_targets, v_best_mu, v_best_hash
    FROM cands;

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

-- ── sql/schema/functions/apply_firefly_rotation.sql ───────────────────────────────────────
-- substrate.apply_firefly_rotation(p_model_source_id, R 3x3)
--
-- Rotate every embedding_firefly POINTZM physicality of a given
-- model_source by a 3×3 orthogonal matrix R, leaving the M coordinate
-- (L2 magnitude) untouched. Run after EmbeddingFireflyPass for non-anchor
-- models. R must be orthogonal (det = +1); the caller is responsible —
-- Procrustes (Kabsch) returns such an R.
--
-- Hash-as-PK: substrate.physicality and substrate.entity_model_source
-- both reference entities by entity_hash (no surrogate id column).

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
                   p_r00 * ST_X(p.geom) + p_r01 * ST_Y(p.geom) + p_r02 * ST_Z(p.geom),
                   p_r10 * ST_X(p.geom) + p_r11 * ST_Y(p.geom) + p_r12 * ST_Z(p.geom),
                   p_r20 * ST_X(p.geom) + p_r21 * ST_Y(p.geom) + p_r22 * ST_Z(p.geom),
                   ST_M(p.geom))
          FROM substrate.entity_model_source ems,
               substrate.physicality_type pt
         WHERE p.entity_hash         = ems.entity_hash
           AND ems.model_source_id   = p_model_source_id
           AND p.physicality_type_id = pt.id
           AND pt.code               = 'embedding_firefly'
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM updated;
$$;

COMMENT ON FUNCTION substrate.apply_firefly_rotation(INT, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8) IS
    'Rotate every embedding_firefly POINTZM physicality of one model_source by a 3×3 orthogonal R. M (L2 magnitude) preserved. Caller (Procrustes/Kabsch) ensures det(R)=+1. Returns count of rotated rows.';

-- ── sql/schema/functions/get_firefly_coords.sql ───────────────────────────────────────
-- substrate.get_firefly_coords(p_bpe_token_entity_hashes BYTEA[], p_model_source_id INT)
--
-- Return per-entity firefly POINTZM coordinates for a vocab intersection
-- set, scoped to one model_source. Used by EmbeddingAlignmentPass to pull
-- the (anchor, this-model) coordinate pairs into managed memory for
-- Procrustes/Kabsch fitting.
--
-- Hash-as-PK: input is an array of entity_hash BYTEAs, not surrogate ids.
-- Output rows are ordered by entity_hash ASC so two calls (anchor model,
-- this model) for the same hash set yield aligned column orderings.

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
       AND pt.code = 'embedding_firefly'
     ORDER BY p.entity_hash ASC;
$$;

COMMENT ON FUNCTION substrate.get_firefly_coords(BYTEA[], INT) IS
    'Per-entity firefly XYZ coords for a vocab intersection set, scoped to one model_source. Ordered by entity_hash ASC so cross-model calls return aligned arrays. Used by EmbeddingAlignmentPass for Procrustes input.';

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

    -- Firefly count: Track 1 embedding_firefly physicalities on any
    -- substrate entity reachable from this model via entity_model_source.
    -- The substrate mechanic is universal — fireflies attach to whatever
    -- content-addressed entity the Laplacian-eigenmap projection landed on,
    -- regardless of classification (word_form / bpe_token / codepoint /
    -- pixel_region / audio_chunk / video_frame / lemma / synset / etc.).
    -- The query is modality- and language-agnostic by design.
    SELECT 'embedding_firefly_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
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
-- All numerical work runs in compiled C from the hartonomous extension:
--   public.point4d(x,y,z,m)      — POINTZM vertex → native point4d
--   public.centroid_4d(point4d)  — single-pass centroid aggregate (C)
--   public.distance_4d(p,q)      — 4D Euclidean distance (C)
--
-- The SQL function is one flat SELECT — no CTE, no plpgsql loop. Two
-- scans of the cloud are necessary (centroid first, then dispersion
-- against centroid). For typical fireflies-per-token (<= models ingested,
-- usually <100) the cost is dominated by index probe, not the scans.
--
-- Future work: a native firefly_consensus(token_hash bytea) C function
-- in ext/hartonomous_pg/src/ would do centroid + dispersion in one
-- pass over the SPI cursor — single-pass, all C, no SQL composition.
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
          SELECT public.centroid_4d(
                     public.point4d(
                         ST_X(p.geom)::double precision,
                         ST_Y(p.geom)::double precision,
                         COALESCE(ST_Z(p.geom), 0)::double precision,
                         COALESCE(ST_M(p.geom), 0)::double precision))   AS centroid,
                 count(*)::int                                            AS n
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) c
      CROSS JOIN LATERAL (
          SELECT max(public.distance_4d(
                     public.point4d(
                         ST_X(p.geom)::double precision,
                         ST_Y(p.geom)::double precision,
                         COALESCE(ST_Z(p.geom), 0)::double precision,
                         COALESCE(ST_M(p.geom), 0)::double precision),
                     c.centroid))                                          AS max_dist
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) d;
$$;

COMMENT ON FUNCTION substrate.cross_model_consensus(bytea) IS
    'Centroid + dispersion + agreement over a token''s firefly cloud. All math via native hartonomous primitives (point4d, centroid_4d aggregate, distance_4d). One SQL function, no CTE, no plpgsql.';

-- ── sql/schema/functions/cross_model_divergence.sql ───────────────────────────────────────
-- substrate.cross_model_divergence(p_token_hash bytea, p_model_a_arch_hash bytea, p_model_b_arch_hash bytea)
--
-- Pairwise 4D Hausdorff distance between two models' fireflies for the
-- same token entity. Returns NULL when either model has no firefly for
-- the token. Drives D-cross-model-divergence-nonzero gate.
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
        SELECT ST_X(ST_PointN(p.geom, 1)) AS x,
               ST_Y(ST_PointN(p.geom, 1)) AS y,
               ST_Z(ST_PointN(p.geom, 1)) AS z,
               ST_M(ST_PointN(p.geom, 1)) AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_a
            ON ems_a.model_source_id = ems_t.model_source_id
           AND ems_a.entity_hash = p_model_a_arch_hash
         WHERE p.entity_hash = p_token_hash
    ),
    b AS (
        SELECT ST_X(ST_PointN(p.geom, 1)) AS x,
               ST_Y(ST_PointN(p.geom, 1)) AS y,
               ST_Z(ST_PointN(p.geom, 1)) AS z,
               ST_M(ST_PointN(p.geom, 1)) AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
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
    'Pairwise 4D distance between model A''s and model B''s fireflies for a shared token entity. Used by `hartonomous compare-models` and D-cross-model-divergence-nonzero gate.';

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

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 14: procedures ─────────────────────────────────────────────

-- ── sql/schema/procedures/monitor_create_session.sql ───────────────────────────────────────
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

-- ── sql/schema/procedures/monitor_close_session.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.close_session()
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE monitor.session
       SET ended_at = NOW()
     WHERE ended_at IS NULL
       AND started_at = (SELECT MAX(started_at) FROM monitor.session WHERE ended_at IS NULL);
END $$;
COMMENT ON FUNCTION monitor.close_session() IS
    'Close the most recent open session.';

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
        CASE WHEN p_status = 'started' THEN NOW() ELSE NULL END,
        CASE WHEN p_status IN ('completed','failed','skipped') THEN NOW() ELSE NULL END,
        p_error_message
    )
    ON CONFLICT (phase_code) DO UPDATE
        SET status        = EXCLUDED.status,
            started_at    = COALESCE(monitor.phase_status.started_at, EXCLUDED.started_at),
            completed_at  = EXCLUDED.completed_at,
            error_message = EXCLUDED.error_message;
END $$;
COMMENT ON PROCEDURE monitor.update_phase_status(TEXT, TEXT, TEXT) IS
    'Upsert the last-known status of a phase. Status: started, completed, failed, skipped.';

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

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 15: views ──────────────────────────────────────────────────

-- ── sql/schema/views/substrate_dashboard.sql ───────────────────────────────────────
-- High-level "is the substrate healthy" rollup for the CLI's status command.
CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    (SELECT count(*) FROM substrate.entity)            AS total_entities,
    (SELECT count(*) FROM substrate.edge)              AS total_edges,
    (SELECT count(*) FROM substrate.physicality)       AS total_physicality,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'completed') AS phases_completed,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'failed')    AS phases_failed,
    (SELECT max(recorded_at) FROM monitor.substrate_health)                AS last_health_snapshot;
COMMENT ON VIEW monitor.substrate_dashboard IS
    'Single-row rollup of substrate state for the CLI''s status command.';

-- ── sql/schema/views/v_active_runs.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.v_active_runs AS
SELECT
    s.id           AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id) AS comparison_count
  FROM monitor.session s
 WHERE s.ended_at IS NULL
 ORDER BY s.started_at DESC;
COMMENT ON VIEW monitor.v_active_runs IS
    'Sessions currently in progress, with their comparison-event count.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (No Phase 16 hartonomous CREATE EXTENSION. The hartonomous-pg/sql/
--  hartonomous--1.0.sql.in template — containing all C-binding type
--  declarations + substrate.cp_*, ucd_*, text_decompose etc. — is
--  spliced into the assembled extension SQL at build time, BEFORE the
--  Phase 13 functions block. See scripts/build/concat_extension_sql.py.)
