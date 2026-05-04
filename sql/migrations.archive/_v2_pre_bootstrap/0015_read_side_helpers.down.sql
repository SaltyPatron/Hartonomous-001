-- Note: substrate.populate_edge_trajectories(INT) is restored to its stub
-- form by re-running migration 0013's @include (out of scope for this down).
-- Drop entity_centroid_4d here — it was added in 0015.
DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
DROP FUNCTION IF EXISTS substrate.entity_neighbors(INT, BYTEA, TEXT);
DROP FUNCTION IF EXISTS substrate.backfill_edge_significance_for_arena(TEXT);
DROP FUNCTION IF EXISTS substrate.prime_edge_significance_for_staging();
DROP FUNCTION IF EXISTS substrate.recompose_text(INT, BYTEA, INT);
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);
DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[], TEXT[]);
DROP FUNCTION IF EXISTS substrate.entity_inbound_edges(INT, BYTEA, TEXT);
DROP FUNCTION IF EXISTS substrate.entity_outbound_edges(INT, BYTEA, TEXT);
DROP FUNCTION IF EXISTS substrate.health_summary();
DELETE FROM substrate.edge_type WHERE code = 'has_constituent';
