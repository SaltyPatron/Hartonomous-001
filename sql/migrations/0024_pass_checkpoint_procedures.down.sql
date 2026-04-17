-- 0024_pass_checkpoint_procedures.down.sql
DROP FUNCTION IF EXISTS substrate.get_completed_model_passes(BIGINT);
DROP FUNCTION IF EXISTS substrate.upsert_model_pass_checkpoint(BIGINT, VARCHAR, BIGINT, BIGINT, TEXT, BOOLEAN);
