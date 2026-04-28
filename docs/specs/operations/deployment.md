# Deployment

**Status**: ✅ Complete

How the system is deployed. Single-machine deployment. No cloud. No Kubernetes. One machine, one PostgreSQL instance, one operator.

---

## Prerequisites

| Component | Version | Purpose |
|-----------|---------|---------|
| PostgreSQL | 17+ | Database. The substrate. |
| PostGIS | 3.5+ | Spatial types (POINTZM, LINESTRINGZM, etc.) |
| .NET | 9.0 | C# application runtime |
| C compiler | GCC 13+ / MSVC 2022 / Clang 17+ | Native library and PG extension build |
| CMake | 3.25+ | Native build system |
| pg_config | (from PostgreSQL dev headers) | PG extension build |

### Windows-Specific

- Visual Studio 2022 Build Tools (MSVC).
- PostgreSQL must be installed with development headers (`postgresql-17-dev` equivalent — included in EnterpriseDB installer).
- PostGIS installed via Stack Builder or manual binary.

### Linux-Specific

- `postgresql-server-dev-17`, `libpostgis`, `dotnet-sdk-9.0` from package manager.
- GCC 13+ or Clang 17+ for AVX-512 support.

---

## Deployment Sequence

### Step 1: Build Native Library

```bash
cd ext/native
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

**Output**: `libhartonomous.dll` (Windows) / `libhartonomous.so` (Linux).

Copy to .NET publish directory: `runtimes/win-x64/native/` or `runtimes/linux-x64/native/`.

### Step 2: Build PG Extension

```bash
cd ext/pg
make PG_CONFIG=/usr/bin/pg_config    # Linux
# or
nmake /f Makefile.win PG_CONFIG="C:\Program Files\PostgreSQL\17\bin\pg_config.exe"   # Windows
make install
```

**Output**: `hartonomous.so`/`.dll` + `hartonomous.control` + `hartonomous--1.0.sql` installed to PostgreSQL extension directory.

### Step 3: Database Setup

```sql
-- Connect as superuser
CREATE DATABASE hartonomous;
\c hartonomous

CREATE EXTENSION postgis;
CREATE EXTENSION hartonomous;

CREATE SCHEMA substrate;
CREATE SCHEMA monitor;
```

### Step 4: Run Migrations

```bash
dotnet run --project Hartonomous.Cli -- migrate
```

Executes all migration scripts in order (0001 → latest). Creates domains, types, tables, indexes, functions, procedures, views, seed data.

### Step 5: Verify

```bash
dotnet run --project Hartonomous.Cli -- status
```

Should show: all migrations applied, all tables present, zero entities/edges, no errors.

### Step 6: Publish .NET Applications

**CLI** (self-contained single-file):
```bash
dotnet publish Hartonomous.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/cli
```

**API** (framework-dependent, for Docker or direct run):
```bash
dotnet publish Hartonomous.Api -c Release -o publish/api
```

### Step 7: Initial Ingestion

```bash
./hartonomous run-all
```

Runs all phases in dependency order. Takes hours to days depending on data volumes. Monitor with `./hartonomous status` in a separate terminal.

---

## Docker (Optional)

### PostgreSQL + Extensions

```dockerfile
FROM postgis/postgis:17-3.5

# Install hartonomous extension
COPY ext/pg/hartonomous.so /opt/pg18/lib/
COPY ext/pg/hartonomous.control /opt/pg18/share/extension/
COPY ext/pg/hartonomous--1.0.sql /opt/pg18/share/extension/

# Init script
COPY ext/sql/init.sql /docker-entrypoint-initdb.d/
```

### .NET CLI

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:9.0

COPY publish/cli/ /app/
COPY appsettings.json /app/

WORKDIR /app
ENTRYPOINT ["./hartonomous"]
```

### .NET API

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0

COPY publish/api/ /app/
COPY appsettings.json /app/

WORKDIR /app
EXPOSE 5000
ENTRYPOINT ["dotnet", "Hartonomous.Api.dll"]
```

### docker-compose

```yaml
services:
  db:
    build: ./docker/db
    ports:
      - "5432:5432"
    environment:
      POSTGRES_PASSWORD: postgres
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./data:/data    # source data mount

  api:
    build: ./docker/api
    ports:
      - "5000:5000"
    depends_on:
      - db

volumes:
  pgdata:
```

CLI runs as a one-shot container or directly on the host. Not included in compose (it's a batch job, not a service).

---

## PostgreSQL Tuning

For a machine with 64 GB RAM and NVMe storage (target development workstation):

```ini
# postgresql.conf
shared_buffers = 16GB
effective_cache_size = 48GB
work_mem = 256MB
maintenance_work_mem = 2GB
max_wal_size = 8GB
min_wal_size = 2GB
checkpoint_completion_target = 0.9
random_page_cost = 1.1
effective_io_concurrency = 200
max_connections = 50
max_parallel_workers_per_gather = 4
max_parallel_workers = 8

# Bulk ingestion (Phase 1 — disable after initial load):
synchronous_commit = off
full_page_writes = off
```

**After initial ingestion**: Restore `synchronous_commit = on`, `full_page_writes = on`. Run `VACUUM ANALYZE` on all tables.

---

## Backup Strategy

| Method | When | Command |
|--------|------|---------|
| `pg_dump` | After each major phase completes | `pg_dump -Fc hartonomous > backup_phase_N.dump` |
| WAL archiving | Continuous (optional) | Configure `archive_mode = on` in postgresql.conf |
| Point-in-time recovery | After data loss | Restore base backup + replay WAL to target timestamp |

Backup frequency: operator judgment. At minimum, backup after Phase 2 (all source data ingested) and after Phase 4 (significance converged).

---

## Source Data Acquisition

| Source | Download |
|--------|----------|
| WordNet 3.1 | https://wordnetwebweb.princeton.edu/wordnet/download/current-version/ |
| OMW | https://github.com/omwn/omw-data |
| Universal Dependencies | https://universaldependencies.org/#download |
| Wiktextract | https://kaikki.org/dictionary/ |
| Tatoeba | https://tatoeba.org/en/downloads |
| UCD | https://www.unicode.org/Public/UCD/latest/ |
| ISO 639-3 | https://iso639-3.sil.org/code_tables/download_tables |
| SafeTensors models | Hugging Face Hub (`huggingface-cli download`) |

All source data is stored outside the repository in the paths configured in `appsettings.json`.

---

## Upgrade Procedure

1. **Stop API** (if running).
2. **Backup**: `pg_dump -Fc hartonomous > backup_pre_upgrade.dump`.
3. **Update code**: `git pull` or replace published binaries.
4. **Run migrations**: `./hartonomous migrate`. New migrations apply; existing ones are verified by SHA-256 checksum.
5. **Update PG extension** (if native code changed): `ALTER EXTENSION hartonomous UPDATE TO '1.1'`.
6. **Rebuild native library** (if C/C++ changed): cmake build → copy to runtimes/.
7. **Restart API**.
8. **Verify**: `./hartonomous status`.

No zero-downtime upgrades. The system goes down during migration. This is a research workstation, not a production service.

---

## Rollback Procedure

1. **Stop API**.
2. **Run migration DOWN scripts**: `./hartonomous migrate --target 0021` (rollback to migration 0021).
3. Or **restore from backup**: `pg_restore -d hartonomous backup_pre_upgrade.dump`.
4. **Revert binaries** to previous version.
5. **Restart API**.
