# Deployment — Production Reference Architecture

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Operations engineers deploying the substrate, anyone planning hardware sizing, anyone designing high-availability or multi-region topologies.

---

## Production stack

- **PostgreSQL 18.x** (verified 18.1).
- **PostGIS 3.6.x** (verified 3.6.3).
- **`hartonomous_pg` extension** (built from `ext/hartonomous_pg/`).
- **Operating system:** Linux (Ubuntu 24.04 LTS or RHEL 9 baseline; FreeBSD experimental).
- **Storage:** NVMe SSD with sufficient IOPS for substrate workload (see sizing).
- **Memory:** sufficient for shared buffers + per-connection working memory (see sizing).
- **CPU:** modern x86-64 or ARM64 with AVX-512 (or SVE) for vectorized 4D operators.
- **Network:** 10 GbE minimum for multi-node deployments.

## Hardware sizing

The substrate's hardware needs scale with three factors:

- **Substrate state size** (atoms + compositions + edges).
- **Inference throughput** (queries per second).
- **Ingestion throughput** (decomposer pipelines running concurrently).

| Tier | Substrate state | Inference QPS | Memory | Cores | Storage |
|---|---|---|---|---|---|
| Development | < 100 GB | < 10 QPS | 32 GB | 8-16 | 1 TB NVMe |
| Single-node production | < 2 TB | < 1000 QPS | 128 GB | 32-64 | 10 TB NVMe |
| Mid-scale | < 10 TB | < 10K QPS | 512 GB-1 TB | 64-128 | 50 TB NVMe + replication |
| Large-scale | > 10 TB | > 10K QPS | sharded; 1 TB+ per shard | 128+ per shard | 100 TB+ per shard |

Reference single-node production deployment details below.

## Reference deployment (single-node production)

```yaml
# docker-compose.yml
services:
  postgres:
    image: hartonomous-postgres:18.1
    ports:
      - "5433:5432"
    environment:
      POSTGRES_USER: hartonomous
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: hartonomous
    volumes:
      - pgdata:/var/lib/postgresql/data
      - pgbackup:/var/lib/postgresql/backup
    shm_size: 32g
    command: >
      postgres
        -c shared_buffers=48GB
        -c effective_cache_size=96GB
        -c work_mem=512MB
        -c maintenance_work_mem=8GB
        -c wal_buffers=128MB
        -c max_wal_size=32GB
        -c min_wal_size=8GB
        -c checkpoint_timeout=30min
        -c checkpoint_completion_target=0.9
        -c wal_compression=zstd
        -c synchronous_commit=on
        -c max_connections=200
        -c max_worker_processes=64
        -c max_parallel_workers=64
        -c max_parallel_workers_per_gather=16
        -c max_parallel_maintenance_workers=16
        -c random_page_cost=1.1
        -c effective_io_concurrency=256
        -c maintenance_io_concurrency=256
        -c shared_preload_libraries=hartonomous_pg,pg_cron,pg_stat_statements
        -c cron.database_name=hartonomous
        -c log_min_duration_statement=1000
        -c log_checkpoints=on
        -c log_connections=on
        -c log_disconnections=on
        -c log_lock_waits=on
        -c log_temp_files=0
        -c track_io_timing=on
        -c track_functions=pl
        -c jit=off

volumes:
  pgdata:
    driver: local
    driver_opts:
      type: none
      device: /mnt/nvme/pgdata
      o: bind
  pgbackup:
    driver: local
    driver_opts:
      type: none
      device: /mnt/backup-nvme/pgbackup
      o: bind
```

This configuration is tuned for a 128 GB / 64-core production node with NVMe storage. Key choices:

- `shared_buffers=48GB`: ~37% of system memory; PostgreSQL recommends 25-40% for dedicated servers.
- `effective_cache_size=96GB`: tells the planner the OS will cache substrate data on top of shared_buffers.
- `synchronous_commit=on`: safer default for production. Throughput-critical deployments may use `off` with replication for durability.
- `wal_compression=zstd`: substantially reduces WAL size with minimal CPU overhead.
- `jit=off`: PostgreSQL JIT does not benefit substrate's C-extension-heavy workload.
- `pg_cron`: required for macro-OODA scheduled jobs.
- `max_connections=200`: enough for typical app+monitoring; high-fanout deployments use connection pooling (PgBouncer).

## Bring-up sequence

