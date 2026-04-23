-- Substrate extensions. Order: PostGIS first, then hartonomous (provides 4D types,
-- BLAKE3, traverse_astar, MKL CBWR). hartonomous.so is lazy-loaded by Postgres on
-- the first call to any of its C functions in a backend; backends that only do
-- ingestion INSERTs (no 4D math, no traverse_astar) never load it and never
-- initialize MKL. _PG_init pins MKL CBWR=AUTO|STRICT for backends that DO load it.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
CREATE EXTENSION IF NOT EXISTS hartonomous;
