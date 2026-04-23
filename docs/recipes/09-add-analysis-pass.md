# Recipe 09: Add an Analysis Pass

Intent: add a new analysis pass (e.g., a new model-decomposition pass like `RotaryPositionPass`, or a new modality-content pass like `BeatTrackingPass`).

Two pass families exist:
- **Model analysis passes** — `IModelAnalysisPass` in `src/Hartonomous.Decomposers/Safetensors/Passes/`. Operate on safetensors model weights.
- **Modality analysis passes** — passes within text/image/audio/video decomposers. Operate on content of that modality.

Recipe shows the model-pass pattern; modality passes follow the same shape with the modality-specific context type.

---

## Prerequisites

- `PassId` — stable identifier, format `{family}.{name}` (e.g., `model.rotary_position`).
- Dependencies on other passes resolved (declare as `IReadOnlyList<string> Dependencies`).
- Entity / edge / physicality types the pass produces are registered.

---

## Steps

### 1. Create the pass class

`src/Hartonomous.Decomposers/Safetensors/Passes/{Pascal}Pass.cs`:

```csharp
namespace Hartonomous.Decomposers.Safetensors.Passes;

public sealed partial class {Pascal}Pass : IModelAnalysisPass
{
    public string PassId => "model.{snake_name}";

    public IReadOnlyList<string> Dependencies => [
        // PassIds of other passes that must complete first on the same model.
        // Empty if independent.
    ];

    public IReadOnlyList<string> AppliesToArchitectures => [
        // empty = all architectures, or list specific ones (e.g., "llama", "gpt").
    ];

    private readonly ILogger _logger;

    public {Pascal}Pass(ILogger logger) => _logger = logger;

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        ulong baseSeed = context.DeriveSeed(PassId);

        // Iterate over context.Tensors (or context.Layers, etc., depending on the pass).
        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            // Filter by tensor classification, role, shape, etc.
            if (!ShouldProcess(t)) continue;

            // Read tensor data via SafetensorsReader.
            // Compute pass-specific result.
            // Build EntitySpec / EdgeSpec / PhysicalitySpec.
            // Submit via session.SubmitBatch(...).

            session.Checkpoint(t.Info.Name); // record progress
        }

        Log.PassComplete(_logger, PassId, context.Model.Name);
    }

    private static bool ShouldProcess(TensorHandle t) =>
        // pass-specific filter
        true;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "{PassId} complete on {Model}")]
    static partial class Log { static partial void PassComplete(ILogger logger, string PassId, string Model); }
}
```

### 2. Declare the dependency contract

If your pass depends on outputs from another pass, the dependency MUST be declared:

```csharp
public IReadOnlyList<string> Dependencies => [
    "model.embedding_fireflies",  // depends on this pass having run first
];
```

The pass orchestrator (`ModelPassOrchestrator`) topologically sorts passes by dependency. A cycle is a hard error.

### 3. Register the pass in the orchestrator

Edit `src/Hartonomous.Decomposers/Safetensors/Passes/ModelPassOrchestrator.cs` (or the equivalent registration list):

```csharp
private static readonly IReadOnlyList<Type> RegisteredPasses = [
    // ... existing
    typeof({Pascal}Pass),
];
```

DI registration:

```csharp
// In DecomposerRegistration or equivalent
services.AddTransient<{Pascal}Pass>();
services.AddTransient<IModelAnalysisPass, {Pascal}Pass>(sp => sp.GetRequiredService<{Pascal}Pass>());
```

### 4. Add tests

`tests/Hartonomous.Decomposers.Tests/Safetensors/Passes/{Pascal}PassTests.cs`:

```csharp
public class {Pascal}PassTests
{
    [Fact]
    public async Task RunAsync_FixtureModel_ProducesExpectedRecords()
    {
        // arrange: build a tiny synthetic ModelPassContext
        // act: run the pass
        // assert: session received the expected EntitySpec / EdgeSpec / PhysicalitySpec records
    }
}
```

Use hand-written fakes for `IPassSession`. Synthetic context construction goes via a test helper.

### 5. Document

- `docs/specs/decomposers/analysis-passes.md` — add the pass to the catalogue with its `PassId`, dependencies, what it produces, and what tensors/layers it operates on.

### 6. Run and verify

```pwsh
pwsh scripts/build/Dotnet.ps1
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}PassTests
```

---

## The `IModelAnalysisPass` contract — full

```csharp
public interface IModelAnalysisPass
{
    /// Stable ID for checkpointing and dependency resolution.
    string PassId { get; }

    /// PassIds that must complete on the same model before this pass runs.
    IReadOnlyList<string> Dependencies { get; }

    /// Architecture codes this pass applies to. Empty = all.
    IReadOnlyList<string> AppliesToArchitectures { get; }

    /// Run the pass against the given model context, submitting outputs via the session.
    Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct);
}
```

`ModelPassContext` carries:
- `ModelArchitectureHandle Model`
- `IReadOnlyList<TensorHandle> Tensors`
- `IReadOnlyList<LayerHandle> Layers`
- `ulong DeriveSeed(string passId)` — deterministic per-pass seed
- `ICanonicalSignatureBuilder CanonicalSignature`

`IPassSession` carries:
- `Task SubmitBatchAsync(IngestionBatch batch, CancellationToken ct)`
- `void Checkpoint(string marker)` — for resumable passes

---

## Anti-patterns (specific to passes)

- **DON'T** read substrate state during a pass. Passes are producers, not consumers.
- **DON'T** declare false dependencies. The orchestrator runs dependency-free passes in parallel; falsely declaring a dependency forces serial execution.
- **DON'T** mutate `ModelPassContext`. It's read-only state.
- **DON'T** keep state across `RunAsync` invocations. Each invocation is independent and checkpointable.
- **DON'T** call non-deterministic compute. Passes must produce identical output for identical input (Law #6).
- **DON'T** swallow exceptions. Let them propagate so the orchestrator fails the model cleanly.

---

## Verification checklist

- [ ] Pass class is `sealed partial`, in correct namespace
- [ ] `PassId` follows `{family}.{name}` convention
- [ ] `Dependencies` lists actual dependencies only
- [ ] DI registration added
- [ ] Pass listed in `ModelPassOrchestrator` registered passes
- [ ] Unit tests cover the produced records
- [ ] Pass runs deterministically (same input → same outputs, byte-identical)
- [ ] Catalogue entry added to `docs/specs/decomposers/analysis-passes.md`

---

## Related recipes

- `08-add-decomposer.md` — host decomposer
- `04-add-physicality-type.md` — if pass produces a new physicality type
- `02-add-entity-type.md`, `03-add-edge-type.md` — if pass produces new entity/edge types
