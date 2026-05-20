# Design Principles

## Interface-First Design

Every injectable service has an interface. The interface is the contract. The concrete class is an implementation detail.

### Interface Placement

All interfaces live in `Hartonomous.Core`. Implementations live in their respective project. This enforces the dependency rule: everything depends on Core, Core depends on nothing.

```
Hartonomous.Core/             ← interfaces, base classes, domain types, enums
Hartonomous.Decomposers/      ← depends on Core
Hartonomous.Engine/           ← depends on Core
Hartonomous.Api/              ← depends on Core
Hartonomous.Cli/              ← depends on Core (and transitively on everything via DI)
```

No project references from Core to any implementation project. Ever. The dependency arrow points inward.

### Interface Granularity

One interface per capability. Not one mega-interface that every service implements.

```csharp
// YES — focused contracts
public interface IEntityHasher
{
    byte[] ComputeHash(ReadOnlySpan<byte> content);
    byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes);
}

public interface IEntityUpserter
{
    Task<long> UpsertEntityAsync(byte[] hash, int entityTypeId, CancellationToken ct);
}

// NO — god interface
public interface ISubstrateOperations
{
    byte[] Hash(byte[] content);
    Task<long> UpsertEntity(...);
    Task<long> CreateEdge(...);
    Task UpdateSignificance(...);
    // ... 40 more methods
}
```

### When NOT to Create an Interface

- DTOs / record types / value objects — these are data, not services.
- Extension methods — these are syntax sugar over existing interfaces.
- Static pure-function helpers — no state, no dependencies, nothing to mock.

---

## No Duplication of Functionality

One operation, one implementation, one location. If two classes need the same behavior, that behavior lives in exactly one place and both classes consume it.

### Hash Computation

BLAKE3 is implemented once in `libhartonomous` (C++). Exposed to C# through one P/Invoke wrapper class implementing `IEntityHasher`. Exposed to PostgreSQL through one extension function. No managed C# reimplementation. No copy-paste of the algorithm.

### Entity Creation

Entity upsert logic lives in one stored procedure. Called through one C# method on `IIngestionPipeline`. No decomposer builds its own INSERT statement. No decomposer has its own dedup logic.

### Geometry Computation

S3 distance, centroid, Fibonacci projection — each implemented once in `libhartonomous`. Wrapped once for C#, once for PG extension. No parallel implementations.

### The DRY Test

Before writing any function, method, or procedure, search for existing implementations. If anything does the same thing, refactor to share it. If two implementations drift apart, that is a defect.

---

## Immutability by Default

### Records for Data

Data that flows between services is `record` or `readonly record struct`. Not mutable classes.

```csharp
public readonly record struct EntityResult(long Id, byte[] Hash, int EntityTypeId);
public sealed record TraversalPath(IReadOnlyList<long> EntityIds, double CumulativeSignificance);
public sealed record Error(string Code, string Message, Exception? Inner = null);
```

### Mutable State Is Explicit

If a class has mutable state, that's a design decision that must be justified. Batch builders accumulate state — fine, but they're scoped (one per batch) and disposed after submission.

---

## No Worthless Engineering

This system is a reinvention of AI. It replaces the black box with a crystal ball. It eliminates the GPU requirement and runs on commodity hardware. That mission demands heavy, precise engineering at every layer — database as AI model, Glicko-2 significance on typed semantic edges, S3 geometric physicality, BLAKE3 Merkle DAGs, n-ary edge traversal, PostGIS spatial operators across modalities.

Every piece of that engineering earns its place because the system cannot work without it.

"No worthless engineering" means: every abstraction, every interface, every layer must serve the mission. The test is not "is this elegant?" — it is "does the crystal ball break without this?"

### What IS Worthless

- **Abstractions that exist to be abstract.** A `StrategyFactory<T>` that wraps one strategy. A `PipelineBuilder` that builds one pipeline configuration. If the indirection adds nothing, it is clutter.
- **Defensive code against impossible states.** If the schema enforces a CHECK constraint, C# does not also validate the same invariant. The database IS the authority. Trust it.
- **Configurability for values that will never change.** BLAKE3 output is 32 bytes. Don't make it configurable. Glicko-2 has three rating components (mu, sigma, volatility). Don't abstract it to N components.
- **Copy-paste "flexibility."** Two decomposers doing the same upsert logic slightly differently because "they might diverge someday." They won't. One pipeline. One path. One implementation.
- **Premature performance abstractions.** Don't add Redis caching, read replicas, or materialized view refresh strategies until profiling proves a bottleneck exists. Add them when you need them, behind an interface, and they'll slot right in because the architecture is clean.

### What Is NOT Worthless

- **Interface-first design** — not overhead, it's the skeleton that makes every module replaceable and testable.
- **DI everywhere** — not ceremony, it's the wiring that makes the monolith behave like microservices.
- **The ingestion pipeline** — not over-built, it's the central nervous system that every decomposer and analysis pass depends on (see [ingestion-pipeline.md](ingestion-pipeline.md)).
- **Schema-qualified SQL contracts** — not bureaucracy, it's the API boundary that lets the database optimize independently.
- **BLAKE3 in C++ shared across PG and C#** — not premature optimization, it's the deduplication guarantee that makes the Merkle DAG work.
- **PostGIS geometry types for non-geographic data** — not a weird hack, it's the insight that makes cross-modal similarity queryable with one operator.

### The Holistic Test

Before adding any component, ask: "Does this serve the whole system, or just the current task?" If an abstraction makes one decomposer cleaner but doesn't compose with the rest of the system, it's local optimization — worthless in context. If it makes the ingestion pipeline faster for all decomposers, exploits the database's indexing for all queries, and makes testing easier for all modules — that's holistic engineering. That's what we do.

Never lose the forest for the trees. Every line of code exists because the crystal ball demands it.
