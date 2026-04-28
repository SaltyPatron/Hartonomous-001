CREATE SCHEMA IF NOT EXISTS monitor;
COMMENT ON SCHEMA monitor IS
    'Operational telemetry: ingestion progress, phase status, inference metrics, error log. Not part of substrate identity.';
