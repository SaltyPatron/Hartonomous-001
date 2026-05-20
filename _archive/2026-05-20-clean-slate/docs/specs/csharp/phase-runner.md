# Phase Runner

**Status**: ✅ Complete

The CLI orchestrator that runs phases in dependency order. Entry point for all substrate operations.

---

## CLI Interface

```
dotnet run --project src/Hartonomous.Cli -- <command> [options]
```

### Commands

| Command | Description | Example |
|---------|-------------|---------|
| `run` | Execute one or more phases | `run --phase UcdUca` |
| `run-all` | Execute all phases in dependency order | `run-all` |
| `migrate` | Run database migrations | `migrate up`, `migrate down 1` |
| `status` | Show phase status and substrate health | `status` |
| `validate` | Validate sources and schema without running | `validate --phase WordNetOmw` |

Additional session-management commands (`session create`, `session close`, `session list`, `session diff`, `session archive`) are documented in [sessions.md](../operations/sessions.md).

### Run Command Options

```
run --phase <Phase>         Run a specific phase
    --decomposer <code>     Run only one decomposer within a phase (optional)
    --batch-size <N>        Override default batch size
    --dry-run               Parse sources and validate without writing to DB
    --force                 Re-run a phase that is already marked complete
```

### Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success — all requested phases completed |
| 1 | Failure — a phase failed (error details on stderr) |
| 2 | Partial — some phases completed, one failed (error details on stderr) |
| 3 | Configuration error — missing config, bad arguments |

---

## Phase Dependency DAG

```
CoreAlgebra
  → UcdUca
    → Iso639
      → WordNetOmw
        → UniversalDeps
          → ModelDecomp
            → Wiktionary
              → Tatoeba
  → SignificanceField
    → InferenceEngine

Tatoeba → InferenceEngine
ModelDecomp → InferenceEngine

InferenceEngine → Validation
```

Phases with multiple dependencies require ALL dependencies satisfied. `InferenceEngine` requires `Tatoeba`, `ModelDecomp`, AND `SignificanceField`.

---

## Phase Definitions

| Phase | Enum Value | Decomposers/Actions | Dependencies |
|-------|-----------|---------------------|-------------|
| `CoreAlgebra` | Phase 1 | Run migrations 0001-0020 (schema + seed data). Create deferred indexes. | None |
| `UcdUca` | Phase 2a | `UcdUcaDecomposer` — codepoint entities, S3 physicality, property junctions | CoreAlgebra |
| `Iso639` | Phase 2b | `Iso639Decomposer` — language reference table population | UcdUca |
| `WordNetOmw` | Phase 2c | `WordNetDecomposer` then `OmwDecomposer` — synsets, lemmas, senses, cross-lingual edges | Iso639 |
| `UniversalDeps` | Phase 2d | `UdDecomposer` — sentences, tokens, dependency edges | WordNetOmw |
| `ModelDecomp` | Phase 3 | `SafetensorsDecomposer` — tensor entities, model architecture edges | UniversalDeps |
| `Wiktionary` | Phase 2e | `WiktionaryDecomposer` — definitions, translations, etymology | ModelDecomp |
| `Tatoeba` | Phase 2f | `TatoebaDecomposer` — sentences, translation pairs, audio | Wiktionary |
| `SignificanceField` | Phase 4 | Initialize Glicko-2 ratings from trust priors. Run initial arena comparisons. | CoreAlgebra |
| `InferenceEngine` | Phase 5 | Start accepting queries. Enable API layer. Run inference validation suite. | Tatoeba, ModelDecomp, SignificanceField |
| `Validation` | Phase 6 | End-to-end validation: round-trip tests, significance distribution checks. | InferenceEngine |

### Within-Phase Execution

**Sequential.** Within a phase, decomposers run one at a time in the order listed above. Rationale: later decomposers in the same phase may depend on entities created by earlier ones (e.g., `OmwDecomposer` references WordNet synsets created by `WordNetDecomposer`).

