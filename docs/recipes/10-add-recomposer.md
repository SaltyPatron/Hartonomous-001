# Recipe 10: Add a Recomposer

Intent: add a deterministic recomposer that reconstructs output (text, image, audio, video, safetensors) from substrate state. Recomposers are the inverse of decomposers — substrate state in, modality output out.

Recomposers do NOT generate. They reconstruct. No model inference, no learned components. See AP-INF-2.

---

## Prerequisites

- Output type `T` (e.g., `string` for text, `byte[]` for image, `Stream` for safetensors).
- Decomposer for the same modality exists and produces a substrate representation that can round-trip.
- All entity types, edge types, sequence rows, and physicalities needed are populated.

---

## Steps

### 1. Create the recomposer file

`src/Hartonomous.Recomposers/{Pascal}Recomposer.cs`:

```csharp
namespace Hartonomous.Recomposers;

public sealed class {Pascal}Recomposer : BaseRecomposer<{TOutputType}>, I{Pascal}Recomposer
{
    private readonly IEntityReader _entityReader;
    private readonly ISequenceReader _sequenceReader;
    private readonly ITraversal _traversal;
    private readonly ILogger<{Pascal}Recomposer> _logger;

    public {Pascal}Recomposer(
        IEntityReader entityReader,
        ISequenceReader sequenceReader,
        ITraversal traversal,
        ILogger<{Pascal}Recomposer> logger)
    {
        _entityReader = entityReader;
        _sequenceReader = sequenceReader;
        _traversal = traversal;
        _logger = logger;
    }

    public override async Task<{TOutputType}> RecomposeAsync(
        long rootEntityId, RecompositionOptions options, CancellationToken ct)
    {
        // 1. Read root entity metadata.
        var root = await _entityReader.GetByIdAsync(rootEntityId, ct);

        // 2. Walk the composition DAG via substrate.sequence rows.
        var children = await _sequenceReader.GetOrderedChildrenAsync(rootEntityId, ct);

        // 3. Recursively reconstruct each child.
        var parts = new List<{TPartType}>(children.Count);
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            parts.Add(await RecomposeChildAsync(child, options, ct));
        }

        // 4. Reassemble parts into the output. Deterministic — same substrate state, same output.
        return AssembleOutput(parts, root, options);
    }

    private async Task<{TPartType}> RecomposeChildAsync(SequenceChild child, RecompositionOptions options, CancellationToken ct)
    {
        // ... per-modality reconstruction
    }

    private static {TOutputType} AssembleOutput(IReadOnlyList<{TPartType}> parts, EntityRecord root, RecompositionOptions options)
    {
        // ... deterministic concatenation / decoding / rendering
    }
}
```

### 2. Define the recomposer interface

`src/Hartonomous.Core/Recomposition/I{Pascal}Recomposer.cs`:

```csharp
namespace Hartonomous.Core.Recomposition;

public interface I{Pascal}Recomposer
{
    Task<{TOutputType}> RecomposeAsync(long rootEntityId, RecompositionOptions options, CancellationToken ct);

    /// Optional streaming surface for large outputs.
    IAsyncEnumerable<{TOutputType}> RecomposeStreamAsync(long rootEntityId, RecompositionOptions options, CancellationToken ct);
}
```

### 3. Register in DI

```csharp
services.AddScoped<{Pascal}Recomposer>();
services.AddScoped<I{Pascal}Recomposer, {Pascal}Recomposer>(sp => sp.GetRequiredService<{Pascal}Recomposer>());
```

### 4. Wire into inference results

If recomposition is called from inference, add it to the inference flow:

```csharp
// In a CLI command or API endpoint
var inference = await _engine.InferAsync(query, ct);
var recomposed = await _recomposer.RecomposeAsync(inference.Paths[0].Endpoint, options, ct);
```

### 5. Add tests

#### Unit (with hand-written fakes)

`tests/Hartonomous.Recomposers.Tests/{Pascal}RecomposerTests.cs`:

```csharp
public class {Pascal}RecomposerTests
{
    [Fact]
    public async Task RecomposeAsync_KnownRoot_ProducesExpectedOutput()
    {
        // arrange: hand-written IEntityReader / ISequenceReader returning a known DAG
        // act: recompose
        // assert: byte-equal expected output
    }
}
```

#### Round-trip (integration)

