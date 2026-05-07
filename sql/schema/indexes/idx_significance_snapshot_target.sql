CREATE INDEX idx_significance_snapshot_target ON monitor.significance_snapshot(target_kind, target_type_id, target_hash, recorded_at DESC);