**No parallelism within a phase.** The computational cost is dominated by I/O (parsing source files, database writes). PostgreSQL's write throughput is the bottleneck, not CPU. Running two decomposers in parallel doubles contention on the same connection pool and WAL without meaningful speedup.

---

## Execution Model

```csharp
public sealed class SequentialPhaseRunner : IPhaseRunner
{
    private readonly IServiceProvider _services;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SequentialPhaseRunner> _logger;

    public async Task<PhaseResult> RunPhaseAsync(Phase phase, CancellationToken ct)
    {
        _logger.LogInformation("Starting phase: {Phase}", phase);
        var sw = Stopwatch.StartNew();

        // 1. Check dependencies
        var status = await GetStatusAsync(ct);
        foreach (var dep in GetDependencies(phase))
        {
            if (status[dep] != PhaseStatus.Completed)
                throw new DependencyNotMetException(phase, dep);
        }

        // 2. Check if already complete
        if (status[phase] == PhaseStatus.Completed)
            throw new PhaseAlreadyCompleteException(phase);
            // (unless --force)

        // 3. Mark in-progress
        await SetPhaseStatusAsync(phase, PhaseStatus.InProgress, ct);

        // 4. Get decomposers for this phase
        var decomposers = ResolveDecomposers(phase);

        // 5. Run each decomposer sequentially
        foreach (var decomposer in decomposers)
        {
            await using var pipeline = CreatePipeline();
            var reporter = CreateReporter();

            await decomposer.ValidateSourceAsync(ct);
            await decomposer.DecomposeAsync(pipeline, reporter, ct);
            await decomposer.DisposeAsync();
        }

        // 6. Mark complete
        await SetPhaseStatusAsync(phase, PhaseStatus.Completed, ct);

        sw.Stop();
        _logger.LogInformation("Completed phase: {Phase} in {Elapsed}", phase, sw.Elapsed);
        return new PhaseResult(phase, PhaseStatus.Completed, sw.Elapsed, null);
    }
}
```

**No try/catch in RunPhaseAsync.** If a decomposer throws, the exception propagates to `RunAllAsync` which catches at the top level, marks the phase as `Failed`, and halts.

---

## Checkpoint & Resume

### How "Complete" Is Determined

Phase completion is tracked in a `monitor.phase_status` table:

```sql
CREATE TABLE monitor.phase_status (
    phase       VARCHAR PRIMARY KEY,
    status      VARCHAR NOT NULL,  -- 'not_started', 'in_progress', 'completed', 'failed'
    started_at  TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    error       TEXT
);
```

### Resume After Failure

If a phase fails partway through:

1. The `phase_status` row shows `failed` with the error message.
2. The operator fixes the source data or code.
3. The operator runs `run --phase <Phase> --force`.
4. The phase runner re-runs the entire phase from the beginning.

**No mid-phase checkpointing.** The granularity is the phase, not the batch. Decomposers are idempotent — re-running on the same data produces the same result (entity upsert on hash + ON CONFLICT DO NOTHING). Re-running a failed phase re-processes entities that were already committed (skipped as duplicates) and picks up where the failure occurred.

**Why not checkpoint per batch**: Tracking batch-level checkpoints adds complexity for minimal benefit. Decomposition of any single source file takes minutes, not hours. Re-processing from the beginning of a phase costs minutes of redundant dedup lookups, not re-computation.

---

## Progress Reporting

Real-time progress is shown via structured log lines:

```
[10:23:45 INF] WordNetDecomposer: 45,000 / 117,659 entities | 120,000 edges | 3,200 dupes | 12.5 KB/s | data.noun
[10:23:50 INF] WordNetDecomposer: 50,000 / 117,659 entities | 135,000 edges | 3,500 dupes | 12.8 KB/s | data.noun
```

