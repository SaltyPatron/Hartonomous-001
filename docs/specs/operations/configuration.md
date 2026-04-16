# Configuration

**Status**: ✅ Complete

Every tunable parameter, its default, valid range, and location. Single source of truth.

---

## Configuration Source

**File**: `appsettings.json` in the application directory.

**Format**: JSON. Microsoft.Extensions.Configuration binds to strongly-typed `SubstrateConfiguration` record.

**Precedence**: `appsettings.json` → command-line arguments → defaults. No environment variable overrides. No YAML. No TOML. The system reads one file.

**Startup validation**: All configuration is validated at startup before any work begins. Invalid values → `ConfigurationException` → exit code 2. No partial starts.

---

## Configuration Schema

```json
{
  "Database": {
    "ConnectionString": "Host=localhost;Port=5432;Database=hartonomous;Username=postgres;Password=postgres",
    "PoolSize": 10,
    "CommandTimeoutSeconds": 300
  },
  "Ingestion": {
    "DefaultBatchSize": 10000,
    "BulkBatchSize": 100000
  },
  "Sources": {
    "WordNet": "D:\\Models\\princeton-wordnet",
    "Omw": "external\\omw",
    "UniversalDeps": "D:\\Models\\ud-treebanks",
    "Wiktionary": "D:\\Models\\wiktionary",
    "Tatoeba": "D:\\Models\\tatoeba",
    "UcdUca": "D:\\Models\\UCD",
    "Iso639": "D:\\Models\\ISO639",
    "SafeTensors": "D:\\Models\\hub"
  },
  "Significance": {
    "InitialMu": 1500.0,
    "InitialPhi": 350.0,
    "InitialSigma": 0.06,
    "ConvergenceThreshold": 0.01,
    "ComparisonBatchSize": 1000
  },
  "Api": {
    "ListenUrl": "http://localhost:5000",
    "MaxPageSize": 1000,
    "DefaultPageSize": 100
  },
  "Monitoring": {
    "HealthSnapshotRetention": 100,
    "ProgressReportIntervalSeconds": 10
  }
}
```

---

## Parameter Reference

### Database

| Parameter | Type | Default | Valid Range | Description |
|-----------|------|---------|------------|-------------|
| `ConnectionString` | string | `Host=localhost;...` | Valid Npgsql connection string | PostgreSQL connection. |
| `PoolSize` | int | 10 | 1–100 | Npgsql connection pool max size. |
| `CommandTimeoutSeconds` | int | 300 | 10–3600 | SQL command timeout. 5 minutes default handles large batch upserts. |

### Ingestion

| Parameter | Type | Default | Valid Range | Description |
|-----------|------|---------|------------|-------------|
| `DefaultBatchSize` | int | 10,000 | 100–100,000 | Entities per IIngestionBatch (incremental mode). |
| `BulkBatchSize` | int | 100,000 | 10,000–1,000,000 | Entities per batch during bulk (Phase 1) ingestion. |

### Sources

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `WordNet` | string | `D:\Models\princeton-wordnet` | Path to WordNet database files. |
| `Omw` | string | `external\omw` | Path to OMW TSV files. |
| `UniversalDeps` | string | `D:\Models\ud-treebanks` | Path to UD treebank directories. |
| `Wiktionary` | string | `D:\Models\wiktionary` | Path to Wiktextract JSONL files. |
| `Tatoeba` | string | `D:\Models\tatoeba` | Path to Tatoeba CSV + audio files. |
| `UcdUca` | string | `D:\Models\UCD` | Path to Unicode data files. |
| `Iso639` | string | `D:\Models\ISO639` | Path to ISO 639 tables. |
| `SafeTensors` | string | `D:\Models\hub` | Path to Hugging Face Hub cache. |

All paths are validated at startup. Missing directory → `ConfigurationException`.

### Significance (Glicko-2)

| Parameter | Type | Default | Valid Range | Description |
|-----------|------|---------|------------|-------------|
| `InitialMu` | double | 1500.0 | 0–3000 | Starting rating for new entities. |
| `InitialPhi` | double | 350.0 | 1–500 | Starting rating deviation. |
| `InitialSigma` | double | 0.06 | 0.01–0.1 | Starting volatility. |
| `ConvergenceThreshold` | double | 0.01 | 0.001–1.0 | Phi below this = converged. |
| `ComparisonBatchSize` | int | 1000 | 100–10,000 | Comparison events per transaction batch. |

> **Naming Convention**: This configuration uses standard Glicko-2 parameter names (φ = rating deviation, σ = volatility). The database schema uses abbreviated names for column brevity: the `significance` table stores rating deviation in a column called `sigma` and volatility in a column called `volatility`. The stored procedure `initialize_significance` follows the DB convention (`p_initial_sigma` = rating deviation, `p_initial_volatility` = volatility). The C# binding layer maps `InitialPhi` → `p_initial_sigma` and `InitialSigma` → `p_initial_volatility`.

### API

| Parameter | Type | Default | Valid Range | Description |
|-----------|------|---------|------------|-------------|
| `ListenUrl` | string | `http://localhost:5000` | Valid URL | API listen address. |
| `MaxPageSize` | int | 1000 | 10–10,000 | Maximum allowed page size for paginated queries. |
| `DefaultPageSize` | int | 100 | 1–MaxPageSize | Default page size when not specified. |

### Monitoring

| Parameter | Type | Default | Valid Range | Description |
|-----------|------|---------|------------|-------------|
| `HealthSnapshotRetention` | int | 100 | 10–10,000 | Number of health snapshots to retain before cleanup. |
| `ProgressReportIntervalSeconds` | int | 10 | 1–300 | Seconds between `report_progress` calls during ingestion. |

---

## Strongly-Typed Binding

```csharp
public sealed record SubstrateConfiguration
{
    public required DatabaseConfig Database { get; init; }
    public required IngestionConfig Ingestion { get; init; }
    public required SourcesConfig Sources { get; init; }
    public required SignificanceConfig Significance { get; init; }
    public required ApiConfig Api { get; init; }
    public required MonitoringConfig Monitoring { get; init; }
}

public sealed record DatabaseConfig
{
    public required string ConnectionString { get; init; }
    public int PoolSize { get; init; } = 10;
    public int CommandTimeoutSeconds { get; init; } = 300;
}

// (same pattern for all sections)
```

**Registration**:
```csharp
var config = builder.Configuration.Get<SubstrateConfiguration>()
    ?? throw new ConfigurationException("Missing configuration");
builder.Services.AddSingleton(config);
```

---

## Command-Line Overrides

System.CommandLine options override `appsettings.json` values for the CLI:

```
hartonomous run --phase wordnet_omw --batch-size 50000
hartonomous run-all --connection-string "Host=remote;..."
hartonomous migrate --connection-string "Host=remote;..."
```

Only `--batch-size` and `--connection-string` are overridable from the command line. All other parameters require editing `appsettings.json`.

---

## Validation Rules

Applied at startup. All violations → `ConfigurationException` (exit code 2):

1. `ConnectionString` is not null or empty.
2. `PoolSize` is within [1, 100].
3. All `Sources.*` paths exist on disk (`Directory.Exists`).
4. `DefaultBatchSize` ≤ `BulkBatchSize`.
5. `DefaultPageSize` ≤ `MaxPageSize`.
6. Glicko-2 parameters are within documented ranges.
7. `CommandTimeoutSeconds` ≥ 10.

No partial validation. All rules checked. All violations collected. Single exception with all violations listed.
