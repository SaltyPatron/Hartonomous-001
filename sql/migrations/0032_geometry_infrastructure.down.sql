-- 0032_geometry_infrastructure.down.sql

DROP VIEW IF EXISTS substrate.edge_trajectory_coverage;
DROP VIEW IF EXISTS substrate.geometry_coverage;
DROP VIEW IF EXISTS substrate.convergence_summary;
DROP FUNCTION IF EXISTS substrate.edge_analogy(bigint, bigint, bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.frayed_edges(text, float8, integer, integer);
DROP FUNCTION IF EXISTS substrate.similar_edges(bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.similar_contours(bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.find_by_hash(bytea, text);
DROP FUNCTION IF EXISTS substrate.entity_labels_recursive(bigint[]);
DROP FUNCTION IF EXISTS substrate.entity_label(bigint);
DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(text, integer);
DROP FUNCTION IF EXISTS substrate.entity_s3_point(bigint);
