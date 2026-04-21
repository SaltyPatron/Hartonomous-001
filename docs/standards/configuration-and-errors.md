# Configuration, Error Handling & Async

## Configuration: Strongly Typed, Validated at Startup

No magic strings. No `Configuration["ConnectionStrings:Default"]` scattered through code.

### Options Pattern

Every configurable concern gets its own options class in Core, bound from configuration, validated at startup:

```csharp
public sealed class DatabaseOptions
{
    public required string ConnectionString { get; init; }
    public int MinPoolSize { get; init; } = 5;
    public int MaxPoolSize { get; init; } = 20;
    public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed class IngestionOptions
{
    public int BatchSize { get; init; } = 5000;
    public int MaxConcurrentDecomposers { get; init; } = 1;
}

public sealed class WordNetOptions
{
    public required string SourcePath { get; init; }
}
```

Registered with validation:

```csharp
services.AddOptions<DatabaseOptions>()
    .BindConfiguration("Database")
    .ValidateDataAnnotations()
    .ValidateOnStart();  // fail loud at startup, not at first use
```

### Configuration Sources (Precedence)

1. Command-line arguments (highest)
2. Environment variables (`HARTONOMOUS_DB`)
3. `appsettings.{Environment}.json`
4. `appsettings.json` (lowest)

Standard .NET configuration layering. No custom config parsers.

---

## Error Handling: Result Types + Fail Loud

Two complementary patterns. They are not interchangeable.

### Result<T> for Expected Outcomes

Operations that have known failure modes return a discriminated result, not exceptions:

```csharp
public readonly record struct Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Error error) => new(error);
}

public sealed record Error(string Code, string Message, Exception? Inner = null);
```

Use for: entity upsert (might return existing ID), source file parsing (might have bad data), batch submission (might have constraint violations).

### Exceptions for Bugs and Infrastructure Failures

Connection lost, out of memory, null reference, index out of range — these are not "expected outcomes." These throw exceptions and propagate up to the phase runner, which halts and reports.

### No Catch-and-Continue

```csharp
// NO — this is the cardinal sin
try { ProcessEntity(entity); }
catch (Exception ex) { _logger.LogWarning(ex, "Skipping entity"); }

// YES — if it fails, everything stops
try { ProcessEntity(entity); }
catch (Exception ex)
{
    _logger.LogError(ex, "Entity processing failed: {Hash}", entity.Hash);
    throw; // propagate — the batch fails, the phase halts
}
```

The only retry-eligible errors are transient infrastructure failures (database connection timeout, deadlock). These retry at the pipeline level with bounded attempts, not inside individual operations.

---

## Async by Default, Cancellation Always

All I/O-bound operations are async. All async methods accept `CancellationToken`.

```csharp
// YES
Task<long> UpsertEntityAsync(byte[] hash, int entityTypeId, CancellationToken ct);

// NO — no cancellation support
Task<long> UpsertEntityAsync(byte[] hash, int entityTypeId);

// NO — blocking I/O
long UpsertEntity(byte[] hash, int entityTypeId);
```

### When Sync Is Acceptable

- Pure computation (hash calculation, geometry math, in-memory transforms).
- Trivial property access.
- Inside the native C/C++ layer (no async in P/Invoke — the managed wrapper makes it async if needed).

### CancellationToken Propagation

Every async method passes its token to every async call it makes. The token originates from the phase runner (CLI) or the request pipeline (API). When the operator hits Ctrl+C or a request times out, everything unwinds cleanly.