```bash
# 1. Provision storage
sudo mkdir -p /mnt/nvme/pgdata /mnt/backup-nvme/pgbackup
sudo chown -R 999:999 /mnt/nvme/pgdata /mnt/backup-nvme/pgbackup    # postgres uid

# 2. Build hartonomous_pg extension
cd ext/hartonomous_pg
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release -DPOSTGRESQL_VERSION=18
cmake --build . --parallel
sudo cmake --install .

# 3. Start PostgreSQL + PostGIS + hartonomous_pg
docker compose up -d
docker compose logs -f postgres    # verify healthy

# 4. Apply substrate schema migrations
for f in schema/migrations/*.up.sql; do
    psql -h localhost -p 5433 -U hartonomous -d hartonomous -f "$f"
done

# 5. Verify foundational gates
psql -h localhost -p 5433 -U hartonomous -d hartonomous -c "
SELECT count(*) FROM ref.entity_type;
SELECT count(*) FROM ref.edge_type;
SELECT count(*) FROM ref.significance_context;
"
# Expected: entity_type 30+, edge_type 200+, significance_context 10

# 6. Begin seed ingestion (per implementation roadmap)
python scripts/ingest_seed.py --phase=ucd
python scripts/ingest_seed.py --phase=iso639
python scripts/ingest_seed.py --phase=wordnet
# ... (per implementation roadmap)

# 7. Configure macro-OODA schedules
psql ... -c "SELECT cron.schedule('macro-ooda-frayed-edge-sweep', '0 2 * * *',
    \$\$SELECT _internal.macro_observe_frayed_edges()\$\$);"
psql ... -c "SELECT cron.schedule('macro-ooda-rating-batch', '*/15 * * * *',
    \$\$SELECT _internal.macro_apply_rating_periods()\$\$);"

# 8. Configure backups
# (See 'Backup and recovery' section)

# 9. Configure monitoring
# (See 30-operations/01-monitoring.md)

# 10. Run smoke tests
psql ... -f tests/smoke/01_round_trip_text.sql
psql ... -f tests/smoke/02_a_star_traversal.sql
psql ... -f tests/smoke/03_glicko_update.sql
```

## High-availability deployment

For deployments requiring HA, the substrate runs as PostgreSQL streaming replication:

- **Primary node** handles writes (ingestion, outcome events, recipe execution).
- **Replica nodes** handle read-heavy inference queries.
- **Synchronous replication** to at least one replica for durability before commit.
- **WAL archiving** to S3-compatible object store for point-in-time recovery.
- **Patroni** for automated failover orchestration.
- **HAProxy or pgpool** for connection routing (writes to primary, reads to nearest healthy replica).

Replica configuration mirrors primary except:

- `max_wal_senders` increased to handle replication slots.
- `hot_standby = on` for read-only queries during replication.
- `recovery_min_apply_delay` may be set for delayed-replica scenarios (e.g., 1-hour delayed replica for accidental-write recovery).

## Multi-region deployment

Per `10-architecture/16-multi-tenancy.md`, tenants with data residency requirements (EU, etc.) need region-specific deployment.

Topology:

- One primary cluster per region (EU, US-East, US-West, AP-South, etc.).
- Public seed state replicated across all regions (via replication topic; substrate operator manages).
- Tenant data in its assigned region only; cross-region queries route through API endpoints, not direct database connections.
- Tenant `data_residency_constraint` enforced at provisioning: a tenant tagged "EU" is created only in the EU cluster.

Cross-region challenges:

- Public seed updates: substrate operator runs ingestion in one region (canonical), replicates to others. Replication can be physical (pg_basebackup + WAL streaming for full-region replicas) or logical (substrate-level replication of public-class entities only).
- Network latency: inference queries that span regions are slow; recipe scheduling avoids this when possible (a tenant in EU running an inference is served from EU; cross-region traversal is rare).

## Container vs bare-metal

The reference deployment uses Docker Compose. Production-scale deployments often run on bare-metal or VM-based deployments for performance. Kubernetes deployments are supported via Helm charts maintained in the operations repository (out of scope for this docs tree).

## Native extension distribution

`hartonomous_pg` is distributed as:

- Source tarball (build from source for custom architectures or hardening).
- Pre-built `.deb` and `.rpm` packages for major Linux distributions (substrate operator-maintained PPA).
- Docker image (`hartonomous-postgres:<version>`) bundling PostgreSQL + PostGIS + hartonomous_pg.

