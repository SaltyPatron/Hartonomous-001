# Recipe 01: Fresh Setup — Clone to First Inference Query

Intent: from zero to a working substrate that answers queries, on a clean machine, via scripts only (no ad-hoc `psql` / `dotnet` / `cmake` invocations).

---

## Prerequisites

| Tool | Minimum version | Verification command |
|---|---|---|
| PowerShell 7+ | 7.4 | `pwsh --version` |
| Docker Desktop | 24+ | `docker --version` |
| .NET SDK | 9.0 | `dotnet --version` |
| Git | 2.40+ | `git --version` |
| CMake | 3.25+ | `cmake --version` |
| A C/C++ toolchain | GCC 12+ / Clang 16+ / MSVC 17.8+ | `gcc --version` or equivalent |

If any of those are missing, install them first. No workarounds.

---

## Steps

### 1. Clone and enter the repo

```pwsh
git clone https://github.com/<org>/Hartonomous.git
cd Hartonomous
```

### 2. Bootstrap — installs dependencies and verifies environment

```pwsh
pwsh scripts/bootstrap/Install.ps1
```

What this does (inspect the script if uncertain):
- Verifies every tool in the prerequisites table.
- Restores .NET packages.
- Vendors native dependencies (BLAKE3, Eigen, Spectra).
- Emits a green "bootstrap OK" line.

If it fails, the error line states the missing or wrong tool. Fix that, rerun.

### 3. Build the native library and PG extension

```pwsh
pwsh scripts/build/Native.ps1
pwsh scripts/build/PgExtension.ps1
```

Both must emit "build OK". Native DLLs land in the standard output path (discoverable via `native-dll.targets`).

### 4. Build the .NET solution

```pwsh
pwsh scripts/build/Dotnet.ps1
```

Must emit 0 warnings, 0 errors. Warnings are treated as errors per `Directory.Build.props`.

### 5. Bring up PostgreSQL

```pwsh
pwsh scripts/docker/Up.ps1
```

Starts the `hartonomous-pg` container from `docker-compose.yml`. The container includes PostgreSQL + PostGIS + the compiled `hartonomous` extension.

Verify it is up:

```pwsh
pwsh scripts/db/Status.ps1
```

Expected: container running, port 5432 open, version banner printed.

### 6. Create the database

```pwsh
pwsh scripts/db/Create.ps1
```

Creates the `hartonomous` database (name configurable in `scripts/config.psd1`). Installs required extensions (`postgis`, `btree_gist`, `pg_trgm`, `hartonomous`).

### 7. Run migrations

```pwsh
pwsh scripts/db/Migrate.ps1
```

Applies every `sql/migrations/*.up.sql` in order. Records checksums in a migration-tracking table. Must emit a line per migration (`0001 ... OK`, `0002 ... OK`, ...).

### 8. Seed — run every Phase 1 decomposer

```pwsh
pwsh scripts/seed/All.ps1
```

Runs, in dependency order: `Ucd → Iso639 → WordNetOmw → UniversalDeps → Wiktionary → Tatoeba`. Each phase emits progress. Total time on a modern workstation: 15–60 minutes depending on corpus sizes.

Per-phase alternative (run selectively):

```pwsh
pwsh scripts/seed/Ucd.ps1
pwsh scripts/seed/Iso639.ps1
pwsh scripts/seed/WordNetOmw.ps1
pwsh scripts/seed/UniversalDeps.ps1
pwsh scripts/seed/Wiktionary.ps1
pwsh scripts/seed/Tatoeba.ps1
```

### 9. Smoke-test the vertical slice

```pwsh
pwsh scripts/test/Integration.ps1 -Filter VerticalSlice
```

Must pass. If it does not, check the failure's output to identify which step of the slice broke (see recipe `00-vertical-slice.md`).

### 10. Ingest a sample text file

```pwsh
echo "The brown dog ran across the yard." > tmp/example.txt
pwsh scripts/seed/Text.ps1 -Path tmp/example.txt
```

Should produce a short progress line and exit cleanly.

### 11. Issue an inference query

```pwsh
pwsh scripts/ops/Infer.ps1 -Query "brown dog"
```

Output: a structured result with seed entity IDs, top-k paths, and enriched entity metadata. If you want recomposed text:

```pwsh
pwsh scripts/ops/Infer.ps1 -Query "brown dog" -Recompose Text
```

---

## Verification

After step 11, the following must all be true:

| Check | How to verify |
|---|---|
| Substrate has content | `pwsh scripts/ops/Status.ps1` shows nonzero entity counts per partition |
| Migrations are current | `pwsh scripts/db/Migrate.ps1 -Status` reports "up to date" |
| Native extension loaded | `pwsh scripts/db/Status.ps1` lists the `hartonomous` extension |
| Inference returns results | Step 11 output contains at least one path |
| Build artifacts are cached | Re-running `scripts/build/All.ps1` completes in seconds, not minutes |

---

## Anti-patterns

- **DON'T** invoke `psql`, `dotnet`, `cmake`, or `docker` directly in any documented flow. Always go through a `scripts/` entrypoint. If a needed entrypoint is missing, add one via recipe `18-add-cli-command.md` or a new PowerShell script under the correct `scripts/` folder.
- **DON'T** run migrations against a production database without a session open (`scripts/ops/Session.ps1`).
- **DON'T** skip `scripts/seed/All.ps1`. A substrate without seeds cannot answer queries — the inference engine depends on the seeded vocabulary.
- **DON'T** hardcode connection strings anywhere. `scripts/config.psd1` or the `HARTONOMOUS_DB` environment variable is the only source.

---

## Troubleshooting

| Symptom | Most likely cause | Fix |
|---|---|---|
| Bootstrap complains about PowerShell version | Using Windows PowerShell 5.x, not pwsh 7 | Install PowerShell 7 from microsoft.com/powershell |
| Docker step fails with port conflict on 5432 | Another Postgres running | Stop it or change port in `scripts/config.psd1` |
| Native build complains about SIMD flags | Older compiler | Upgrade toolchain; see `specs/native/build-system.md` |
| Migrations fail with "extension hartonomous does not exist" | Step 5 didn't install the extension | Rerun `scripts/db/InstallExtension.ps1` |
| Seed takes hours | Missing corpus files on disk | Check `scripts/config.psd1` source paths; pre-download corpora |
| Inference returns empty | No seed phase has run | Run `scripts/seed/All.ps1` |

---

## Next recipes

- `00-vertical-slice.md` — understand what just happened in detail
- `13-add-migration.md` — add new schema
- `08-add-decomposer.md` — add a new content source
