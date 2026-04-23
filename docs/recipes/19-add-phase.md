# Recipe 19: Add a Phase

Intent: add a new orchestration phase (e.g., `GovernanceCorpus`, `MultilingualAlignment`) to the `Phase` enum and wire its execution order into the `SequentialPhaseRunner`.

Phases run in dependency order during seed and runtime ingestion. Each phase is a coarse unit of work containing one or more decomposers.

---

## Prerequisites

- Phase has a clear name describing what it accomplishes (e.g., `GovernanceCorpus` runs the corpora that populate `entity_pragmatic_register`).
- Phase has a defined position in the dependency order — what must run before it, what must run after.
- One or more decomposers will declare this phase in their `Phases` property.

---

## Steps

### 1. Add the enum value

`src/Hartonomous.Core/Orchestration/Phase.cs`:

```csharp
public enum Phase
{
    CoreAlgebra      = 0,
    UcdUca           = 1,
    Iso639           = 2,
    WordNetOmw       = 3,
    UniversalDeps    = 4,
    ModelDecomp      = 5,
    Wiktionary       = 6,
    Tatoeba          = 7,
    TextDecomp       = 8,
    SignificanceField = 9,
    {NewPhase}       = 10,    // ← inserted in dependency order
    InferenceEngine  = 11,
    Validation       = 12,
}
```

The integer value determines execution order. Insert between phases that bracket the new phase's dependencies. Bumping later values is fine — they're never persisted; the runtime resolves by enum name.

### 2. Update the runner's dependency graph (if needed)

`src/Hartonomous.Engine/Orchestration/SequentialPhaseRunner.cs` already runs phases in enum-integer order. If your phase has additional dependencies beyond ordinal precedence (e.g., requires data from a non-immediately-prior phase), add a constraint to the dependency graph in `PhaseDependencies.cs`:

```csharp
public static readonly IReadOnlyDictionary<Phase, IReadOnlyList<Phase>> Dependencies =
    new Dictionary<Phase, IReadOnlyList<Phase>>
    {
        // ... existing
        [Phase.{NewPhase}] = [Phase.UcdUca, Phase.WordNetOmw],  // hard dependencies
    };
```

The runner topologically sorts phases by these dependencies and refuses to start a phase whose dependencies haven't completed.

### 3. Have decomposers declare the phase

Each decomposer that participates in this phase declares it:

```csharp
public override IReadOnlyList<Phase> Phases => [Phase.{NewPhase}];
```

A decomposer can participate in multiple phases (e.g., text decomposer participates in both `Tatoeba` and `TextDecomp` indirectly via being called by other decomposers — but its own declared phases cover when it runs as a primary).

### 4. Add a phase-level entrypoint

`scripts/ops/Phases.ps1` already supports running individual phases. Add the new phase code:

```powershell
# Add to the $PhaseMap inside Phases.ps1
$PhaseMap = @{
    # ... existing
    '{kebab-name}' = @{ Phase = '{NewPhase}'; Description = '{What this phase does}' }
}
```

Usage: `pwsh scripts/ops/Phases.ps1 -Run {kebab-name}`.

### 5. Update seed orchestration (if applicable)

If the new phase should be part of `seed/All.ps1`, add it in dependency order:

```powershell
# scripts/seed/All.ps1
& $PSScriptRoot/Ucd.ps1
& $PSScriptRoot/Iso639.ps1
& $PSScriptRoot/WordNetOmw.ps1
& $PSScriptRoot/UniversalDeps.ps1
& $PSScriptRoot/Wiktionary.ps1
& $PSScriptRoot/Tatoeba.ps1
& $PSScriptRoot/{NewPhase}.ps1   # ← new phase entrypoint
```

Each `scripts/seed/{Phase}.ps1` script invokes the relevant decomposer CLI commands for that phase.

### 6. Add tests

`tests/Hartonomous.Engine.Tests/Orchestration/PhaseRunnerTests.cs` (extend if exists):

```csharp
[Fact]
public async Task PhaseRunner_RespectsDependencyOrder_For{NewPhase}()
{
    var runner = new SequentialPhaseRunner(/* mock decomposers */);
    await runner.RunAsync([Phase.{NewPhase}], CancellationToken.None);
    // assert dependencies ran first, then the new phase
}

[Fact]
public async Task PhaseRunner_Refuses_WhenDependenciesNotMet()
{
    // Try to run NewPhase without WordNetOmw having completed.
    var act = () => runner.RunAsync([Phase.{NewPhase}], CancellationToken.None);
    await act.Should().ThrowAsync<PhaseDependencyException>();
}
```

### 7. Document

- `docs/specs/csharp/phase-runner.md` — add the new phase to the inventory with its position, dependencies, and decomposers.
- `docs/architecture.md` § Phases — include in the list.

### 8. Run and verify

```pwsh
pwsh scripts/build/Dotnet.ps1
pwsh scripts/test/Dotnet.ps1 -Filter PhaseRunnerTests
pwsh scripts/ops/Phases.ps1 -Run {kebab-name}
```

---

## Canonical example — adding `GovernanceCorpus`

```csharp
// src/Hartonomous.Core/Orchestration/Phase.cs
public enum Phase
{
    // ... existing through Tatoeba (7), TextDecomp (8), SignificanceField (9)
    GovernanceCorpus  = 10,    // depends on Tatoeba (for some attested negative-sentiment sentences) and Wiktionary (for register vocabulary)
    InferenceEngine   = 11,
    Validation        = 12,
}
```

```csharp
// src/Hartonomous.Engine/Orchestration/PhaseDependencies.cs
[Phase.GovernanceCorpus] = [Phase.Tatoeba, Phase.Wiktionary],
```

```csharp
// src/Hartonomous.Decomposers/ConflictCorpus/ConflictCorpusDecomposer.cs
public sealed class ConflictCorpusDecomposer : Decomposer<ConflictCorpusSource>
{
    public override IReadOnlyList<Phase> Phases => [Phase.GovernanceCorpus];
}
```

---

## Anti-patterns

- **DON'T** insert a phase out of dependency order. The enum value determines execution order; if you need to run before X, your value must be less than X's.
- **DON'T** declare a phase that no decomposer participates in. Empty phases are dead code.
- **DON'T** make every decomposer participate in every phase. A decomposer's `Phases` should list only phases where it's the PRIMARY producer.
- **DON'T** introduce a phase without a corresponding `scripts/seed/{Phase}.ps1` or `scripts/ops/{Verb}.ps1` entrypoint. Operations go through scripts.
- **DON'T** run a phase manually outside the runner. The runner records phase status in `monitor.phase_status`; bypassing it leaves status tracking inaccurate.

---

## Verification checklist

- [ ] Enum value added at correct ordinal position
- [ ] Dependencies declared in `PhaseDependencies` (if non-trivial)
- [ ] At least one decomposer declares the new phase
- [ ] Script entrypoint added (`Phases.ps1` map and dedicated script)
- [ ] `seed/All.ps1` updated if part of seed orchestration
- [ ] Tests cover dependency enforcement
- [ ] Phase inventory updated in `docs/specs/csharp/phase-runner.md`

---

## Related recipes

- `08-add-decomposer.md` — decomposers participate in phases
- `18-add-cli-command.md` — phase entrypoints are CLI commands
