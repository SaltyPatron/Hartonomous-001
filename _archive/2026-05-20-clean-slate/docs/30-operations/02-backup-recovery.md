# Backup and Recovery

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Operations engineers managing backup procedures, anyone planning disaster recovery, anyone responsible for compliance retention.

---

## Backup tiers

The substrate maintains four overlapping backup tiers, each addressing different failure modes and retention requirements:

| Tier | Method | Frequency | Retention | RTO | RPO | Used for |
|---|---|---|---|---|---|---|
| Continuous WAL | WAL archiving to object store | Continuous | 30 days | < 30 min | < 5 min | Recent corruption, accidental ops |
| Base physical | `pg_basebackup` to object store | Weekly | 4 weeks | < 4 hours | < 1 week | Cluster restore |
| Logical | `pg_dump` of catalog and small tables | Monthly | 1 year | hours | < 1 month | Disaster recovery, format migration |
| Substrate snapshot | `substrate.snapshot_at` audit-chain-verified | Quarterly | 7 years (or per regulation) | hours | < 3 months | Compliance retention |

## Tier 1 — Continuous WAL archiving

WAL is streamed continuously to S3-compatible object storage via WAL-G or pgBackRest:

```bash
# WAL-G configuration (excerpt)
WALG_S3_PREFIX="s3://substrate-backups/wal/"
WALG_COMPRESSION_METHOD="zstd"
WALG_DELTA_MAX_STEPS=6

# Postgres archive_command
archive_command = 'wal-g wal-push %p'
archive_mode = on
archive_timeout = 60          # force archive every 60s minimum (caps RPO)
```

WAL recovery enables:

- **Point-in-time recovery (PITR)** to any second within the retention window.
- **Accidental-write recovery** via delayed replica or PITR.
- **Replication catchup** after primary failover.

WAL retention of 30 days covers most operational scenarios. For longer retention, base backups + WAL chains can be assembled.

## Tier 2 — Weekly base backups

