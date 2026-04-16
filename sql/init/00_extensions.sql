-- Development init: enable extensions required by the substrate.
-- Runs once when the Postgres container initializes an empty data directory.

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
CREATE EXTENSION IF NOT EXISTS hartonomous;
