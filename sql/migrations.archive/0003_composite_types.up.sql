-- Migration 0003: Composite types
-- Per specs/sql/domains-and-types.md.

CREATE TYPE substrate.significance_state AS (
    mu         substrate.significance_mu,
    sigma      substrate.significance_sigma,
    volatility substrate.significance_volatility,
    games      INTEGER
);
COMMENT ON TYPE substrate.significance_state IS
    'Glicko-2 rating state tuple.';

CREATE TYPE substrate.entity_result AS (
    id             BIGINT,
    hash           substrate.hash_value,
    entity_type_id INT,
    was_created    BOOLEAN
);
COMMENT ON TYPE substrate.entity_result IS
    'Entity upsert result. was_created = false means dedup hit.';

CREATE TYPE substrate.edge_result AS (
    id           BIGINT,
    hash         substrate.hash_value,
    edge_type_id INT,
    was_created  BOOLEAN
);
COMMENT ON TYPE substrate.edge_result IS
    'Edge creation result. was_created = false means duplicate edge deduplicated.';

CREATE TYPE substrate.traversal_step AS (
    entity_id               BIGINT,
    edge_id                 BIGINT,
    edge_type_code          VARCHAR(64),
    role_code               VARCHAR(32),
    step_significance       FLOAT8,
    cumulative_significance FLOAT8
);
COMMENT ON TYPE substrate.traversal_step IS
    'One step in an inference traversal path.';

CREATE TYPE substrate.traversal_path AS (
    steps              substrate.traversal_step[],
    total_significance FLOAT8,
    path_length        INT
);
COMMENT ON TYPE substrate.traversal_path IS
    'Complete inference traversal path with aggregate score.';

CREATE TYPE substrate.ingestion_entity AS (
    hash           substrate.hash_value,
    entity_type_id INT
);
COMMENT ON TYPE substrate.ingestion_entity IS
    'Batch entity submission item.';

CREATE TYPE substrate.ingestion_edge AS (
    hash              substrate.hash_value,
    edge_type_id      INT,
    provenance_id     INT,
    member_entity_ids BIGINT[],
    member_role_ids   INT[],
    member_positions  SMALLINT[],
    geom              GEOMETRY(LINESTRINGZM)
);
COMMENT ON TYPE substrate.ingestion_edge IS
    'Batch edge submission item. Members as parallel arrays.';
