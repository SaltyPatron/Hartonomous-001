# Testing Strategy

**Status**: ✅ Complete

How every layer is tested. xUnit + FluentAssertions for C#. `pg_regress` for SQL. Google Test for C/C++.

---

## Test Pyramid

```
         ┌───────────┐
         │   E2E     │  ← 3–5 tests (full pipeline on small data)
        ─┼───────────┼─
        │ Integration │  ← ~50 tests (C# ↔ PostgreSQL, PG extension)
       ─┼─────────────┼─
       │   Unit Tests   │  ← ~500 tests (C#, C/C++, SQL functions)
      ─┴─────────────────┴─
```

---

## Unit Tests (C#)

**Framework**: xUnit 2.x + FluentAssertions.

**Project**: `Hartonomous.Tests/Unit/`.

### Categories

| Category | Count (est.) | Tests |
|----------|-------------|-------|
| Hash computation | ~20 | Known BLAKE3 inputs → known outputs. Verify ComputeHash, ComputeMerkleHash, ComputeEdgeHash match reference vectors. |
| Parsing | ~80 | Per-decomposer parser tests. Known source file fragments → expected parsed entries. One test class per parser (WordNetDbParser, ConllUParser, WiktextractParser, etc.). |
| Interface contracts | ~30 | Verify every IDecomposer implementation returns valid DecomposerCode, correctly reports GetSourcePaths, etc. |
| Base class behavior | ~20 | BaseDecomposer.ValidateSourceAsync with missing files. BaseRecomposer.FlattenToAtomsAsync with known tree. BaseAnalysisPass.QueryEntitiesInBatchesAsync paging logic. |
| Glicko-2 math | ~15 | Known rating inputs → expected mu/phi/sigma outputs. Reference test vectors from Glickman's paper. |
| Configuration validation | ~15 | Valid configs pass. Invalid configs (bad ranges, missing paths) → ConfigurationException. |
| Error hierarchy | ~10 | Exception creation with ErrorContext. Verify all exception types serialize correctly. |
| Recomposer assembly | ~20 | TextRecomposer concatenation logic. ImageRecomposer pixel placement. Tested with mock graph data (no database). |

### Test Data

Small known datasets checked into `Hartonomous.Tests/TestData/`:

```
TestData/
  wordnet/          ← 3 synsets, 5 lemmas, known pointer relationships
  conllu/           ← 2 sentences, known dependency arcs
  wiktextract/      ← 5 entries, known senses and translations
  unicode/          ← 10 codepoints, known properties
  safetensors/      ← 1 tiny model (2 tensors, 4×4 matrices)
  iso639/           ← 5 language codes
  tatoeba/          ← 3 sentences, 2 translation links
```

All test data is minimal (smallest possible to exercise the logic) and hand-crafted (not sliced from real data — avoids copyright issues).

---

## Unit Tests (C/C++)

**Framework**: Google Test.

**Location**: `ext/native/tests/`.

### Categories

| Category | Count (est.) | Tests |
|----------|-------------|-------|
| BLAKE3 | ~10 | Official BLAKE3 test vectors from the specification. All hash lengths. |
| S3 geometry | ~15 | Known point pairs → known geodesic distances. Known point sets → known centroids. |
| Super-Fibonacci | ~10 | Known n values → known (θ, φ) outputs. Verify uniform distribution properties. |
| Hilbert curve | ~10 | Known (x, y, z) → known Hilbert index. Round-trip: encode → decode → original. |
| SIMD correctness | ~20 | Every SIMD-optimized function compared against scalar reference implementation. AVX-512, AVX2, SSE4.2, and scalar paths all produce identical results for identical inputs. |

### Test Vectors

BLAKE3 test vectors from https://github.com/BLAKE3-team/BLAKE3/blob/master/test_vectors/test_vectors.json. Committed to `ext/native/tests/vectors/`.

---

## Integration Tests (C# + PostgreSQL)

**Framework**: xUnit + FluentAssertions + real PostgreSQL.

**Project**: `Hartonomous.Tests/Integration/`.

**Database**: Disposable PostgreSQL database created per test class. Each test class creates a fresh database, runs migrations, and tears down after all tests complete.

```csharp
public class IngestionPipelineTests : IAsyncLifetime
{
    private NpgsqlDataSource _db;
    private string _dbName;

    public async Task InitializeAsync()
    {
        _dbName = $"test_{Guid.NewGuid():N}";
        // CREATE DATABASE, run migrations
    }

    public async Task DisposeAsync()
    {
        // DROP DATABASE
    }
}
```

No Docker. No Testcontainers. Tests assume PostgreSQL is running on localhost:5432 (same as development). If it's not available, integration tests are skipped (`[Fact(Skip = "...")]` via test filter trait).

### Categories

