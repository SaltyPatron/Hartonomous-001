# Schema Reference

> **Authority note (2026-05-09):** The `substrate.firefly_consensus` table description below (~line 656) describes a denormalized view that is now **deprecated** by the 2026-05-08 architectural correction. Per [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VII and §X.1, fireflies are POINTZM physicalities attached to existing `word_form` content entities (the species), and consensus is **computed at query time** from Voronoi cells over the species' firefly cluster — NOT stored as a separate `firefly_consensus` composition entity. The `firefly_consensus` table, if present in deployed schema, is on the removal path; new code computes consensus on demand from `substrate.physicality` filtered by the firefly partition and `entity_hash` of the target word_form. Cross-reference [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md) for how `EmbeddingLayerSynthesizer` reads firefly clusters during Substrate Synthesis synthesis.

**Status:** Canonical for the entity/edge/physicality core. The `firefly_consensus` table is deprecated per the authority note above.
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers writing migrations, decomposers, recomposers, or any SQL touching the substrate.

---

## Schemas

The substrate uses several PostgreSQL schemas to separate concerns:

| Schema | Purpose | Write access |
|---|---|---|
| `substrate` | Substrate content: entity, edge, edge_member, physicality, significance | Pipeline + outcome handlers only |
| `ref` | Reference vocabulary: entity_type, edge_type, arena, provenance, etc. | Migrations + admin only |
| `junc` | Junction tables: classification metadata for entities | Decomposers (via pipeline) |
| `staging` | Staging tables for bulk COPY before flush to substrate | Pipeline workers only |
| `monitor` | Operational metrics, ingestion progress, query stats | Monitoring writers |
| `cognitive` | Functions exposed via `hartonomous.*` cognitive surface (note: physical schema, namespace via search_path) | Code-managed via migrations |

## Custom types and domains

```sql
CREATE EXTENSION IF NOT EXISTS hartonomous_pg;  -- native extension; provides point4d, linestring4d, BLAKE3, A*, etc.
CREATE EXTENSION IF NOT EXISTS postgis;          -- 3.6+ for GeometryZM 2D/3D surface

-- Identity hash domain (16 bytes for BLAKE3-128, or 32 for BLAKE3-256)
CREATE DOMAIN ref.hash_value AS bytea
    CHECK (octet_length(VALUE) IN (16, 32));

-- Float8 in safe ranges
CREATE DOMAIN ref.elo_rating AS float8
    CHECK (VALUE >= 0 AND VALUE <= 5000);

CREATE DOMAIN ref.elo_sigma AS float8
    CHECK (VALUE >= 0 AND VALUE <= 1000);

CREATE DOMAIN ref.elo_volatility AS float8
    CHECK (VALUE >= 0 AND VALUE <= 1);

-- Substrate-native types from extension (point4d, linestring4d, multilinestring4d):
-- These are CREATE TYPE declarations in the extension's SQL bootstrap.
```

## Reference tables

```sql
CREATE TABLE ref.entity_type (
    id           SERIAL PRIMARY KEY,
    code         VARCHAR(64) UNIQUE NOT NULL,
    modality     VARCHAR(32) NOT NULL,                 -- text, image, audio, video, model, universal
    description  TEXT
);

CREATE TABLE ref.edge_type (
    id           SERIAL PRIMARY KEY,
    code         VARCHAR(64) UNIQUE NOT NULL,
    category     VARCHAR(32) NOT NULL,                 -- structural, semantic, syntactic, cross_lingual, cross_modal, model_derived, unicode
    arity        INT NOT NULL DEFAULT 2,
    directionality  VARCHAR(16) NOT NULL DEFAULT 'directed', -- directed, undirected
    symmetry        VARCHAR(16),                       -- symmetric, asymmetric
    transitivity    VARCHAR(16),                       -- transitive, intransitive
    inverse_id      INT REFERENCES ref.edge_type(id),
    semantic_family VARCHAR(64),
    description  TEXT
);

CREATE TABLE ref.edge_role (
    id           SERIAL PRIMARY KEY,
    code         VARCHAR(32) UNIQUE NOT NULL            -- source, target, context, mediator, evidence, head, dependent
);

CREATE TABLE ref.physicality_type (
    id              SERIAL PRIMARY KEY,
    code            VARCHAR(64) UNIQUE NOT NULL,
    dimensionality  INT NOT NULL CHECK (dimensionality IN (2, 3, 4)),
    coordinate_shape VARCHAR(16) NOT NULL CHECK (coordinate_shape IN ('point', 'trajectory', 'multi_trajectory')),
    surface         VARCHAR(16) NOT NULL CHECK (surface IN ('postgis', 'native_4d')),
    description     TEXT
);

CREATE TABLE ref.provenance (
    id            SERIAL PRIMARY KEY,
    code          VARCHAR(128) UNIQUE NOT NULL,
    curator_class VARCHAR(64) NOT NULL,
    initial_mu    ref.elo_rating NOT NULL DEFAULT 1500,
    description   TEXT
);

CREATE TABLE ref.significance_context (
    id     SERIAL PRIMARY KEY,
    code   VARCHAR(64) UNIQUE NOT NULL,
    description TEXT
);

-- Classification reference tables
CREATE TABLE ref.pos (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(16) UNIQUE NOT NULL  -- UPOS values: NOUN, VERB, ADJ, ADV, ...
);

CREATE TABLE ref.deprel (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(32) UNIQUE NOT NULL  -- nsubj, obj, amod, ...
);

CREATE TABLE ref.morph_feature (
    id     SERIAL PRIMARY KEY,
    key    VARCHAR(32) NOT NULL,        -- Number, Tense, Case, ...
    value  VARCHAR(32) NOT NULL,
    UNIQUE (key, value)
);

CREATE TABLE ref.sense (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(128) UNIQUE NOT NULL,    -- WordNet sense keys, etc.
    gloss     TEXT,
    lexname   VARCHAR(64),
    pos_id    INT REFERENCES ref.pos(id)
);

CREATE TABLE ref.lexname (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(64) UNIQUE NOT NULL  -- noun.animal, verb.motion, ...
);

CREATE TABLE ref.semantic_relation_type (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(64) UNIQUE NOT NULL  -- WordNet pointer types: hypernym, hyponym, meronym, ...
);

CREATE TABLE ref.general_category (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(8) UNIQUE NOT NULL  -- Lu, Ll, Nd, Po, ...
);

CREATE TABLE ref.script (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(64) UNIQUE NOT NULL  -- Latin, Han, Arabic, ...
);

CREATE TABLE ref.block (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(128) UNIQUE NOT NULL  -- Basic_Latin, CJK_Unified_Ideographs, ...
);

CREATE TABLE ref.break_property (
    id        SERIAL PRIMARY KEY,
    property  VARCHAR(8) NOT NULL,    -- GCB, WB, SB, LB
    value     VARCHAR(32) NOT NULL,
    UNIQUE (property, value)
);

CREATE TABLE ref.language (
    id              SERIAL PRIMARY KEY,
    iso639_3        VARCHAR(3) UNIQUE NOT NULL,
    name            VARCHAR(128) NOT NULL,
    scope           VARCHAR(16),                    -- individual, macrolanguage, special
    type            VARCHAR(16),                    -- living, extinct, historical, constructed, ancient
    family          VARCHAR(128)
);

CREATE TABLE ref.tensor_role (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(64) UNIQUE NOT NULL  -- attention_query, ffn_up, token_embedding, ...
);

CREATE TABLE ref.architecture_class (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(64) UNIQUE NOT NULL  -- text_llm, vision_transformer, multimodal_llm, diffusion_image, ...
);
```

## Substrate content tables

### substrate.entity

```sql
CREATE TABLE substrate.entity (
    hash            ref.hash_value PRIMARY KEY
);

-- Single, non-partitioned. NO entity_type_id column.
-- Structural classification lives in substrate.entity_classification
-- (entity_hash, entity_type_id, provenance_id) so the same content under
-- multiple structural classifications (e.g. dog as both word_form and
-- lemma) is ONE row in substrate.entity with multiple classification rows.
```

### substrate.entity_classification

```sql
CREATE TABLE substrate.entity_classification (
    entity_hash      ref.hash_value NOT NULL REFERENCES substrate.entity(hash),
    entity_type_id   INT NOT NULL REFERENCES ref.entity_type(id),
    provenance_id    INT NOT NULL REFERENCES ref.provenance(id),
    PRIMARY KEY (entity_hash, entity_type_id, provenance_id)
);

CREATE INDEX entity_classification_by_type ON substrate.entity_classification(entity_type_id, entity_hash);
```

### substrate.edge

```sql
CREATE TABLE substrate.edge (
    edge_type_id     INT NOT NULL,
    hash             ref.hash_value NOT NULL,
    geom             geometry(GeometryZM),                  -- nullable; for 2D/3D surface
    linestring4d     hartonomous.linestring4d,               -- nullable; for 4D surface
    provenance_id    INT NOT NULL REFERENCES ref.provenance(id),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (edge_type_id, hash),
    CONSTRAINT edge_one_geom CHECK (
        (geom IS NOT NULL AND linestring4d IS NULL) OR
        (geom IS NULL AND linestring4d IS NOT NULL)
    )
) PARTITION BY LIST (edge_type_id);

-- Per-partition GiST indexes:
-- CREATE INDEX ON substrate.edge_<partition> USING gist (linestring4d hartonomous.linestring4d_gist_ops);
-- CREATE INDEX ON substrate.edge_<partition> USING gist (geom);    -- where applicable
```

### substrate.edge_member

```sql
CREATE TABLE substrate.edge_member (
    edge_type_id     INT NOT NULL,
    edge_hash        ref.hash_value NOT NULL,
    entity_hash      ref.hash_value NOT NULL,
    edge_role_id     INT NOT NULL REFERENCES ref.edge_role(id),
    role_position    SMALLINT NOT NULL,
    PRIMARY KEY (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position),
    FOREIGN KEY (edge_type_id, edge_hash) REFERENCES substrate.edge(edge_type_id, hash),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash)
) PARTITION BY LIST (edge_type_id);

-- Lookup index on entity:
CREATE INDEX edge_member_by_entity ON substrate.edge_member(entity_hash);
```

### substrate.physicality

```sql
CREATE TABLE substrate.physicality (
    physicality_type_id  INT NOT NULL REFERENCES ref.physicality_type(id),
    entity_hash          ref.hash_value NOT NULL,
    geom                 geometry(GeometryZM),
    point4d              hartonomous.point4d,
    linestring4d         hartonomous.linestring4d,
    multilinestring4d    hartonomous.multilinestring4d,
    PRIMARY KEY (physicality_type_id, entity_hash),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash),
    CONSTRAINT physicality_one_value CHECK (
        (geom IS NOT NULL)::int +
        (point4d IS NOT NULL)::int +
        (linestring4d IS NOT NULL)::int +
        (multilinestring4d IS NOT NULL)::int = 1
    )
) PARTITION BY LIST (physicality_type_id);
```

### substrate.entity_significance

```sql
CREATE TABLE substrate.entity_significance (
    context_type_id  INT NOT NULL REFERENCES ref.significance_context(id),
    entity_hash      ref.hash_value NOT NULL,
    mu               ref.elo_rating NOT NULL DEFAULT 1500,
    sigma            ref.elo_sigma NOT NULL DEFAULT 350,
    volatility       ref.elo_volatility NOT NULL DEFAULT 0.06,
    games            INT NOT NULL DEFAULT 0,
    last_update      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (context_type_id, entity_hash),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash)
) PARTITION BY LIST (context_type_id);
```

### substrate.edge_significance

```sql
CREATE TABLE substrate.edge_significance (
    context_type_id  INT NOT NULL REFERENCES ref.significance_context(id),
    edge_type_id     INT NOT NULL,
    edge_hash        ref.hash_value NOT NULL,
    mu               ref.elo_rating NOT NULL DEFAULT 1500,
    sigma            ref.elo_sigma NOT NULL DEFAULT 350,
    volatility       ref.elo_volatility NOT NULL DEFAULT 0.06,
    games            INT NOT NULL DEFAULT 0,
    last_update      TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash),
    FOREIGN KEY (edge_type_id, edge_hash) REFERENCES substrate.edge(edge_type_id, hash)
) PARTITION BY LIST (context_type_id);
```

## Junction tables

```sql
CREATE TABLE junc.entity_pos (
    entity_hash     ref.hash_value NOT NULL,
    pos_id          INT NOT NULL REFERENCES ref.pos(id),
    mu              ref.elo_rating NOT NULL DEFAULT 1500,
    sigma           ref.elo_sigma NOT NULL DEFAULT 350,
    volatility      ref.elo_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, pos_id),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash)
);

CREATE TABLE junc.entity_sense (
    entity_hash     ref.hash_value NOT NULL,
    sense_id        INT NOT NULL REFERENCES ref.sense(id),
    mu              ref.elo_rating NOT NULL DEFAULT 1500,
    sigma           ref.elo_sigma NOT NULL DEFAULT 350,
    volatility      ref.elo_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, sense_id)
);

CREATE TABLE junc.entity_language (
    entity_hash     ref.hash_value NOT NULL,
    language_id     INT NOT NULL REFERENCES ref.language(id),
    PRIMARY KEY (entity_hash, language_id)
);

CREATE TABLE junc.entity_morph_feature (
    entity_hash       ref.hash_value NOT NULL,
    morph_feature_id  INT NOT NULL REFERENCES ref.morph_feature(id),
    PRIMARY KEY (entity_hash, morph_feature_id)
);

CREATE TABLE junc.codepoint_property (
    entity_hash         ref.hash_value PRIMARY KEY,    -- entity_type_id implied to be codepoint
    general_category_id INT REFERENCES ref.general_category(id),
    script_id           INT REFERENCES ref.script(id),
    block_id            INT REFERENCES ref.block(id),
    gcb_id              INT REFERENCES ref.break_property(id),
    wb_id               INT REFERENCES ref.break_property(id),
    sb_id               INT REFERENCES ref.break_property(id),
    lb_id               INT REFERENCES ref.break_property(id),
    combining_class     SMALLINT,
    decomposition_type  VARCHAR(16),
    decomposition_mapping  ref.hash_value[]            -- chain of codepoint atoms in canonical decomposition
);

CREATE TABLE junc.tensor_tensor_role (
    entity_hash     ref.hash_value NOT NULL,
    tensor_role_id  INT NOT NULL REFERENCES ref.tensor_role(id),
    PRIMARY KEY (entity_hash, tensor_role_id)
);

CREATE TABLE junc.model_architecture_class (
    entity_hash             ref.hash_value NOT NULL,
    architecture_class_id   INT NOT NULL REFERENCES ref.architecture_class(id),
    PRIMARY KEY (entity_hash, architecture_class_id)
);

CREATE TABLE junc.pattern_deprel (
    entity_hash     ref.hash_value NOT NULL,            -- typically attention_pattern
    deprel_id       INT NOT NULL REFERENCES ref.deprel(id),
    mu              ref.elo_rating NOT NULL DEFAULT 1500,
    sigma           ref.elo_sigma NOT NULL DEFAULT 350,
    volatility      ref.elo_volatility NOT NULL DEFAULT 0.06,
    games           INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, deprel_id)
);
```

## Staging tables

```sql
-- One per record type the pipeline emits. Drained continuously to substrate.* tables.

CREATE UNLOGGED TABLE staging.entity_in (
    hash            ref.hash_value NOT NULL,
    provenance_id   INT
);

CREATE UNLOGGED TABLE staging.edge_in ( ... );
CREATE UNLOGGED TABLE staging.edge_member_in ( ... );
CREATE UNLOGGED TABLE staging.physicality_in ( ... );
CREATE UNLOGGED TABLE staging.junction_<table>_in ( ... );  -- per junction table
```

## Monitoring tables

```sql
CREATE TABLE monitor.ingestion_progress (
    id            BIGSERIAL PRIMARY KEY,
    decomposer    VARCHAR(64) NOT NULL,
    phase         VARCHAR(64) NOT NULL,
    file_path     TEXT,
    started_at    TIMESTAMPTZ NOT NULL,
    last_progress TIMESTAMPTZ NOT NULL,
    entities_emitted   BIGINT NOT NULL DEFAULT 0,
    edges_emitted      BIGINT NOT NULL DEFAULT 0,
    duplicates_skipped BIGINT NOT NULL DEFAULT 0,
    error_message TEXT
);

CREATE VIEW monitor.substrate_health AS
SELECT
    (SELECT count(*) FROM substrate.entity) AS total_entities,
    (SELECT count(*) FROM substrate.edge) AS total_edges,
    (SELECT count(*) FROM substrate.physicality) AS total_physicality_rows,
    (SELECT count(DISTINCT context_type_id) FROM substrate.edge_significance) AS active_arenas
    -- Plus per-entity-type counts, per-edge-type counts, per-arena distributions, etc.
;

CREATE TABLE monitor.inference_metrics (
    id              BIGSERIAL PRIMARY KEY,
    started_at      TIMESTAMPTZ NOT NULL,
    elapsed_ms      FLOAT8 NOT NULL,
    paths_returned  INT NOT NULL,
    nodes_visited   INT NOT NULL,
    elapsed_step    JSONB NOT NULL,                -- per-step latency
    arena_recipe_hash  ref.hash_value,
    governance_violations TEXT[],
    response_entity_hash ref.hash_value
);
```

## Indexing strategy

The substrate's hot-path operations dictate index strategy:

| Index | Purpose | Type |
|---|---|---|
| `substrate.entity (hash)` | Identity lookup, deduplication | B-tree (PK) |
| `substrate.entity_classification (entity_hash, entity_type_id, provenance_id)` | Per-content classification lookup | B-tree (PK) |
| `substrate.entity_classification (entity_type_id, entity_hash)` | All-of-type enumeration | B-tree |
| `substrate.edge (edge_type_id, hash)` | Edge identity, deduplication | B-tree (PK) |
| `substrate.edge_member (entity_hash)` | "What edges involve this entity?" | B-tree |
| `substrate.physicality (physicality_type_id, entity_hash)` per partition | Centroid lookup by entity | B-tree (PK) |
| `substrate.physicality.linestring4d` per partition | 4D shape similarity | GiST `linestring4d_gist_ops` |
| `substrate.physicality.point4d` per partition | 4D nearest-neighbor | GiST `point4d_gist_ops`, SP-GiST optional |
| `substrate.physicality.geom` per partition | 2D/3D spatial | GiST default opclass (PostGIS) |
| `substrate.edge_significance (context_type_id, edge_type_id, edge_hash)` | Significance lookup in arena | B-tree (PK) |
| `junc.entity_pos`, etc. | Classification lookups | B-tree (PK) on `(entity_hash, classification_id)` |

Index creation per partition during seed phases. Initial indexes are dropped before bulk-load and rebuilt after, for ingestion throughput. Partition pruning happens automatically when queries filter by partition key (edge_type_id, context_type_id, physicality_type_id). `substrate.entity` itself is non-partitioned — the hash is its own identity — with classification recorded separately in `substrate.entity_classification`.

## Foreign key strategy

FKs enforce referential integrity but can slow bulk-load. The convention:

- During seed phases, FKs may be dropped, bulk-loaded, then recreated. The pipeline's `process()` flow handles this.
- During incremental ingestion (post-seed), FKs are enforced at insert.
- Composite FKs from `edge_member` to `edge` and from `edge_member` to `entity` are preserved (per-partition; partition-aware FK in PostgreSQL 18+).

## Multi-tenancy and audit tables

These tables implement the multi-tenancy and audit-chain models specified in `10-architecture/16-multi-tenancy.md` and `10-architecture/17-audit-chain.md`.

### `ref.tenant`

```sql
CREATE TABLE ref.tenant (
    tenant_id                UUID PRIMARY KEY,
    display_name             VARCHAR(256) NOT NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    subscription_tier        VARCHAR(64) NOT NULL,
    data_residency_constraint VARCHAR(64),                    -- e.g., 'EU', 'US-only'; NULL = no constraint
    default_inference_arenas TEXT[],
    public_key_pem           TEXT,                             -- optional: tenant's own signing key for content attestation
    is_active                BOOLEAN NOT NULL DEFAULT true,
    deactivated_at           TIMESTAMPTZ
);

CREATE INDEX tenant_active ON ref.tenant(is_active) WHERE is_active = true;
```

### `ref.sharing_group`

```sql
CREATE TABLE ref.sharing_group (
    sharing_group_id  UUID PRIMARY KEY,
    display_name      VARCHAR(256) NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    access_terms_url  TEXT,
    is_active         BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE ref.sharing_group_member (
    sharing_group_id  UUID NOT NULL REFERENCES ref.sharing_group(sharing_group_id),
    tenant_id         UUID NOT NULL REFERENCES ref.tenant(tenant_id),
    joined_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    share_classes     TEXT[] NOT NULL,                          -- which provenance classes this tenant shares into the group
    PRIMARY KEY (sharing_group_id, tenant_id)
);
```

### `substrate.tenant_arena_rating`

Per-tenant divergent Glicko-2 ratings. Sparsely populated — most tenants don't diverge.

```sql
CREATE TABLE substrate.tenant_arena_rating (
    tenant_id          UUID NOT NULL REFERENCES ref.tenant(tenant_id),
    context_type_id    INT NOT NULL REFERENCES ref.significance_context(id),
    edge_type_id       INT NOT NULL,
    edge_hash          ref.hash_value NOT NULL,
    mu                 ref.elo_rating NOT NULL DEFAULT 1500,
    sigma              ref.elo_sigma NOT NULL DEFAULT 350,
    volatility         ref.elo_volatility NOT NULL DEFAULT 0.06,
    games              INT NOT NULL DEFAULT 0,
    last_update        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, context_type_id, edge_type_id, edge_hash),
    FOREIGN KEY (edge_type_id, edge_hash) REFERENCES substrate.edge(edge_type_id, hash)
) PARTITION BY HASH (tenant_id);

CREATE INDEX tenant_arena_rating_lookup ON substrate.tenant_arena_rating
    (context_type_id, edge_type_id, edge_hash);
```

### `substrate.outcome_event`

```sql
CREATE TABLE substrate.outcome_event (
    outcome_id            UUID PRIMARY KEY,
    inference_trace_id    UUID NOT NULL,
    outcome_class         VARCHAR(32) NOT NULL CHECK (outcome_class IN
        ('validated', 'refuted', 'partial', 'irrelevant', 'corroborated', 'contradicted')),
    arena                 VARCHAR(128) NOT NULL,
    submitted_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    submitted_by          VARCHAR(256),
    tenant_id             UUID REFERENCES ref.tenant(tenant_id),
    rationale             JSONB,
    rating_period_id      UUID,                                  -- NULL until applied
    applied_at            TIMESTAMPTZ                            -- NULL until applied
);

CREATE INDEX outcome_event_pending ON substrate.outcome_event(arena, submitted_at)
    WHERE rating_period_id IS NULL;

CREATE INDEX outcome_event_by_trace ON substrate.outcome_event(inference_trace_id);
```

### `substrate.rating_period`

```sql
CREATE TABLE substrate.rating_period (
    rating_period_id  UUID PRIMARY KEY,
    arena             VARCHAR(128) NOT NULL,
    started_at        TIMESTAMPTZ NOT NULL,
    completed_at      TIMESTAMPTZ NOT NULL,
    outcomes_applied  BIGINT NOT NULL,
    edges_updated     BIGINT NOT NULL
);

CREATE INDEX rating_period_by_arena ON substrate.rating_period(arena, started_at DESC);
```

### `substrate.inference_trace`

```sql
CREATE TABLE substrate.inference_trace (
    trace_id           UUID PRIMARY KEY,
    recipe_id          UUID,                                     -- the recipe used; nullable for ad-hoc inferences
    tenant_id          UUID REFERENCES ref.tenant(tenant_id),
    session_id         UUID,
    started_at         TIMESTAMPTZ NOT NULL,
    completed_at       TIMESTAMPTZ NOT NULL,
    seed_entity_type_id INT,
    seed_entity_hash   ref.hash_value,
    output_entity_hash ref.hash_value,
    output_entity_type_id INT,
    path_chain_hash    ref.hash_value NOT NULL,                  -- BLAKE3 of the visited-edge sequence
    elapsed_ms         INT NOT NULL,
    nodes_visited      INT NOT NULL,
    parent_chain_hash_in   ref.hash_value,
    parent_chain_hash_out  ref.hash_value
);

CREATE INDEX trace_by_tenant ON substrate.inference_trace(tenant_id, started_at DESC);
CREATE INDEX trace_by_recipe ON substrate.inference_trace(recipe_id, started_at DESC);
```

### `substrate.audit_trace`

```sql
CREATE TABLE substrate.audit_trace (
    audit_id              UUID PRIMARY KEY,
    operation_type        VARCHAR(64) NOT NULL,                  -- 'ingestion_run', 'recipe_parse', 'macro_ooda_decision', 'tenant_op', etc.
    invoking_principal    VARCHAR(256) NOT NULL,                 -- tenant_id, operator_id, recipe_id, etc.
    inputs                JSONB,
    outputs               JSONB,
    started_at            TIMESTAMPTZ NOT NULL,
    completed_at          TIMESTAMPTZ NOT NULL,
    outcome_class         VARCHAR(32) NOT NULL CHECK (outcome_class IN
        ('success', 'partial', 'failed')),
    failure_reason        TEXT,
    parent_chain_hash_in  ref.hash_value,
    parent_chain_hash_out ref.hash_value
);

CREATE INDEX audit_trace_by_principal ON substrate.audit_trace(invoking_principal, started_at DESC);
CREATE INDEX audit_trace_by_operation ON substrate.audit_trace(operation_type, started_at DESC);
```

### `substrate.ingestion_run`

```sql
CREATE TABLE substrate.ingestion_run (
    run_id                  UUID PRIMARY KEY,
    decomposer_name         VARCHAR(64) NOT NULL,
    decomposer_version      VARCHAR(32) NOT NULL,
    source_uri              TEXT NOT NULL,
    source_hash             ref.hash_value,                      -- of the source corpus, where computable
    started_at              TIMESTAMPTZ NOT NULL,
    completed_at            TIMESTAMPTZ NOT NULL,
    parent_chain_hash_in    ref.hash_value NOT NULL,
    parent_chain_hash_out   ref.hash_value NOT NULL,
    merkle_root             ref.hash_value NOT NULL,
    signed_attestation      BYTEA,                               -- cryptographic signature
    operator_signing_key_id VARCHAR(64),
    entities_emitted        BIGINT NOT NULL DEFAULT 0,
    edges_emitted           BIGINT NOT NULL DEFAULT 0,
    atoms_emitted           BIGINT NOT NULL DEFAULT 0,
    deduplications          BIGINT NOT NULL DEFAULT 0
);

CREATE INDEX ingestion_run_by_source ON substrate.ingestion_run(source_uri, started_at DESC);
CREATE INDEX ingestion_run_chain ON substrate.ingestion_run(parent_chain_hash_in);
```

### `substrate.frayed_edge_candidate`

```sql
CREATE TABLE substrate.frayed_edge_candidate (
    candidate_id            UUID PRIMARY KEY,
    arena                   VARCHAR(128) NOT NULL,
    entity_a_type_id        INT NOT NULL,
    entity_a_hash           ref.hash_value NOT NULL,
    entity_b_type_id        INT NOT NULL,
    entity_b_hash           ref.hash_value NOT NULL,
    confidence              FLOAT NOT NULL CHECK (confidence BETWEEN 0 AND 1),
    proximity_score         FLOAT NOT NULL,
    neighborhood_score      FLOAT NOT NULL,
    trajectory_score        FLOAT NOT NULL,
    common_neighbors_count  INT NOT NULL,
    implicating_traj_count  INT NOT NULL,
    detected_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    detected_by_run_id      UUID REFERENCES substrate.ingestion_run(run_id),
    resolution_status       VARCHAR(32) NOT NULL DEFAULT 'open' CHECK (resolution_status IN
        ('open', 'validated', 'refuted', 'stale')),
    resolved_at             TIMESTAMPTZ
);

CREATE INDEX frayed_candidate_by_arena ON substrate.frayed_edge_candidate
    (arena, confidence DESC) WHERE resolution_status = 'open';
```

### `substrate.firefly_consensus`

The composition entity for Voronoi consensus over firefly clouds. Implementation note: the `firefly_consensus` is also represented as a regular composition in `substrate.entity` and `substrate.edge_member`; this table is a denormalized view for performant consensus-update scheduling.

```sql
CREATE TABLE substrate.firefly_consensus (
    consensus_id            UUID PRIMARY KEY,
    arena                   VARCHAR(128) NOT NULL,
    conceptual_position     JSONB NOT NULL,                      -- (architecture_handler, slot_descriptor)
    tier                    VARCHAR(16) NOT NULL CHECK (tier IN
        ('weight', 'row', 'col', 'head', 'layer', 'block')),
    centroid_4d             hartonomous.point4d NOT NULL,
    physicality_4d          hartonomous.linestring4d NOT NULL,
    contributing_count      INT NOT NULL,
    max_distance            FLOAT NOT NULL,
    median_distance         FLOAT NOT NULL,
    bimodality_flag         BOOLEAN NOT NULL DEFAULT false,
    dispersion              FLOAT NOT NULL,
    supersedes_consensus_id UUID REFERENCES substrate.firefly_consensus(consensus_id),
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (arena, conceptual_position, tier, supersedes_consensus_id)
);

CREATE INDEX consensus_by_arena_position ON substrate.firefly_consensus
    (arena, conceptual_position, tier, created_at DESC);
```

## Row-level security policies

Per `10-architecture/16-multi-tenancy.md`, every substrate table that holds tenant-scoped state has an RLS policy enforcing visibility:

```sql
ALTER TABLE substrate.entity ENABLE ROW LEVEL SECURITY;

CREATE POLICY entity_visibility ON substrate.entity
    FOR SELECT
    USING (
        substrate.is_visible_to_current_tenant(hash)
    );
```

The `substrate.is_visible_to_current_tenant` function consults the entity's provenance and the current tenant's session-bound visibility rights. The function is implemented in C in `hartonomous_pg` for performance.

Equivalent policies exist on `substrate.edge`, `substrate.edge_member`, `substrate.physicality`, `substrate.entity_significance`, `substrate.edge_significance`, `substrate.tenant_arena_rating`, `substrate.outcome_event`, `substrate.inference_trace`, `substrate.audit_trace`. Operator role bypasses RLS for support purposes; bypass is itself audited.

## Constraints summary

| Constraint type | Where | Purpose |
|---|---|---|
| `PRIMARY KEY` | All substrate tables | Identity uniqueness |
| `FOREIGN KEY` | `edge_member` → `edge`, `edge_member` → `entity`, `physicality` → `entity`, `*_significance` → `edge`/`entity`, `tenant_arena_rating` → `tenant` and `edge` | Referential integrity |
| `CHECK` (one-of) | `edge`, `physicality` | Enforce that the populated geometry column matches the type's surface |
| `CHECK` (range) | `ref.elo_*` domains, `confidence` in frayed-edge candidate | Prevent out-of-range values |
| `UNIQUE` | Code columns in ref tables, `(arena, conceptual_position, tier, supersedes)` in firefly_consensus | Catalog uniqueness |
| `RLS` policy | All tenant-scoped tables | Multi-tenancy visibility |

## Partition strategy

| Table | Partition key | Rationale |
|---|---|---|
| `substrate.entity` | (none) | Non-partitioned; PK is `hash` only. Classification lives in `substrate.entity_classification` |
| `substrate.edge` | LIST (edge_type_id) | Same as entity; recipes filter by edge_type heavily |
| `substrate.edge_member` | LIST (edge_type_id) | Co-locate with parent edge for locality |
| `substrate.physicality` | LIST (physicality_type_id) | Geometry types differ; per-type GiST opclass requires per-partition indexing |
| `substrate.entity_significance` | LIST (context_type_id) | Per-arena arrays are common; partition pruning by arena |
| `substrate.edge_significance` | LIST (context_type_id) | Same as entity significance |
| `substrate.tenant_arena_rating` | HASH (tenant_id) | Distribute tenant data; avoid hot partitions |

Partition creation happens at migration time for the seed entity types and edge types; subsequent additions create partitions on demand via `entity_type_addition.sql` and `edge_type_addition.sql` migration templates.

## Index strategy (extended)

In addition to the basic indexes already listed:

| Index | Purpose | Type |
|---|---|---|
| `outcome_event(arena, submitted_at) WHERE rating_period_id IS NULL` | Pending-outcome queue scan | Partial B-tree |
| `frayed_edge_candidate(arena, confidence DESC) WHERE resolution_status = 'open'` | Macro-OODA candidate ranking | Partial B-tree |
| `inference_trace(tenant_id, started_at DESC)` | Tenant trace timeline | B-tree |
| `audit_trace(invoking_principal, started_at DESC)` | Per-principal audit timeline | B-tree |
| `firefly_consensus(arena, conceptual_position, tier, created_at DESC)` | Latest consensus per cloud | B-tree |
| `tenant_arena_rating(context_type_id, edge_type_id, edge_hash)` | Per-arena, per-edge tenant overrides | B-tree |

Per-partition GiST indexes on `physicality.point4d`, `physicality.linestring4d`, and `physicality.geom` are required for substantive 4D geometric queries (Fréchet, Hausdorff, A* heuristic). The `hartonomous_pg` extension provides custom GiST operator classes that index 4D geometries efficiently on S³.

## Cross-references

- Architectural rationale for each schema choice: `10-architecture/00-overview.md` and the three pillar documents
- Native extension custom types (`point4d`, `linestring4d`, GiST opclasses): `20-technical/01-native-extension-api.md`
- Multi-tenancy model (RLS, sharing groups, residency): `10-architecture/16-multi-tenancy.md`
- Audit chain (cryptographic chain commitment over `parent_chain_hash`): `10-architecture/17-audit-chain.md`
- Continuous learning loop (rating periods, outcome events): `10-architecture/18-continuous-learning-loop.md`
- Track 1/Track 2 model ingestion (firefly_consensus, tensor compositions): `10-architecture/11-track1-track2-model-ingestion.md`
- Voronoi consensus (firefly_consensus update mechanism): `10-architecture/12-voronoi-consensus.md`
- Frayed-edge detection (frayed_edge_candidate population): `10-architecture/13-frayed-edge-detection.md`
- Migration ordering and policy: `40-process/checklists/03-schema-migration-checklist.md`
- Per-table examples and queries: `20-technical/0X-*.md` per topic
