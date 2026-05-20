# scripts/ — Hartonomous ops tooling

Linux-native Bash entrypoints live under `scripts/linux/`. Use these on Linux;
they do not call PowerShell. The `.ps1` scripts are the original Windows
orchestration surface.

Shared logic lives in `lib/*.psm1`. Config defaults live in `config.psd1`
and every value is overridable via environment variable.

---

## Quickstart

### Linux

| Goal | Command |
|---|---|
| Build all local artifacts | `scripts/hart build` |
| Build .NET | `scripts/hart build dotnet --no-restore` |
| Run DB-free .NET unit tests | `scripts/hart test unit --no-build` |
| Build native library | `scripts/hart build native` |
| Run native tests | `scripts/hart test native` |
| Build extension SQL | `scripts/hart build extension-sql` |
| Build/install local PG extension | `scripts/hart build pg-extension` |
| Install already-built PG extension artifacts | `scripts/hart install pg-extension --copy` |
| Dev symlink PG extension artifacts | `scripts/hart install pg-extension --symlink` |
| Regenerate Unicode extension assets | `scripts/hart codegen unicode --ucd-root /path/to/UCD/latest` |
| Create + bootstrap DB | `scripts/hart db create && scripts/hart db bootstrap` |
| Reset DB destructively | `scripts/hart db reset --force` |
| Run DB-backed smoke tests | `scripts/hart test smoke --no-build` |
| Run DB-backed integration tests | `scripts/hart test integration --no-build` |
| Run native + unit + DB-backed gates | `scripts/hart test all` |
| Run a seed phase | `scripts/hart seed UcdUca --no-build` |
| Substrate dashboard | `scripts/hart ops status` |

The implementation lives in `scripts/linux/` and is intentionally Bash-first:
no `pwsh`, no Windows path assumptions, and DB-backed gates are explicit instead
of being mixed into plain unit validation. On Linux, DB commands target local
PostgreSQL through `pg_config`, `psql`, and `HARTONOMOUS_DB`; Docker is kept as
an explicit optional runtime under `scripts/hart docker ...` for Windows parity,
not as the default Linux substrate. Unicode generated C/blob assets are
an explicit offline `codegen` step; normal build, DB bootstrap, and seed consume
the committed/generated extension assets without regenerating them.

### Windows / PowerShell

| Goal | Command |
|---|---|
| First-time setup, cold machine → seeded substrate | `pwsh scripts/bootstrap/Cold.ps1` |
| Dev loop: up + bootstrap + build, no seed, no tests | `pwsh scripts/bootstrap/Dev.ps1` |
| Force full rebuild & reseed | `pwsh scripts/bootstrap/Cold.ps1 -Recreate -Rebuild` |
| Local CI pipeline (same order as GitHub Actions) | `pwsh scripts/ci/Pipeline.ps1` |
| Substrate dashboard | `pwsh scripts/ops/Status.ps1` |

---

## Script matrix

### `bootstrap/`

| Script | Purpose |
|---|---|
| `Cold.ps1`  | Full cold-start orchestrator: preflight → extension SQL → docker → build → test → bootstrap → seed. |
| `Dev.ps1`   | Inner-loop: extension SQL + docker + build + bootstrap (no seed, no tests). |

### `ci/`

| Script | Purpose |
|---|---|
| `Preflight.ps1`    | Verify every prerequisite (pwsh, dotnet SDK, cmake, docker, sources). |
| `Pipeline.ps1`     | End-to-end local CI run mirroring `.github/workflows/ci.yml`. |
| `Install-Tools.ps1`| Best-effort install of missing prereqs via `winget` (Windows). |

### `docker/`

| Script | Purpose |
|---|---|
| `Start-Desktop.ps1` | Ensure Docker Desktop is up (Windows). |
| `Up.ps1`            | `docker compose up -d` + wait-healthy. `-Rebuild` to rebuild image. |
| `Down.ps1`          | `compose down` (preserves volumes). `-RemoveVolumes` to wipe pgdata. |
| `Teardown.ps1`      | Destructive: `down -v --remove-orphans` + optional `-RemoveImage`. |
| `Logs.ps1`          | Stream container logs (`-Follow`, `-Tail N`). |
| `Psql.ps1`          | Interactive psql shell or one-shot `-Sql "..."`. |
| `Exec.ps1`          | `docker exec` arbitrary commands in the PG container. |
| `Status.ps1`        | Container + health + port exposure. |

### `build/`

| Script | Purpose |
|---|---|
| `Dotnet.ps1`      | Build `Hartonomous.slnx`. `-Configuration Debug\|Release`. |
| `Native.ps1`      | CMake configure + build `libhartonomous`. `-Clean`, `-NoTests`. |
| `ExtensionSql.ps1`| Expand `sql/schema/bootstrap.sql` + C-binding template into generated extension SQL. |
| `PgExtension.ps1` | Build+install `hartonomous_pg` inside the running container (PGXS). |
| `All.ps1`         | All three in the CI-canonical order. |
| `Clean.ps1`       | Remove `bin/`, `obj/`, `ext/*/build/`. `-Managed` for `dotnet clean`. |

### `test/`

| Script | Purpose |
|---|---|
| `Dotnet.ps1`      | `dotnet test` + TRX + coverage into `reports/`. Supports `-Filter`, `-Project`. |
| `Native.ps1`      | `ctest --output-on-failure`. `-Rebuild` to re-configure+build first. |
| `Pg.ps1`          | `pg_regress` inside container (`make installcheck`). |
| `Integration.ps1` | `Hartonomous.Integration.Tests` (requires running container). |
| `All.ps1`         | All four with selective skip flags. |

