# SQL Domains and Custom Types

**Status**: ✅ Complete

Custom SQL domains enforce validation at the database level. Composite types enable structured return types from functions and stored procedures. All live in the `substrate` schema.

---

## Domains

### hash_value

BLAKE3 content hash. 32 bytes, always present.

```sql
CREATE DOMAIN substrate.hash_value AS BYTEA
    CONSTRAINT hash_value_length CHECK (octet_length(VALUE) = 32);

COMMENT ON DOMAIN substrate.hash_value IS 'BLAKE3 256-bit hash. Used for entity.hash and edge.hash.';
```

**Used by**: `entity.hash`, `edge.hash`.
**Rationale**: BYTEA without length constraint would accept any byte sequence. The domain enforces that every hash is exactly 32 bytes — a wrong-length hash is a defect in the hashing code, caught at INSERT.

### significance_mu

Glicko-2 rating mean. No hard range constraint — the math allows any real value, but extreme outliers indicate a defect.

```sql
CREATE DOMAIN substrate.significance_mu AS FLOAT8;

COMMENT ON DOMAIN substrate.significance_mu IS 'Glicko-2 rating mean. Typical range 0–3000, trust priors 1000–2000.';
```

**Used by**: `significance.mu`, `entity_pos.mu`, `entity_sense.mu`, `pattern_deprel.mu`, `provenance.initial_mu`.
**No CHECK constraint**: Glicko-2 mathematics do not bound mu. Arbitrarily low values are valid (entity that lost every comparison). Monitoring detects anomalous values rather than constraining them.

### significance_sigma

Glicko-2 rating uncertainty. Must be positive — zero uncertainty is mathematically invalid (would mean infinite confidence).

```sql
CREATE DOMAIN substrate.significance_sigma AS FLOAT8
    CONSTRAINT sigma_positive CHECK (VALUE > 0);

COMMENT ON DOMAIN substrate.significance_sigma IS 'Glicko-2 rating uncertainty. Decreases as evidence accumulates. Must be > 0.';
```

**Used by**: `significance.sigma`, `entity_pos.sigma`, `entity_sense.sigma`, `pattern_deprel.sigma`.
**Rationale**: sigma = 0 breaks the Glicko-2 formula (division by zero in g(sigma)). This is a mathematical invariant, not a business rule.

### significance_volatility

Glicko-2 meta-uncertainty. Must be positive.

```sql
CREATE DOMAIN substrate.significance_volatility AS FLOAT8
    CONSTRAINT volatility_positive CHECK (VALUE > 0);

COMMENT ON DOMAIN substrate.significance_volatility IS 'Glicko-2 meta-uncertainty (how much sigma is expected to change). Must be > 0.';
```

**Used by**: `significance.volatility`, `entity_pos.volatility`, `entity_sense.volatility`, `pattern_deprel.volatility`.

### tier_number

Entity tier. 0 = atom, positive integers for compositions.

```sql
CREATE DOMAIN substrate.tier_number AS INTEGER
    CONSTRAINT tier_non_negative CHECK (VALUE >= 0);

COMMENT ON DOMAIN substrate.tier_number IS 'Entity tier. 0 = atom (codepoint). Emergent from reference depth.';
```

**Used by**: computed columns, function return types, partitioning calculations.
**Rationale**: negative tiers are nonsensical — prevents accidental -1 from buggy tier computation.

### rle_count

Run-length encoding count in the sequence table.

```sql
CREATE DOMAIN substrate.rle_count AS INTEGER
    CONSTRAINT rle_at_least_one CHECK (VALUE >= 1);

COMMENT ON DOMAIN substrate.rle_count IS 'RLE occurrence count in sequence. 100 identical blue pixels = one reference with count=100.';
```

**Used by**: `sequence.count`.
**Rationale**: count = 0 means "no occurrences" which is a deletion, not a sequence entry. count < 0 is nonsensical.

### ordinal_position

Position within an ordered sequence. 0-indexed.

```sql
CREATE DOMAIN substrate.ordinal_position AS INTEGER
    CONSTRAINT position_non_negative CHECK (VALUE >= 0);

COMMENT ON DOMAIN substrate.ordinal_position IS '0-indexed ordinal position in a parent composition.';
```

**Used by**: `sequence.position`, `edge_member.position`.

### code_value

Non-empty reference table code.

