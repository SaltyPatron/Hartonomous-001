-- Substrate extensions. Order: PostGIS first, then hartonomous (provides 4D types,
-- BLAKE3, traverse_astar, MKL CBWR). hartonomous.so is lazy-loaded by Postgres on
-- the first call to any of its C functions in a backend; backends that only do
-- ingestion INSERTs (no 4D math, no traverse_astar) never load it and never
-- initialize MKL. _PG_init pins MKL CBWR=AUTO|STRICT for backends that DO load it.
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
-- hartonomous depends on btree_gist (and postgis, already declared above);
-- use CASCADE so first-boot install pulls btree_gist in instead of failing
-- with "required extension btree_gist is not installed".
CREATE EXTENSION IF NOT EXISTS hartonomous CASCADE;
