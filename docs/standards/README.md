# Engineering Standards

This directory defines the quality bar for all code in the Hartonomous project. Every implementation spec, every PR, every code review must conform to these standards. This is not aspirational — this is the minimum.

The file layout conventions (one-file-per-object, directory structures) are in [architecture.md](../architecture.md) § Coding Standards. These documents cover the engineering patterns those files must follow.

---

## Contents

| Doc | Scope |
|-----|-------|
| [dependency-injection.md](dependency-injection.md) | DI everywhere, registration rules, no service locator, no static state, assembly boundaries, module registration pattern, project ownership table. |
| [design-principles.md](design-principles.md) | Interface-first design, no duplication of functionality, immutability by default, no worthless engineering, the holistic test. |
| [configuration-and-errors.md](configuration-and-errors.md) | Strongly typed configuration with `IOptions<T>`, error handling (Result&lt;T&gt; + fail loud), async by default, CancellationToken everywhere. |
| [testing.md](testing.md) | Every interface gets a test, test project structure, test naming, what gets mocked, what gets integration tested. |
| [sql.md](sql.md) | No inline SQL, naming conventions for all SQL objects, schema ownership, idempotent migrations. |
| [native.md](native.md) | C/C++ flat C API, memory rules, error returns, PG extension memory contexts. |
| [csharp-conventions.md](csharp-conventions.md) | C# naming conventions, structured logging, generic constraints and patterns, keyed services. |
| [ingestion-pipeline.md](ingestion-pipeline.md) | The unified ingestion pipeline. One pipeline for all writes. IngestionUnit types, provenance/tenant identity, C#-side deduplication, concurrency, index exploitation, decomposer relationship. |