```sql
CREATE DOMAIN substrate.code_value AS VARCHAR(128)
    CONSTRAINT code_not_empty CHECK (LENGTH(TRIM(VALUE)) > 0);

COMMENT ON DOMAIN substrate.code_value IS 'Reference table code column. Never empty or whitespace-only.';
```

**Used by**: All reference table `code` columns.
**Rationale**: an empty code is a defect in seed data. Whitespace-only codes would cause invisible collisions.

---

## Composite Types

### significance_state

Glicko-2 state tuple. Return type for functions that compute or retrieve significance.

```sql
CREATE TYPE substrate.significance_state AS (
    mu         substrate.significance_mu,
    sigma      substrate.significance_sigma,
    volatility substrate.significance_volatility,
    games      INTEGER
);

COMMENT ON TYPE substrate.significance_state IS 'Glicko-2 rating state tuple. Used as return type for significance functions.';
```

**Used by**: `SignificanceUpdater` return values, significance computation functions.

### entity_result

Return type for entity upsert operations.

```sql
CREATE TYPE substrate.entity_result AS (
    id             BIGINT,
    hash           substrate.hash_value,
    entity_type_id INT,
    was_created    BOOLEAN
);

COMMENT ON TYPE substrate.entity_result IS 'Entity upsert result. was_created = false means the entity already existed (dedup hit).';
```

**Used by**: `substrate.upsert_entity()` stored procedure.
**Rationale**: Callers need both the entity ID (for subsequent edge/junction creation) and whether it was new (for monitoring entity-created counts).

### edge_result

Return type for edge creation operations.

```sql
CREATE TYPE substrate.edge_result AS (
    id           BIGINT,
    hash         substrate.hash_value,
    edge_type_id INT,
    was_created  BOOLEAN
);

COMMENT ON TYPE substrate.edge_result IS 'Edge creation result. was_created = false means a duplicate edge was deduplicated.';
```

**Used by**: `substrate.create_edge()` stored procedure.

### traversal_step

One step in a traversal path.

```sql
CREATE TYPE substrate.traversal_step AS (
    entity_id              BIGINT,
    edge_id                BIGINT,
    edge_type_code         VARCHAR(64),
    role_code              VARCHAR(32),
    step_significance      FLOAT8,
    cumulative_significance FLOAT8
);

COMMENT ON TYPE substrate.traversal_step IS 'One step in an inference traversal path. Ordered array of these = the explanation trace.';
```

**Used by**: Traversal CTE return type, inference trace queries.

### traversal_path

Complete traversal path with cumulative significance.

```sql
CREATE TYPE substrate.traversal_path AS (
    steps                   substrate.traversal_step[],
    total_significance      FLOAT8,
    path_length             INT
);

COMMENT ON TYPE substrate.traversal_path IS 'Complete inference traversal path. Array of steps with aggregate score.';
```

**Used by**: Path comparison functions, inference result ranking.

### ingestion_entity

Structure for batch entity submission.

```sql
CREATE TYPE substrate.ingestion_entity AS (
    hash           substrate.hash_value,
    entity_type_id INT
);

COMMENT ON TYPE substrate.ingestion_entity IS 'Batch entity submission item. Hash + type. Pipeline submits arrays of these.';
```

**Used by**: Batch ingestion stored procedure.

### ingestion_edge

Structure for batch edge submission.

```sql
CREATE TYPE substrate.ingestion_edge AS (
    hash           substrate.hash_value,
    edge_type_id   INT,
    provenance_id  INT,
    member_entity_ids BIGINT[],
    member_role_ids   INT[],
    member_positions  SMALLINT[],
    geom           GEOMETRY(LINESTRINGZM)
);

COMMENT ON TYPE substrate.ingestion_edge IS 'Batch edge submission item. Members specified as parallel arrays (entity_ids, role_ids, positions).';
```

**Used by**: Batch ingestion stored procedure.

---

## Creation Order

1. Domains first (no dependencies between domains)
2. `significance_state` type (depends on `significance_mu`, `significance_sigma`, `significance_volatility` domains)
3. `entity_result` type (depends on `hash_value` domain)
4. `edge_result` type (depends on `hash_value` domain)
5. `traversal_step` type (no domain dependencies)
6. `traversal_path` type (depends on `traversal_step` type)
7. `ingestion_entity` type (depends on `hash_value` domain)
8. `ingestion_edge` type (depends on `hash_value` domain)

All domains and types must be created before table DDL that references them.