The extension is open-source code (license: AGPL or commercial under operator's licensing terms).

## Configuration management

Configuration is layered:

1. **Container/process defaults.** PostgreSQL config baked into the image.
2. **Cluster overrides.** `postgresql.conf` overrides for the cluster (memory, WAL, parallelism).
3. **Per-database overrides.** Database-level GUCs via `ALTER DATABASE hartonomous SET ...`.
4. **Per-arena overrides.** Arena-specific configurations stored in substrate state (e.g., rating-period settings, batch sizes).
5. **Per-tenant overrides.** Tenant-scoped configurations stored in `tenant.config` JSONB column.
6. **Per-recipe overrides.** Recipe-specific limits and policies in the recipe's JSONB.

Configuration changes flow through standard substrate operator pathways with audit traces.

## Backup and recovery

### Backup strategy

- **WAL archiving:** continuous WAL streaming to S3-compatible object store. Retention: 30 days minimum.
- **Base backups:** weekly full physical backups via `pg_basebackup`. Retention: 4 weeks.
- **Logical backups:** monthly `pg_dump` for catalog and small tables (auxiliary, not primary recovery method).
- **Substrate state snapshots:** quarterly substrate-level snapshots (audit-chain-verified) for compliance retention. These are queryable via `substrate.snapshot_at`.

### Recovery RTO/RPO targets

- **RPO (data loss):** < 5 minutes for production deployments (continuous WAL).
- **RTO (recovery time):** < 30 minutes for primary failover; < 4 hours for full restore from base backup.

### Point-in-time recovery (PITR)

```bash
# Recover to specific timestamp
pg_restore --point-in-time="2026-04-29 14:00:00 UTC" \
    --target-cluster /mnt/nvme/pgdata-recovery \
    --base-backup s3://substrate-backups/base-2026-04-22/ \
    --wal-archive s3://substrate-backups/wal/
```

After recovery, re-verify via:

```sql
SELECT * FROM provenance.verify_integrity_full(NULL, max_depth => 100);
```

This confirms the audit chain is intact at the recovery point.

### Tenant-level recovery

Per `10-architecture/16-multi-tenancy.md`, tenant-level recovery is supported via:

```sql
SELECT * FROM substrate.snapshot_at(timestamp '2026-04-29T00:00:00Z',
                                     tenant_id => 'acme-corp-uuid');
```

This produces a tenant-scoped snapshot that can be used to roll back tenant state without affecting other tenants.

## Performance tuning

Key tuning parameters:

| Parameter | Effect | Tuning guidance |
|---|---|---|
| `shared_buffers` | Substrate hot data cache | 25-40% of system memory |
| `work_mem` | Per-query sort/hash memory | 256MB-1GB; balance with max_connections |
| `maintenance_work_mem` | VACUUM, CREATE INDEX | 4-16 GB |
| `max_parallel_workers_per_gather` | Per-query parallelism | 8-16 for inference queries |
| `effective_io_concurrency` | NVMe parallel I/O | 256+ for NVMe RAID |
| `random_page_cost` | Cost model bias | 1.1 for NVMe; 4.0 for spinning rust |
| `wal_compression` | WAL size reduction | `zstd` (PG18+) |
| `jit` | JIT compilation | `off` (no benefit for substrate workload) |

Beyond PostgreSQL settings, the substrate's hot paths benefit from:

- **NUMA-aware deployment.** Pin PostgreSQL processes to specific NUMA nodes; memory locality matters.
- **Transparent huge pages disabled** for the PostgreSQL data directory (THP can cause latency spikes).
- **CPU governor set to `performance`** rather than `powersave`.
- **Storage: separate WAL and data directories** on different physical devices for IOPS isolation.

## Security

### Authentication

- TLS-only client connections (`hostssl` in pg_hba.conf; reject `host`).
- Certificate-based authentication for substrate operator role.
- SCRAM-SHA-256 password authentication for tenant connections.
- Optional SCRAM channel binding for additional protection.

### Authorization

- Per-role schema permissions (substrate, ref, junc, staging, monitor, cognitive).
- Row-level security policies on all tenant-scoped tables (see `20-technical/00-schema-reference.md`).
- Operator role bypasses RLS but is audited.

### Encryption

- **At rest:** PostgreSQL data directory on encrypted filesystem (LUKS/dm-crypt).
- **In transit:** TLS 1.3 for client and replication.
- **Backup:** WAL archives and base backups encrypted at rest in object store.

### Audit

- All connections logged.
- All operator actions audited via substrate-internal `audit_trace` (see `10-architecture/17-audit-chain.md`).
- All cross-tenant queries (operator-only) flagged in audit logs.

## Cross-references

- Schema reference: `20-technical/00-schema-reference.md`
- Native extension API: `20-technical/01-native-extension-api.md`
- Multi-tenancy: `10-architecture/16-multi-tenancy.md`
- Audit chain: `10-architecture/17-audit-chain.md`
- Continuous learning loop (rating period scheduling): `10-architecture/18-continuous-learning-loop.md`
- Macro-OODA (scheduled job context): `10-architecture/10-godel-engine.md`
- Roadmap: `40-process/04-implementation-roadmap.md`
- Monitoring: `30-operations/01-monitoring.md`
- Backup recovery procedures (detailed): `30-operations/02-backup-recovery.md`

## External references

- PostgreSQL 18 administration: <https://www.postgresql.org/docs/18/admin.html>
- PostGIS: <https://postgis.net/documentation/>
- pg_cron: <https://github.com/citusdata/pg_cron>
- Patroni: <https://patroni.readthedocs.io/>
- WAL-G (backup tool): <https://github.com/wal-g/wal-g>
