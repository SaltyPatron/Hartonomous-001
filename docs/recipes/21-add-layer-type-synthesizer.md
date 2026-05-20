# Recipe 21: Add a layer-type synthesizer

**When to use this recipe:** You're adding the reciprocal synthesizer for a layer-type decomposer. Per Substrate Synthesis (spec §VI), every layer-type decomposer needs a reciprocal synthesizer that, given a target tensor's role and shape, projects substrate consensus attestations into the target tensor's basis.

**Prerequisite:** The reciprocal layer-type decomposer exists (per [`docs/recipes/20-add-layer-type-decomposer.md`](20-add-layer-type-decomposer.md)) and is documented in [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md). The synthesizer reads the substrate state that the decomposer emitted.

---

## Steps

### Step 1: Define the synthesizer's contract

For your new synthesizer, document:
- What `TargetTensorSpec.TensorRole` it handles
- What attestation_type / edge_type it queries from substrate
- What synthesis algorithm it uses (low-rank approximation, KV-memory inversion, PCA, etc. — published research; cite the paper)
- What target tensor shape it produces
- How it handles honest abstention (which cells stay zero)

### Step 2: Implement against the `ILayerTypeSynthesizer` interface

```csharp
public interface ILayerTypeSynthesizer
{
    bool Handles(TensorRole targetRole);

    Task<byte[]> SynthesizeAsync(
        TargetTensorSpec target,
        SubstrateAttestationQuery query,
        RecompositionOptions options,
        CancellationToken ct);
}

internal sealed class MyLayerTypeSynthesizer : ILayerTypeSynthesizer
{
    public bool Handles(TensorRole targetRole) =>
        targetRole == TensorRole.MyRole;

    public async Task<byte[]> SynthesizeAsync(
        TargetTensorSpec target,
        SubstrateAttestationQuery query,
        RecompositionOptions options,
        CancellationToken ct)
    {
        // 1. Query substrate consensus for the matching attestation_type on the matching edge type
        // Filtered by arena weighting, significance threshold, source filter, layer/head metadata
        IReadOnlyList<AttestationRecord> attestations = await query.QueryAttestationsAsync(
            edgeType: "my_edge_type",
            attestationType: "model_my_attestation_type",
            arenaCodes: options.ArenaCodes,
            minMu: options.SignificanceThreshold,
            sourceFilter: options.SourceFilter,
            layerIndex: target.LayerIndex,
            headIndex: target.HeadIndex,
            ct);

        // 2. Build the sparse attestation matrix S (or whatever shape your synthesis math takes)
        // Typically: S[token_a_index][token_b_index] = consensus_mu

        // 3. Run the synthesis algorithm
        // ... low-rank approximation, KV-memory inversion, PCA, etc.

        // 4. Apply honest abstention: cells with no attestation evidence stay at zero
        // No fabrication.

        // 5. Pack to wire format per target dtype
        return PackToWire(synthesizedWeights, target.Dtype, target.Shape);
    }
}
```

### Step 3: Register the synthesizer

Add to the recomposer's synthesizer registry (the dispatch table that maps `TensorRole → ILayerTypeSynthesizer`).

### Step 4: Document in the synthesis library spec

Add a row to the appropriate table (Universal layer synthesizers OR Specialist layer synthesizers) in [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md):
- Reciprocal of (which layer-type decomposer)
- Target tensor roles
- Substrate query
- Synthesis math (with paper citation)
- Output shape
- Honest abstention behavior

### Step 5: Write a coverage-and-correctness test

Two tests:
1. **Round-trip on a single source:** ingest model M alone, synthesize the same architecture from the substrate, verify output bytes are dense-substrate-faithful (sparse cells stay zero, attested cells reproduce the consensus).
2. **Multi-source corroboration improves output:** ingest models M1, M2, M3 with shared content; synthesize one architecture from the combined consensus; verify per-tensor coverage statistics show higher density and tighter sigma than single-source synthesis.

---

## Anti-patterns specific to layer-type synthesizers

- **DON'T** read phantom per-role-unit entities. Synthesizers read attestation edges between content entities; phantom paths are deprecated. See AP-28.
- **DON'T** invent weights to cover gaps. Honest abstention: under-attested cells stay at exact zero. Output is genuinely sparse. The recomposer's job is to project consensus, not fabricate. See AP-29.
- **DON'T** round-trip from one source as the default surface. The default is multi-source consensus across all ingested models filtered by `RecompositionOptions.SourceFilter`. Single-source filter is allowed but is the trivial case.
- **DON'T** modify substrate state from a synthesizer. Synthesizers read; they don't write. (Audit-trail metadata in safetensors header is the only synthesizer output beyond the tensor bytes.)
- **DON'T** use approximation methods that compromise the substrate read. Approximation in the synthesis math itself (iterative SVD, sampling) is permitted per spec §XI.2; corrupting what you read from substrate is not.
- **DON'T** output non-standard formats. Output is always loadable safetensors; audit metadata in the JSON header is the only proprietary information.

---

## Verification checklist

- [ ] Synthesizer class implements `ILayerTypeSynthesizer`
- [ ] `Handles(TensorRole)` correctly identifies which target roles this synthesizer claims
- [ ] Substrate query uses correct `attestation_type` and `edge_type` matching the reciprocal decomposer
- [ ] Honest abstention: under-attested cells stay at zero (verified by test)
- [ ] Synthesis algorithm cites published research (paper / DOI in code header comment)
- [ ] Output dtype packing handles the full dtype matrix (F32, F16, BF16, F8_E4M3, F8_E5M2)
- [ ] Per-tensor coverage statistics emitted to safetensors header metadata
- [ ] Single-source round-trip test passes
- [ ] Multi-source corroboration test verifies improvement
- [ ] Row added to [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md)

---

## Cross-references

- [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VI (canonical recomposer architecture)
- [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md) (the library spec)
- [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md) (the reciprocal decomposers)
- [`docs/recipes/20-add-layer-type-decomposer.md`](20-add-layer-type-decomposer.md) (the reciprocal recipe)
- [`.claude/rules/45-anti-patterns.md`](../../.claude/rules/45-anti-patterns.md) AP-5, AP-28, AP-29
