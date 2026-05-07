CREATE INDEX idx_inference_metrics_recent  ON monitor.inference_metrics(recorded_at DESC);
