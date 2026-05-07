CREATE INDEX idx_inference_metrics_session ON monitor.inference_metrics(session_id, recorded_at DESC);
