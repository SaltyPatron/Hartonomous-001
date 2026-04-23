-- 0031_fix_srid_in_centroid_calculations.down.sql
-- Revert to 0032 versions (without ST_SetSRID wrappers).
-- The 0032 versions are CREATE OR REPLACE so they'll be restored by re-running 0032.

-- Drop and re-create from 0032 would be complex; since these are all
-- CREATE OR REPLACE, the down migration simply needs to restore the old versions.
-- In practice, rolling back means re-applying 0032's definitions.

-- For safety, just drop and let 0032 recreate:
DROP FUNCTION IF EXISTS substrate.edge_analogy(bigint, bigint, bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.frayed_edges(text, float8, integer, integer);
DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(text, integer);
DROP FUNCTION IF EXISTS substrate.entity_s3_point(bigint);
