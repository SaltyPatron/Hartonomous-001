CREATE TABLE substrate.significance_context (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.significance_context IS
    'Open-vocabulary arena definitions. Codes can be added at runtime; significance must auto-prime against every existing arena (rule 45 AP-1).';