`tests/Hartonomous.Integration.Tests/{Pascal}RoundTripTests.cs`:

```csharp
[Fact]
public async Task DecomposeRecompose_Fixture_IsByteIdentical()
{
    // arrange: load a fixture file
    var input = await File.ReadAllBytesAsync(fixturePath);

    // act: decompose then recompose
    var rootId = await _decomposer.IngestAsync(input, ct);
    var output = await _recomposer.RecomposeAsync(rootId, RecompositionOptions.RoundTrip, ct);

    // assert: output bytes match input bytes (or match within documented tolerance for lossy modalities)
    output.Should().Equal(input);
}
```

### 6. Document

- `docs/specs/csharp/recomposers.md` — add the row to the inventory with output type, traversal strategy, round-trip fidelity guarantees.

### 7. Run and verify

```pwsh
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}RecomposerTests
pwsh scripts/test/Integration.ps1 -Filter {Pascal}RoundTripTests
```

---

## Anti-patterns (specific to recomposers)

- **DON'T** call any LLM, embedding model, or learned generator. Recomposition is deterministic reconstruction from substrate state.
- **DON'T** modify substrate state. Recomposers read; they don't write.
- **DON'T** branch on rating thresholds in a way that changes output content. Recomposition is deterministic — same substrate state, same output bytes. (Filtering by rating is an inference-time concern; recomposers receive already-filtered inputs.)
- **DON'T** assume a specific traversal strategy. The recomposer receives a root entity ID; how that ID was selected is the caller's concern.
- **DON'T** swallow recomposition failures. If a child entity is missing, throw `RecompositionException` with the specific entity ID that's missing.
- **DON'T** present a single-source phantom-scatter recomposer as Build-a-bear.** The current `SafetensorsRecomposer.AssembleTensorBytesAsync` (lines 239-373) walks `has_constituent` children that are deprecated phantom per-role-unit entities and scatters their stored contours into target row positions — it can only round-trip a model whose phantoms were stored at ingest, with the same shape, from one source. The Build-a-bear product surface (per [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VI) requires synthesis-from-consensus across all ingested models, with arbitrary target architecture spec. The replacement is the per-layer-type synthesizer library at [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md). When adding a new recomposer for safetensors output (or extending `SafetensorsRecomposer`), implement against the synthesizer library, NOT the phantom-scatter path. See AP-28.
- **DON'T** invent weights to cover gaps in attestation density.** Honest abstention: cells with no consensus stay at exact zero. Output is genuinely sparse. Per-tensor coverage statistics go in the safetensors header metadata for downstream evaluation. See spec §VI.3.
- **DON'T** treat fireflies as a recomposition signal source for inference paths.** Fireflies are a derived value-add side-channel for cross-model consensus visualization, NOT the inference mechanism (see AP-29). The recomposer reads attestation edges; fireflies are read by query/visualization tooling separately.

---

## Round-trip fidelity contract

Per modality:

| Modality | Round-trip guarantee |
|---|---|
| Text | Byte-identical (NFC-normalized) |
| Image (lossless formats: PNG, BMP, etc.) | Byte-identical for the pixel payload; container metadata may differ if not ingested |
| Image (lossy formats: JPEG) | Pixel-identical after decode; encoded bytes differ |
| Audio (lossless: WAV, FLAC) | Sample-identical |
| Audio (lossy: MP3, OGG) | Within documented PSNR threshold |
| Video | Frame-identical for ingested frames; container metadata reconstruction is best-effort |
| Safetensors | Tensor-identical; header order may differ if not preserved as substrate metadata |

Document the specific guarantee in `docs/specs/csharp/recomposers.md`.

---

## Verification checklist

- [ ] Recomposer file at `src/Hartonomous.Recomposers/{Pascal}Recomposer.cs`
- [ ] Interface at `src/Hartonomous.Core/Recomposition/I{Pascal}Recomposer.cs`
- [ ] Inherits `BaseRecomposer<TOutput>`
- [ ] No model inference calls
- [ ] No substrate writes
- [ ] DI registration added
- [ ] Unit tests pass
- [ ] Round-trip integration test passes against a fixture
- [ ] Round-trip fidelity guarantee documented

---

## Related recipes

- `08-add-decomposer.md` — paired decomposer for the same modality
- `17-add-test.md` — testing patterns including round-trip
