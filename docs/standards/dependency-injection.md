# Dependency Injection & Assembly Boundaries

## Dependency Injection — Everything

Every service is registered in a DI container. Every dependency is injected through the constructor. No exceptions.

```csharp
// YES
public class WordNetDecomposer : BaseDecomposer
{
    public WordNetDecomposer(
        IIngestionPipeline pipeline,
        IProgressReporter progress,
        IOptions<WordNetOptions> options) : base(pipeline, progress)
    { }
}

// NO — hidden dependency, untestable, unreplaceable
public class WordNetDecomposer : BaseDecomposer
{
    public override async Task DecomposeAsync(CancellationToken ct)
    {
        var pipeline = new IngestionPipeline(connectionString); // NO
        var hasher = Blake3.Instance;                           // NO
    }
}
```

### Registration Rules

- **Transient**: stateless services that do work and hold no state between calls. Most decomposers, analysis passes, recomposers.
- **Scoped**: services tied to a unit of work (one ingestion batch, one API request). `IIngestionBatch`, database connections.
- **Singleton**: services that are expensive to create and safe to share. Native interop wrappers (`IBlake3Hasher`), configuration snapshots.

### No Service Locator

No `IServiceProvider.GetService<T>()` in business logic. If a class needs something, it declares it in its constructor. The only place that resolves services is the composition root (`Program.cs` or the host builder).

### No Static State

No `static` fields, properties, or methods that hold mutable state. Static helpers are acceptable only when they are pure functions with no side effects and no dependencies (e.g., math utilities). If it needs configuration, a connection, or any other service — it goes through DI.

---

## Assembly Boundaries: Microservice-Shaped Modules

The solution is a monolith at deployment time but structured internally like microservices. Each project has a clear bounded context, owns its registrations, and communicates with other modules only through interfaces defined in Core.

### The Module Registration Pattern

Each project exposes a single extension method that registers all its services:

```csharp
// In Hartonomous.Decomposers
public static class DecomposerRegistration
{
    public static IServiceCollection AddDecomposers(this IServiceCollection services)
    {
        services.AddTransient<IDecomposer, WordNetDecomposer>();
        services.AddTransient<IDecomposer, UcdUcaDecomposer>();
        services.AddTransient<IDecomposer, UniversalDependenciesDecomposer>();
        // ...
        return services;
    }
}

// In Hartonomous.Engine
public static class EngineRegistration
{
    public static IServiceCollection AddEngine(this IServiceCollection services)
    {
        services.AddScoped<IIngestionPipeline, IngestionPipeline>();
        services.AddTransient<ISignificanceUpdater, GlickoUpdater>();
        // ...
        return services;
    }
}
```

The composition root calls these:

```csharp
// Program.cs (CLI or API host)
services.AddDecomposers();
services.AddEngine();
services.AddNativeInterop();
services.AddMonitoring();
```

Each module is independently testable. Swap one registration, the rest don't notice.

### What Goes Where

| Project | Contains | Does NOT Contain |
|---------|----------|------------------|
| `Core` | Interfaces, base classes, domain records/enums, options types, error types | Any implementation. Any `using Npgsql`. Any P/Invoke. |
| `Decomposers` | Seed decomposer implementations, source parsers | Database connection logic, HTTP endpoints. |
| `Engine` | Ingestion pipeline, significance updater, traversal, batch management | Source parsing, API controllers, CLI argument parsing. |
| `Api` | ASP.NET controllers/endpoints, request/response DTOs, middleware | Decomposer logic, direct SQL. |
| `Cli` | Phase runner, CLI argument parsing, console output | API controllers, decomposer logic. |
| `Native` | P/Invoke declarations, native library loading, interop marshaling | Business logic. This is a thin wrapper. |
