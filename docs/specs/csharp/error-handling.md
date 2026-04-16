# Error Handling

**Status**: ✅ Complete

Error types, the "fail loud" pattern (Law #13), and how errors propagate from decomposers through the ingestion pipeline to the monitoring schema. No silent swallowing.

---

## Error Hierarchy

```
SubstrateException (abstract)
├── SourceValidationException       -- Source files missing, corrupt, unexpected format
├── IngestionException              -- Database write failures
│   ├── BatchSubmitException        -- Transaction-level failure during batch submit
│   ├── ConstraintViolationException -- FK or UNIQUE violation (should not happen if code is correct)
│   └── ConnectionException         -- PostgreSQL unreachable
├── TraversalException              -- Inference/traversal failures
│   ├── CostBudgetExceededException -- Traversal hit cost limit
│   └── CycleDetectedException     -- Traversal detected a cycle (safety, should not happen)
├── SignificanceException           -- Glicko-2 update failures
├── PhaseException                  -- Phase-level orchestration failures
│   ├── DependencyNotMetException   -- Prerequisite phase not complete
│   └── PhaseAlreadyCompleteException -- Re-running a completed phase without --force
└── ConfigurationException          -- Missing or invalid configuration
```

All exceptions inherit from `SubstrateException`:

```csharp
public abstract class SubstrateException : Exception
{
    /// <summary>
    /// Structured error context for diagnostics and monitoring.
    /// </summary>
    public ErrorContext Context { get; }

    protected SubstrateException(string message, ErrorContext context, Exception? inner = null)
        : base(message, inner)
    {
        Context = context;
    }
}

public sealed record ErrorContext
{
    public string? DecomposerCode { get; init; }
    public string? PhaseCode { get; init; }
    public string? SourceFile { get; init; }
    public long? SourceLine { get; init; }
    public byte[]? EntityHash { get; init; }
    public long? EntityId { get; init; }
    public long? EdgeId { get; init; }
    public int? BatchNumber { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
```

---

## Recoverability Classification

| Error Category | Recoverable? | Action |
|----------------|-------------|--------|
| Source file missing | **No** | Halt. Fix deployment. Re-run. |
| Source file corrupt | **No** | Halt. Replace source. Re-run. |
| Connection failure | **No** | Halt. Fix PostgreSQL. Re-run. |
| Deadlock | **No** | Halt. The stored procedure handles lock ordering — a deadlock means a bug. Fix code. |
| Constraint violation | **No** | Halt. Means a logic error in the decomposer — wrong FK reference, duplicate edge without proper hashing. Fix code. |
| Storage full | **No** | Halt. Law #13 — capacity planning defect. |
| Cost budget exceeded | **Yes** (by design) | Logged. Traversal returns partial result up to budget. This is the intentional termination mechanism. |
| Configuration missing | **No** | Halt. Fix configuration. Re-run. |

**No retries. No backoff. No circuit breakers.**

Transient errors (intermittent connection drops, temporary locks) do not exist in this system. PostgreSQL runs on localhost. If the connection drops, the database is down — that is a halt condition, not a retry condition.

---

## The Fail Loud Pattern

From Substrate Law #13: "Every operation succeeds completely or fails explicitly with full diagnostic context."

### Concrete Rules

1. **No empty catch blocks.** The codebase has zero `catch {}` or `catch (Exception) { }` blocks. Static analysis enforces this via `.editorconfig`: `dotnet_diagnostic.CA1031.severity = error`.

2. **No catch-and-continue.** No `try { ... } catch { logger.LogWarning(...); continue; }`. If an operation in a batch fails, the batch fails. If a batch fails, the decomposer fails. If a decomposer fails, the phase fails. No partial results.

3. **No graceful degradation.** The system does not "do its best" with incomplete data. If Wiktionary source files are missing, the Wiktionary decomposer halts — it does not skip missing files and process the rest.

4. **No fallback continuations.** If the C native library is unavailable for BLAKE3, the system does not fall back to a managed implementation. It halts. The native library is a hard dependency, not an optimization.

5. **All exceptions carry context.** Every `throw` creates a `SubstrateException` subclass with a populated `ErrorContext` record. "Something went wrong" is never acceptable.

### How It Works in Practice

```csharp
// In a decomposer's DecomposeCoreAsync:
await foreach (var entityBatch in ParseSourceFile(path, ct))
{
    var batch = pipeline.CreateBatch();
    foreach (var entry in entityBatch)
    {
        var hash = ComputeHash(entry.Content);
        var handle = batch.AddEntity(hash, entry.TypeCode);
        // ... edges, junctions, etc.
    }

    // If this throws, DecomposeCoreAsync throws.
    // BaseDecomposer.DecomposeAsync does not catch it.
    // PhaseRunner receives the exception and halts the phase.
    await pipeline.SubmitBatchAsync(batch, ct);
    await reporter.ReportAsync(snapshot, ct);
}

// There is NO try/catch here. The decomposer does not handle errors.
// Errors propagate to the phase runner, which halts and reports.
```

---

## Error Propagation Chain

```
Parser throws (corrupt source data)
  → DecomposeCoreAsync propagates
    → BaseDecomposer.DecomposeAsync propagates
      → PhaseRunner catches at the top level
        → PhaseRunner logs with full ErrorContext
        → PhaseRunner writes failure to monitor.ingestion_progress
        → PhaseRunner returns PhaseResult { Status = Failed, ErrorMessage = ... }
        → CLI prints the error with full context
        → Process exits with code 1
```

Each layer adds context:

| Layer | Context Added |
|-------|--------------|
| Parser | Source file path, line number, raw content |
| Decomposer | Provenance code, entity hash, batch number |
| Pipeline | Transaction ID, stored procedure name |
| Phase Runner | Phase, decomposer, elapsed time |
| CLI | Full formatted error message for the operator |

---

## Monitoring Schema Integration

When a phase fails, the phase runner writes the failure to `monitor.ingestion_progress`:

```sql
-- Written by PhaseRunner on failure
CALL monitor.report_progress(
    p_decomposer := 'princeton_wordnet',
    p_phase := 'Phase2c_WordNet_OMW',
    p_entities_created := 45000,
    p_edges_created := 120000,
    p_duplicates_skipped := 3200,
    p_error := 'SourceValidationException: Expected 29 WordNet data files in D:\Models\princeton-wordnet, found 27. Missing: data.adv, index.adv'
);
```

The `monitor.ingestion_status` view surfaces stuck and failed decomposers. An operator querying that view sees immediately:
- Which decomposer failed
- When it last reported progress
- How far it got before failing
- The full error message

---

## Logging

**Framework**: `Microsoft.Extensions.Logging` with structured logging.

**Sinks**: Console (always) + file (when configured). No monitor schema logging of individual log lines — only progress snapshots and error summaries.

**Structured fields on every log entry**:

| Field | Source | Example |
|-------|--------|---------|
| `Timestamp` | Logger | `2025-01-15T10:23:45.123Z` |
| `Level` | Logger | `Error` |
| `Decomposer` | DecomposerCode | `princeton_wordnet` |
| `Phase` | Phase enum | `WordNetOmw` |
| `EntityHash` | ErrorContext | `a3f2...` (hex) |
| `SourceFile` | ErrorContext | `data.noun` |
| `SourceLine` | ErrorContext | `14523` |
| `BatchNumber` | ErrorContext | `45` |
| `Message` | Exception | Full message |
| `StackTrace` | Exception | Full trace |

**Log levels used**:

| Level | Usage |
|-------|-------|
| `Information` | Phase start/complete, decomposer start/complete, batch submission counts. |
| `Warning` | Never used. There are no "warnings" in this system. Something either succeeds or fails. |
| `Error` | Any exception. Accompanied by halt. |
| `Debug` | Per-entity/per-edge details (disabled by default — produces enormous volume). |

**Warning is never used.** This is deliberate. A "warning" implies "something bad happened but we continued anyway." Law #13 forbids that. If it's worth logging, it's either informational (succeeded) or an error (halted).
