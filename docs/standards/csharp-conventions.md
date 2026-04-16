# C# Conventions

## Naming Conventions

Standard .NET conventions with project-specific precision.

| Element | Convention | Example |
|---------|-----------|---------|
| Namespace | `Hartonomous.{Project}` | `Hartonomous.Core`, `Hartonomous.Decomposers` |
| Interface | `I` + PascalCase | `IDecomposer`, `IIngestionPipeline` |
| Abstract class | `Base` + PascalCase | `BaseDecomposer`, `BaseAnalysisPass` |
| Concrete class | PascalCase, descriptive | `WordNetDecomposer`, `GlickoUpdater` |
| Method | PascalCase, verb-first | `UpsertEntityAsync`, `ComputeHash` |
| Async method | `...Async` suffix | `DecomposeAsync`, `TraverseAsync` |
| Property | PascalCase noun | `BatchSize`, `SourcePath` |
| Private field | `_camelCase` | `_pipeline`, `_logger` |
| Parameter | `camelCase` | `entityTypeId`, `cancellationToken` |
| Constant | PascalCase | `MaxBatchSize`, `DefaultMu` |
| Options class | `{Feature}Options` | `DatabaseOptions`, `WordNetOptions` |
| Registration method | `Add{Module}` | `AddDecomposers()`, `AddEngine()` |
| Record / DTO | PascalCase, no suffix unless ambiguous | `EntityResult`, `TraversalPath` |
| Enum | PascalCase, singular | `Modality`, `CuratorClass` |

### File Naming

One type per file. File name = type name. `IDecomposer.cs`, `WordNetDecomposer.cs`, `DatabaseOptions.cs`.

Exception: a record and its companion static factory can share a file if the factory exists solely for that record.

---

## Logging

Structured logging via `Microsoft.Extensions.Logging`. No `Console.WriteLine`. No `Debug.Print`.

### What to Log

| Level | When |
|-------|------|
| `Trace` | Granular per-entity/per-edge detail. Off in production. |
| `Debug` | Per-batch summaries, intermediate state. Off in production. |
| `Information` | Phase start/end, decomposer start/end, milestone counts (every N entities). |
| `Warning` | Recoverable anomalies: unexpected but handled data format, transient retry. |
| `Error` | Failures that halt the current operation. Always includes full context. |
| `Critical` | Failures that halt the entire process. |

### Structured Properties

```csharp
_logger.LogInformation(
    "Batch submitted: {EntityCount} entities, {EdgeCount} edges, {Duration}ms",
    batch.EntityCount, batch.EdgeCount, elapsed.TotalMilliseconds);
```

Named placeholders. Never string interpolation in log calls (defeats structured logging).

---

## Generic Constraints and Patterns

### IRecomposer<T>

`T` is the output type. Constrained appropriately:

```csharp
public interface IRecomposer<T> where T : class
{
    Task<T> RecomposeAsync(long entityId, CancellationToken ct);
}
```

Implementations: `IRecomposer<Stream>` for binary formats, `IRecomposer<string>` for text, etc. The consumer specifies what output format it wants by requesting the right `IRecomposer<T>`.

### Keyed Services for Multiple Implementations

Multiple decomposers all implement `IDecomposer`. Resolve by name when you need a specific one:

```csharp
services.AddKeyedTransient<IDecomposer, WordNetDecomposer>("wordnet");
services.AddKeyedTransient<IDecomposer, UcdUcaDecomposer>("ucd_uca");

// Phase runner resolves by key
var decomposer = provider.GetRequiredKeyedService<IDecomposer>("wordnet");
```

Or resolve all of them for phase execution:

```csharp
public PhaseRunner(IEnumerable<IDecomposer> decomposers) { }
```

### No Unconstrained Generics

Every generic parameter has at least one constraint — `where T : class`, `where T : IEntity`, `where T : struct`. Unconstrained generics are a code smell that means the abstraction is too vague.