| Category | Count (est.) | Tests |
|----------|-------------|-------|
| Ingestion pipeline | ~15 | Submit batch → verify entity/edge/junction rows. Duplicate entity → verify dedup. Batch rollback on failure. |
| Entity upsert | ~5 | Insert entity → read back. Upsert same hash → same entity_id. |
| Edge creation | ~5 | Create edge with members → verify edge + edge_member rows. Edge hash dedup. |
| Junction population | ~8 | Entity + classification → junction row. Verify all 8 junction tables. |
| Significance | ~5 | Record comparison → verify updated mu/phi/sigma. Initialize significance → verify default values. |
| Traversal | ~5 | Build small graph (10 entities, 15 edges) → traverse → verify paths and costs. |
| Stored procedures | ~14 | One test per SP. Known inputs → expected side effects in database. |
| Functions | ~12 | One test per function. Known inputs → expected outputs. |
| Views | ~6 | Populate data → query view → verify result set. |
| Migration | ~3 | Run all UP → verify schema. Run all DOWN → verify empty. Run UP again → idempotent. |

---

## Integration Tests (PG Extension)

**Framework**: `pg_regress` (PostgreSQL's built-in regression test tool).

**Location**: `ext/pg/test/sql/` (input) + `ext/pg/test/expected/` (expected output).

### Tests

| Test | Description |
|------|-------------|
| `extension_load.sql` | `CREATE EXTENSION hartonomous` succeeds. All functions exist. |
| `blake3_hash.sql` | `SELECT hartonomous.blake3_hash(...)` matches reference outputs. |
| `s3_distance.sql` | S3 geodesic distance between known points. |
| `s3_centroid.sql` | Centroid of known point set. |
| `neighbors.sql` | Small graph → `SELECT * FROM hartonomous.neighbors(...)` → expected result set. |

---

## End-to-End Tests

**Project**: `Hartonomous.Tests/E2E/`.

**Count**: 3–5 tests. Each test runs a complete pipeline on minimal test data.

### Test 1: Minimal WordNet Ingestion

1. Create database + migrate.
2. Run UCD/UCA decomposer on 10 codepoints.
3. Run ISO 639 decomposer on 5 languages.
4. Run WordNet decomposer on 3 synsets + 5 lemmas.
5. Verify: entity counts match, edge types correct, junction tables populated, monitor shows `completed`.

### Test 2: Text Round-Trip

1. Ingest a small UD treebank (2 sentences).
2. Run text analysis passes.
3. Recompose sentence entity → compare output text with source (semantic equivalence, not byte-identical).

### Test 3: Full Phase Runner

1. Run `hartonomous run-all` on the complete test dataset (all decomposers, minimal data).
2. Verify all phases complete.
3. Verify monitor.phase_status shows all `completed`.
4. Verify no errors in monitor.error_log.

### Test 4: Significance Convergence

1. Ingest small dataset.
2. Run significance initialization.
3. Run 3 comparison rounds.
4. Verify sigma decreasing (convergence).
5. Verify session records exist.

---

## Per-Phase Validation Criteria

Applied after each phase completes (both in E2E tests and by the `hartonomous validate` CLI command):

| Phase | Validation |
|-------|-----------|
| CoreAlgebra (1) | Schema exists. All migrations applied. Seed data present. |
| UcdUca (2a) | Entity count ≈ 150K (±1%). All codepoints have entity_type = `codepoint`. No orphan physicalities. |
| Iso639 (2b) | Entity count matches ISO 639-3 table row count (7,910 ± 10). Language reference table populated. |
| WordNetOmw (2c) | Synset count ≈ 117K. Edge types `hypernym`, `hyponym` exist with expected cardinalities. |
| UniversalDeps (2d) | Token count ≈ treebank sentence count × avg tokens. All dependency edges have valid deprel codes. |
| Wiktionary (2e) | Sense count > 0. No orphan edges (all edge members reference existing entities). |
| Tatoeba (2f) | Sentence count matches `sentences.csv` line count. Translation edge count matches `links.csv`. |
| ModelDecomp (3) | Tensor count matches safetensors header tensor count. Model architecture entity exists. |
| SignificanceField (4) | All entity pairs in scope have significance records. Initial mu = configured value. |
| InferenceEngine (5) | Comparison events recorded. Sigma decreasing per session. |
| Validation (6) | No orphan entities. No dangling edges. No null hashes. No duplicate hashes within same entity_type. |

---

## CI Pipeline

```
Build (.NET + C/C++) → Unit Tests → Integration Tests → E2E Tests
```

No separate CI server specified. The developer runs `dotnet test` locally. If a CI system is later added, it runs the same commands:

```bash
dotnet build Hartonomous.sln
dotnet test Hartonomous.Tests --filter "Category=Unit"
dotnet test Hartonomous.Tests --filter "Category=Integration"
dotnet test Hartonomous.Tests --filter "Category=E2E"
```

Integration and E2E tests require a running PostgreSQL instance. CI must provide one.

---

## Test Coverage

No coverage target. Coverage metrics are not tracked. The validation criteria above define "correct" — not a percentage.
