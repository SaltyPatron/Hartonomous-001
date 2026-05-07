CREATE INDEX idx_comparison_event_session ON monitor.comparison_event(session_id, recorded_at DESC);
