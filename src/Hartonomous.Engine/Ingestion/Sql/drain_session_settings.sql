-- Per-worker session tuning for the streaming ingestion pipeline.
--
-- temp_buffers: pg_temp.X_inflight staging tables fit comfortably in 256 MB
-- per worker; bumping above default avoids spilling to disk during COPY +
-- INSERT-SELECT drain.
SET temp_buffers = '256MB';

-- synchronous_commit: bulk seed phases produce millions of rows and Glicko
-- updates per minute; the default ON setting forces every chunk commit to
-- block on WALWrite + WalSync (visible as LWLock WALWrite waits across the
-- worker pool). The substrate is rebuildable from sources (Law #6 / seed
-- determinism), so OFF here trades durability across an unclean shutdown
-- for ~5-10x higher ingest throughput. The seed orchestrator handles
-- restart from monitor.phase_status, and a torn shutdown re-runs the
-- affected phase. Inference / synthesis-time writes use the global default
-- (ON) since those connections come through a different code path.
SET synchronous_commit = OFF;
