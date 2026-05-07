CREATE INDEX idx_ingestion_progress_recent ON monitor.ingestion_progress(recorded_at DESC);
