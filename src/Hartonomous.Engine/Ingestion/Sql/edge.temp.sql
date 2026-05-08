CREATE TEMP TABLE IF NOT EXISTS edge_inflight (
    edge_type_id  INT   NOT NULL,
    hash          BYTEA NOT NULL,
    provenance_id INT   NOT NULL,
    geom_wkb      BYTEA
)