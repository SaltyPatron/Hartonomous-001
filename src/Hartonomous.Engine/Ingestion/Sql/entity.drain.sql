INSERT INTO substrate.entity (hash)
SELECT DISTINCT hash FROM pg_temp.entity_inflight
ON CONFLICT (hash) DO NOTHING