No progress bar library. No terminal UI. Simple structured log lines that can be parsed, filtered, and tailed.

The `IProgressReporter` writes to `monitor.ingestion_progress` at every batch boundary. The `monitor.ingestion_status` view computes throughput from consecutive entries.

---

## Configuration

```json
// appsettings.json
{
  "Database": {
    "ConnectionString": "Host=localhost;Port=5432;Database=hartonomous;Username=postgres;Password=postgres"
  },
  "Sources": {
    "UcdUca": "D:\\Models\\UCD",
    "Iso639": "D:\\Models\\ISO639",
    "WordNet": "D:\\Models\\princeton-wordnet",
    "Omw": "external\\omw",
    "UniversalDeps": "D:\\Models\\ud-treebanks",
    "Wiktionary": "D:\\Models\\wiktionary",
    "Tatoeba": "D:\\Models\\tatoeba",
    "Safetensors": "D:\\Models\\hub"
  },
  "BatchSize": 10000
}
```

**No environment variables.** No configuration indirection. One JSON file in the working directory. The CLI reads it at startup. Overrides via CLI arguments (`--batch-size 50000`).

---

## Dependency Injection Composition

The CLI `Program.cs` is the DI composition root:

```csharp
var services = new ServiceCollection();

// Core
services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var builder = new NpgsqlDataSourceBuilder(config.Database.ConnectionString);
    builder.UseNetTopologySuite();
    return builder.Build();
});

// Decomposers (seed)
services.AddTransient<UcdUcaDecomposer>();
services.AddTransient<Iso639Decomposer>();
services.AddTransient<WordNetDecomposer>();
services.AddTransient<OmwDecomposer>();
services.AddTransient<UdDecomposer>();
services.AddTransient<WiktionaryDecomposer>();
services.AddTransient<TatoebaDecomposer>();
services.AddTransient<SafetensorsDecomposer>();

// Decomposers (runtime — available after seed phases complete)
services.AddTransient<TextDecomposer>();
services.AddTransient<ImageDecomposer>();
services.AddTransient<AudioDecomposer>();
services.AddTransient<VideoDecomposer>();

// Analysis passes (registered by modality)
// Text passes
services.AddTransient<IAnalysisPass, MorphologicalAnalysis>();
services.AddTransient<IAnalysisPass, DependencyParsing>();
services.AddTransient<IAnalysisPass, SemanticSimilarity>();
services.AddTransient<IAnalysisPass, CrossLingualAlignment>();
services.AddTransient<IAnalysisPass, FrequencyAnalysis>();
services.AddTransient<IAnalysisPass, CollocationDetection>();
services.AddTransient<IAnalysisPass, EtymologyTracing>();
// Image passes
services.AddTransient<IAnalysisPass, FeatureExtraction>();
services.AddTransient<IAnalysisPass, SpatialDecomposition>();
services.AddTransient<IAnalysisPass, ColorSpaceAnalysis>();
services.AddTransient<IAnalysisPass, TextureClassification>();
services.AddTransient<IAnalysisPass, ShapeDetection>();
services.AddTransient<IAnalysisPass, PatternRecognition>();
services.AddTransient<IAnalysisPass, CompositionAnalysis>();
services.AddTransient<IAnalysisPass, PerceptualHashing>();
// Audio passes
services.AddTransient<IAnalysisPass, SpectralAnalysis>();
services.AddTransient<IAnalysisPass, PitchDetection>();
services.AddTransient<IAnalysisPass, RhythmAnalysis>();
services.AddTransient<IAnalysisPass, HarmonicAnalysis>();
services.AddTransient<IAnalysisPass, TimbreAnalysis>();
services.AddTransient<IAnalysisPass, OnsetDetection>();
services.AddTransient<IAnalysisPass, EnvelopeExtraction>();
services.AddTransient<IAnalysisPass, FormantAnalysis>();
services.AddTransient<IAnalysisPass, SourceSeparation>();
services.AddTransient<IAnalysisPass, SpatialAudioAnalysis>();
services.AddTransient<IAnalysisPass, DynamicRangeAnalysis>();
services.AddTransient<IAnalysisPass, NoiseProfiling>();
services.AddTransient<IAnalysisPass, TransientAnalysis>();
services.AddTransient<IAnalysisPass, ModulationAnalysis>();
services.AddTransient<IAnalysisPass, PsychoacousticModeling>();
services.AddTransient<IAnalysisPass, TemporalPattern>();
services.AddTransient<IAnalysisPass, SpectralPattern>();
services.AddTransient<IAnalysisPass, CrossModalAlignment>();
services.AddTransient<IAnalysisPass, MicrostructureAnalysis>();
services.AddTransient<IAnalysisPass, PhaseCoherence>();
services.AddTransient<IAnalysisPass, ResonanceDetection>();
services.AddTransient<IAnalysisPass, ArtifactDetection>();
// Video passes
services.AddTransient<IAnalysisPass, FrameDecomposition>();
services.AddTransient<IAnalysisPass, MotionEstimation>();
services.AddTransient<IAnalysisPass, SceneDetection>();
services.AddTransient<IAnalysisPass, TemporalSegmentation>();
services.AddTransient<IAnalysisPass, AudioVisualSync>();
services.AddTransient<IAnalysisPass, ObjectTracking>();

// Engine
services.AddSingleton<IPhaseRunner, SequentialPhaseRunner>();
services.AddTransient<IIngestionPipeline, NpgsqlIngestionPipeline>();
services.AddTransient<IProgressReporter, DatabaseProgressReporter>();
services.AddTransient<IHealthCheck, SqlHealthCheck>();
services.AddTransient<ISignificanceUpdater, GlickoSignificanceUpdater>();
services.AddTransient<ITraversal, SignificanceGuidedTraversal>();

// Logging
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
```