Full physical backups via `pg_basebackup` (or WAL-G's `backup-push`):

```bash
# WAL-G base backup
wal-g backup-push /var/lib/postgresql/data
```

Each base backup is a full physical snapshot of the cluster's data directory, compressed and uploaded to object storage. Combined with WAL archives, any past timestamp within retention is restorable.

Backup metadata recorded in operator catalog:

- Timestamp of backup.
- Cluster size at backup time.
- Backup byte size.
- WAL position at start and end.
- SHA-256 of the backup archive.

## Tier 3 — Monthly logical backups

`pg_dump` of substrate catalog tables (`ref.*`, configuration tables, small reference data) for format-migration scenarios:

```bash
pg_dump -h localhost -p 5433 -U hartonomous \
        --schema=ref \
        --schema=monitor \
        --format=directory \
        --file=/mnt/backup-nvme/pgbackup/logical-2026-04-01/ \
        --jobs=8 \
        hartonomous
```

Logical backups are NOT used for primary recovery (too slow at substrate scale, and may lose constraints). They serve as:

- Format-migration source if upgrading PostgreSQL major version requires re-creation.
- Cross-platform restore (e.g., x86 to ARM).
- Catalog inspection without running the full cluster.

## Tier 4 — Substrate snapshots

Substrate-level snapshots use `substrate.snapshot_at` (see `10-architecture/17-audit-chain.md`):

```sql
INSERT INTO operator.snapshot_archive (snapshot_at, scope, snapshot_data)
SELECT
    timestamp '2026-04-30T00:00:00Z',
    'full',
    pg_export_snapshot();
```

Substrate snapshots are exported as content-addressed artifacts — they include the audit-chain commitments at the snapshot point. This is what enables compliance retention: a snapshot from 2026-Q1 can be presented in 2033 with cryptographic proof that it was the substrate's state at that time and has not been modified since.

Snapshot exports include:

- Logical dump of all tables at the snapshot point.
- Audit-chain verification proof (signed by operator's compliance key).
- Substrate version metadata.
- Hash manifest of constituent blobs.

Storage: Snapshots are stored in WORM (write-once, read-many) compliance storage with object-lock enabled to prevent tampering.

## Recovery procedures

### Recovery scenario 1 — accidental write to production

If a tenant accidentally pushed bad data, or operator action corrupted state:

1. **Identify the bad timestamp** — when the corruption occurred.
2. **PITR to just before the bad timestamp** in a new cluster:

```bash
wal-g backup-fetch /mnt/recovery-target /latest
```

3. **Configure recovery target:**

```ini
# recovery.conf
restore_command = 'wal-g wal-fetch %f %p'
recovery_target_time = '2026-04-30 13:55:00 UTC'
recovery_target_action = 'promote'
```

4. **Start recovery cluster** and verify state.
5. **Compare with production** to identify the diff to restore.
6. **Apply restore via SQL or substrate snapshot replay** to production (operator-controlled, audited).

### Recovery scenario 2 — primary node failure

Patroni-orchestrated automatic failover:

1. Patroni detects primary unresponsive (heartbeat timeout 30s).
2. Streaming replica with lowest replication lag is promoted to primary.
3. Connection routers (HAProxy) switch traffic to new primary.
4. Recovery target: < 30 min total, < 5 min auto-failover.

Manual failover:

```bash
patronictl failover --candidate replica-eastus-2
```

After failover, the original primary (when recovered) rejoins as a replica via `pg_basebackup` or WAL replay catchup.

### Recovery scenario 3 — full cluster loss

Total disaster (region failure, malicious deletion, etc.):

1. **Provision new cluster infrastructure** (compute, storage, network).
2. **Restore latest base backup:**

```bash
wal-g backup-fetch /mnt/nvme/pgdata LATEST
```

3. **Apply WAL chain to current time (or last clean WAL position):**

```bash
# recovery configured to apply WAL up to current
restore_command = 'wal-g wal-fetch %f %p'
recovery_target_action = 'promote'
```

4. **Verify substrate integrity:**

```sql
SELECT * FROM provenance.verify_integrity_full(NULL, max_depth => 1000);
```

5. **Reconnect tenants and applications.**
6. **Audit the recovery** (`audit_trace` entry).

RTO target: < 4 hours from total loss to operational.

### Recovery scenario 4 — tenant-level rollback

Tenant requests their state be rolled back to a specific timestamp (rare; e.g., bad ingestion):

```sql
-- Inspect what would change
SELECT * FROM substrate.snapshot_diff(
    tenant_id => 'acme-corp-uuid',
    from_time => timestamp '2026-04-29T00:00:00Z',
    to_time => now()
);

-- Apply rollback (operator-only, audited)
SELECT operator.tenant_rollback(
    tenant_id => 'acme-corp-uuid',
    target_time => timestamp '2026-04-29T00:00:00Z',
    rationale => 'Customer request: bad ingestion 2026-04-29 14:30 UTC'
);
```

Rollback removes tenant-scoped provenance entries created after the target time. Atoms with surviving provenance from other tenants/seeds remain (the tenant's contribution is retracted, not the content itself). Audit trace records the rollback.

### Recovery scenario 5 — compliance audit

A regulator or customer requests proof of substrate state at a past timestamp:

```sql
-- Replay snapshot
SELECT * FROM substrate.snapshot_at(timestamp '2025-Q4');

-- Verify audit chain at that timestamp
SELECT * FROM provenance.verify_integrity_at(
    timestamp '2025-Q4',
    sample_size => 10000
);

-- Export with cryptographic attestation
SELECT operator.export_compliance_snapshot(
    timestamp => '2025-Q4',
    format => 'compliance_snapshot_v1',
    signing_key_id => 'operator-compliance-2025'
);
```

The compliance export includes substrate state, audit-chain proof, and operator's cryptographic signature. The recipient can independently verify the export's integrity using the operator's public key.

## Backup verification

Backups are useless if they don't restore. The substrate operator runs:

- **Daily restore drill** — restore latest base backup + WAL to a separate environment; run substrate health checks; tear down.
- **Monthly DR drill** — full cross-region restore from cold backups; verify against production state checksum.
- **Quarterly compliance verification** — restore a substrate snapshot and verify audit-chain integrity.

Drill failures are CRITICAL alerts — backups are not safe until verified to restore.

## Encryption

All backup tiers are encrypted:

- **WAL archives:** WAL-G's built-in encryption (libsodium-based).
- **Base backups:** WAL-G encryption.
- **Logical backups:** Symmetric AES-256 encryption pre-upload.
- **Substrate snapshots:** Asymmetric (operator's public key) for compliance archives.

Keys are managed via the operator's HSM or KMS:

- WAL/backup encryption keys: rotated quarterly; old keys retained for backup retention period.
- Compliance signing keys: managed under stricter rotation policy; old keys retained indefinitely (for verifying past compliance attestations).

## Cross-region replication

For multi-region deployments (`30-operations/00-deployment.md`):

- WAL streamed to PRIMARY region's archive.
- Cross-region WAL replication via object-store replication (S3 cross-region replication, or equivalent).
- Each region maintains its own base backups (regional autonomy).
- Compliance snapshots are archived in the regulatory-relevant region.

If primary region fails, the surviving region's WAL archive is the recovery source. Failover is operator-coordinated; automatic cross-region failover is too risky (network partitions vs true region loss).

## Audit chain interaction

Backup operations themselves are audited:

```sql
SELECT * FROM substrate.audit_trace
WHERE operation_type IN ('backup_run', 'restore_run', 'snapshot_export')
ORDER BY started_at DESC LIMIT 50;
```

Restores, in particular, are critical events that emit audit entries with full operator attribution.

## Failure mode catalog

| Failure | Detection | Response |
|---|---|---|
| WAL archive upload failure | `archive_command` exit non-zero; pg_stat_archiver | Alert immediately; substrate not durable until resolved |
| Base backup failure | Backup runner exit non-zero | Alert; investigate; retry |
| Restore drill fails | Drill workflow exit non-zero | CRITICAL; backups not safe |
| WAL retention exceeded before restore needed | Time-based check | Cannot restore that far back; investigate root cause |
| Backup encryption key lost | Key inventory check | CATASTROPHIC; backups unrecoverable; recovery requires last good unencrypted state |
| Cross-region replication lag | Object-store metrics | Alert if lag > 1h sustained; recovery to recent point may not be possible from secondary region |

## Cross-references

- Audit chain (snapshot_at, verify_integrity): `10-architecture/17-audit-chain.md`
- Multi-tenancy (tenant offboarding, rollback): `10-architecture/16-multi-tenancy.md`
- Deployment: `30-operations/00-deployment.md`
- Monitoring (backup health metrics): `30-operations/01-monitoring.md`
- Substrate Law 13 (fail loud applies to backup failures): `10-architecture/01-substrate-laws.md`

## External references

- WAL-G: <https://github.com/wal-g/wal-g>
- pgBackRest: <https://pgbackrest.org/>
- Patroni: <https://patroni.readthedocs.io/>
- PostgreSQL backup and restore: <https://www.postgresql.org/docs/18/backup.html>
- S3 object lock for WORM: <https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html>