### `db/`

| Script | Purpose |
|---|---|
| `Create.ps1`          | `CREATE DATABASE` + ensure PostGIS (idempotent). |
| `Drop.ps1`            | `DROP DATABASE` with backend termination + `-Force`. |
| `Reset.ps1`           | Drop + Create + Bootstrap. |
| `Bootstrap.ps1`       | Install the generated `hartonomous` PostgreSQL extension with `CREATE EXTENSION`. |
| `Migrate.ps1`         | Deprecated redirect to `Bootstrap.ps1`; retained for old command muscle memory only. |
| `InstallExtension.ps1`| `CREATE EXTENSION hartonomous`. `-Drop` to reinstall. |
| `Backup.ps1`          | `pg_dump` → `artifacts/backups/<ts>.dump` (custom/plain/tar). |
| `Restore.ps1`         | `pg_restore`/`psql -f` + optional drop/recreate. |

### `seed/`

| Script | Purpose |
|---|---|
| `Ucd.ps1`        | Phase: UcdUca (Unicode + UCA collation). |
| `Iso639.ps1`     | Phase: Iso639. |
| `WordNetOmw.ps1` | Phase: WordNetOmw (Princeton WN + OMW). |
| `Safetensors.ps1`| Phase: ModelDecomp (safetensors ingestion). |
| `All.ps1`        | Every phase in FK order. `-WithModel` to include Safetensors. |
| `Validate.ps1`   | Print substrate row-count dashboard. |

### `verify/`

| Script | Purpose |
|---|---|
| `AgentScaffolding.ps1` | Scan AI-facing scaffolding and standards docs for migration-era schema drift (`pwsh -File scripts/verify/AgentScaffolding.ps1`). |
| `NoInlineSql.ps1` | Scan C# and normal ops scripts for inline SQL command bodies (`pwsh -File scripts/verify/NoInlineSql.ps1`). |
| `compare_safetensors.py` | Compare exported safetensors files. |

### Data acquisition helpers

| Tool | Purpose |
|---|---|
| `download_seeds.py` | Download or backfill external seed datasets from `scripts/seeds.yaml`. Example: `python scripts/download_seeds.py --data-root /data/Unicode --dataset ucd-uca-full-mirror`. |

### `ops/`

| Script | Purpose |
|---|---|
| `Status.ps1`  | Full substrate dashboard (daemon, container, DB, extension bootstrap, phase status, counts). |
| `Readiness.ps1` | Exact live readiness report: data counts, phase rows, significance coverage, geometry gaps, and query/function probes. Exits non-zero on any warning or failure. |
| `Phases.ps1`  | Wrap `phases list\|status\|run`. |
| `Session.ps1` | Wrap `session open\|close\|status`. |

### `lib/` (modules — not called directly)

| Module | Purpose |
|---|---|
| `Hartonomous.Common.psm1`   | Logging, step banners, config loader, assertions, polling. |
| `Hartonomous.Docker.psm1`   | Daemon + compose + container helpers. |
| `Hartonomous.Postgres.psm1` | psql wrappers + health checks + row-count queries. |
| `Hartonomous.Build.psm1`    | dotnet/cmake discovery + invocation. |
| `Hartonomous.Phases.psm1`   | CLI wrappers for legacy migrate, phases, and session commands. |

---

## Configuration

`scripts/config.psd1` holds all defaults. Env-var overlay:

| Env var | Overrides |
|---|---|
| `HARTONOMOUS_DB`                    | `Postgres.ConnectionString` (full override) |
| `HARTONOMOUS_POSTGRES__HOST`        | `Postgres.Host` |
| `HARTONOMOUS_POSTGRES__PORT`        | `Postgres.Port` |
| `HARTONOMOUS_POSTGRES__USER`        | `Postgres.User` |
| `HARTONOMOUS_POSTGRES__PASSWORD`    | `Postgres.Password` |
| `HARTONOMOUS_POSTGRES__DATABASE`    | `Postgres.Database` |
| `HARTONOMOUS_PATHS__SOURCEROOT`     | `Paths.SourceRoot` |
| `HARTONOMOUS_DOTNET__CONFIGURATION` | `Dotnet.Configuration` |
| `HARTONOMOUS_NATIVE__CONFIGURATION` | `Native.Configuration` |

Env vars win over `config.psd1`; command-line parameters win over env vars.

---

## Exit codes (sysexits.h)

| Code | Name          | Meaning |
|------|---------------|---------|
| 0    | Ok            | Success |
| 64   | Usage         | Bad CLI args |
| 65   | DataError     | Source data missing/invalid |
| 66   | NoInput       | Required input file absent |
| 69   | Unavailable   | Docker daemon / container / service down |
| 70   | Software      | Internal logic error |
| 73   | CantCreate    | Can't write output |
| 78   | Config        | Preflight / config problem |

---

## Logging

Every script writes to `logs/hartonomous-YYYYMMDD-<script>-<pid>.log` in the
repo root. Console output is colorized by level. The `logs/` dir is
gitignored.

---

## CI integration

`.github/workflows/ci.yml` can call any of these scripts directly via
`shell: pwsh`. The intent is that what runs locally is exactly what runs
in CI — `scripts/ci/Pipeline.ps1` is the canonical sequence.