**Decomposers are `Transient`** — created fresh for each phase run, disposed after. The phase runner resolves decomposers by phase, creates pipeline instances, and manages the lifecycle. Runtime decomposers (Text, Image, Audio, Video) are available for on-demand ingestion after all seed phases complete.

**Analysis passes are `Transient`** — resolved as `IEnumerable<IAnalysisPass>` by the runtime decomposers. Each modality decomposer filters passes by their `Modality` property. All 43 passes from [analysis-passes.md](analysis-passes.md) are registered here. Pass class names correspond to analysis pass files in `Hartonomous.Analysis/` (see [project-structure.md](project-structure.md)).

---

## Helper Methods

### ResolveDecomposers

Maps a `Phase` enum value to the ordered list of decomposer instances for that phase. Uses the DI container to resolve concrete types.

```csharp
private IReadOnlyList<IDecomposer> ResolveDecomposers(Phase phase) => phase switch
{
    Phase.UcdUca       => [_services.GetRequiredService<UcdUcaDecomposer>()],
    Phase.Iso639       => [_services.GetRequiredService<Iso639Decomposer>()],
    Phase.WordNetOmw   => [_services.GetRequiredService<WordNetDecomposer>(),
                           _services.GetRequiredService<OmwDecomposer>()],
    Phase.UniversalDeps => [_services.GetRequiredService<UdDecomposer>()],
    Phase.ModelDecomp  => [_services.GetRequiredService<SafetensorsDecomposer>()],
    Phase.Wiktionary   => [_services.GetRequiredService<WiktionaryDecomposer>()],
    Phase.Tatoeba      => [_services.GetRequiredService<TatoebaDecomposer>()],
    _                  => []  // Non-decomposer phases (CoreAlgebra, SignificanceField, etc.)
};
```

**Ordering matters.** `WordNetOmw` returns `[WordNetDecomposer, OmwDecomposer]` because OMW references WordNet synsets. The `RunPhaseAsync` loop runs them sequentially in this order.
