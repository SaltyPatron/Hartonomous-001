-- Down migration: revert to the original (broken) traverse_astar.
-- In practice, you'd re-apply 0026's version, but since 0026 itself is corrupted,
-- we just drop the function. A fresh migrate up from 0026 would recreate it.
-- No-op: the function signature is unchanged, CREATE OR REPLACE is idempotent.
-- To truly revert, rollback to 0025 and re-apply.
SELECT 1; -- no-op placeholder
