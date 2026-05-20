# Recipe 20: Add a layer-type decomposer

**When to use this recipe:** You're adding a new universal or specialist layer-type decomposer to the library at [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md). This is the recipe for extending the substrate's tensor-decomposition surface to a new tensor-role family (e.g., adding `MoeRouterLayerDecomposer`, `CrossAttentionLayerDecomposer`, or a specialist for a new architecture's unique tensor type).

**When NOT to use this recipe:**
- For text/image/audio/video content decomposition: use [`docs/recipes/08-add-decomposer.md`](08-add-decomposer.md).
- For per-tensor analysis surfaces (sparsity profile, weight distribution, etc.): use [`docs/recipes/09-add-analysis-pass.md`](09-add-analysis-pass.md).
- For metadata decomposers (config.json, tokenizer_config.json, etc.): create a new metadata decomposer following the same pattern as `ModelConfigDecomposer`.

**Working template:** [`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`](../../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs). This is the canonical reference; every layer-type decomposer follows its shape.

---

## Prerequisites

Before adding a layer-type decomposer:

1. **The tensor role exists in `TensorClassifier` / `TensorRole`.** If your decomposer needs to consume a tensor role that isn't yet recognized by `TensorClassifier`, add the role classification rule first (the dispatch from layer-name pattern → `TensorRole`).
2. **Use the 3 generic attestation types in `sql/schema/seed/attestation_type.sql`** per P1d 2026-05-14 collapse: `positive_evidence` (Glicko score=1.0), `negative_evidence` (score=0.0), `neutral_evidence` (score=0.5). Sign of the tensor signal determines which. Do NOT add modality-specific attestation_type rows (that violates AP-38) — kind-of-evidence metadata goes on `EdgeRatingEvent` attribution fields (`PrimitiveCode`, `TupleCode`, `SlotCode`, `LayerIdx`, `HeadIdx`, `ExpertIdx`, `ModelSourceId`, `TensorHash`, `SourceTensorName`).
3. **The edge type exists in `sql/schema/seed/edge_type.sql`.** Most layer-type decomposers emit the existing `model_attention_pattern`, `model_concept_similarity`, or `model_ffn_factor` edge types between word_form entities. Cross-modal and specialist decomposers may need new edge types — add them via [`recipe 03-add-edge-type.md`](03-add-edge-type.md) first.
4. **Content entities your decomposer binds exist via a content decomposer.** For text-side bindings the `SubstrateTextDecomposer` already produces word_form entities from the model's tokenizer. For vision/audio/video bindings the corresponding content decomposer (`ImageContentDecomposer`, `AudioContentDecomposer`) must be available — those are spec'd as future modality slices.

---

## Steps

### Step 1: Define the decomposer's contract

Document in a header comment for the new pass class:
- What `TensorRole` (or paired roles) it consumes
- What content entities it binds (typically `word_form ↔ word_form` for universal; `word_form ↔ visual_concept` for cross-modal; etc.)
- What `attestation_type` it emits on the rating event
- What `edge_type` it emits the edge as
- What math it performs (the per-role-unit identification)
- What its sparse-recording behavior is (per-tensor adaptive noise floor + top-K filter)

### Step 2: Implement against the `IModelAnalysisPass` interface

```csharp
internal sealed partial class MyLayerTypeDecomposer : IModelAnalysisPass
{
    public string PassId => "model.my_layer_type";
    public IReadOnlyList<string> Dependencies => ["model.tokenizer_mapping"];
    public IReadOnlyList<string> AppliesToArchitectures => []; // empty = all

    private const int TopKPerSide = 32;
    private const double NoiseFraction = 0.10;
    private const double ModelDerivedTrustMu = 60_000.0;
    private const int FlushThreshold = 5_000;

    private readonly ILogger _logger;

    public MyLayerTypeDecomposer(ILogger logger) { _logger = logger; }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        // 1. Resolve content-entity hashes (vocab → word_form bridge via tokenizer)
        Dictionary<int, byte[]>? vocabHashes = TryBuildVocabTokenHashMap(context, session, ct);
        if (vocabHashes is null) { return; }

        // 2. Read the relevant tensors (TensorRole filter)
        TensorHandle? myTensor = null;
        foreach (TensorHandle t in context.Tensors)
        {
            if (t.Classification.Role == TensorRole.MyRole) { myTensor = t; break; }
        }
        if (myTensor is null) { return; }

        double[] tensorData = SafetensorsReader.ReadTensorAsDouble(myTensor.Info);

        // 3. Compute per-role-unit math — what content entities does each unit bind?
        // ... per-row analysis, per-pair scoring, etc.

        // 4. Apply per-tensor adaptive sparse filter
        double noiseFloor = PerRowContentPass.ComputeAdaptiveNoiseFloor(tensorData);
        // ... top-K filter above floor

        // 5. For each surviving (token_a, token_b) pair, emit attestation edge
        foreach ((int tokA, int tokB, double pairStrength) in survivingPairs)
        {
            if (!vocabHashes.TryGetValue(tokA, out byte[]? hashA)) { continue; }
            if (!vocabHashes.TryGetValue(tokB, out byte[]? hashB)) { continue; }

            EntityHandle entityA = new(hashA, "word_form");
            EntityHandle entityB = new(hashB, "word_form");

            double mu = Math.Clamp(1500.0 + (pairStrength / scale) * 200.0, 500.0, 2500.0);

            EdgeSignificanceSpec[] sigSpecs =
            [
                new EdgeSignificanceSpec("model_trust", "model_my_attestation_type", mu),
                new EdgeSignificanceSpec("my_relevant_arena", "model_my_attestation_type", mu),
            ];

            session.Batch.AddEdge(
                "my_edge_type",
                context.ProvenanceCode,
                [
                    new EdgeMemberSpec(entityA, "source", 0),
                    new EdgeMemberSpec(entityB, "target", 1),
                ],
                sigSpecs);

            await session.MaybeFlushAsync(FlushThreshold, ct);
        }
    }

    private static Dictionary<int, byte[]>? TryBuildVocabTokenHashMap(
        ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        // Per TokenAttentionEdgePass.cs:282-329 — read tokenizer.json, route each
        // token's bytes through SubstrateTextDecomposer.EmitStatic to get the
        // existing word_form hash. Same hash regardless of which model's tokenizer
        // surfaced it (content-addressed identity).
        // ...
    }
}
```

### Step 3: Register the decomposer

Add the pass to the orchestrator's `BuildPassSet()` in `SafetensorsDecomposer.cs:208-256`. The orchestrator's topological-sort over `Dependencies` will run it in the right order.

### Step 4: Write a determinism test

Per Law #6: same input + same decomposer version = byte-identical substrate state. The CI determinism test runs the full pass catalogue twice on a fixed small model (MiniLM-L6-v2) in isolated DBs and asserts bitwise-identical entity hash sets and edge hash sets. Your new decomposer participates automatically.

### Step 5: Document in the layer-type library spec

Add a row to the appropriate table (Universal layer decomposers OR Specialist layer decomposers) in [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md):

- Tensor roles consumed
- Content entities bound
- Math performed
- Edge type emitted
- Attestation type on rating event
- Arenas
- Sparsity mechanism

### Step 6: Add the reciprocal synthesizer

Per Substrate Synthesis: every layer-type decomposer needs a reciprocal synthesizer in [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md). Use [`docs/recipes/21-add-layer-type-synthesizer.md`](21-add-layer-type-synthesizer.md).

---

## Anti-patterns specific to layer-type decomposers

- **DON'T** emit phantom per-role-unit entities (`ffn_neuron`, `attention_head`, `embedding_position`, etc., on the spec §XII removal list). The whole point of this recipe is the corrected pattern: emit attestation EDGES between content entities. See AP-25.
- **DON'T** create per-decomposer canonical hashing or per-decomposer content collapse logic. The substrate's content-addressed identity is the collapse mechanism; same content from any decomposer = same hash = one edge.
- **DON'T** write modality-specific code in a layer-type decomposer. Layer-type decomposers are universal across architectures that use the layer type. If you find yourself writing "if vision: do X, if text: do Y," you're conflating layer-type with modality. See AP-26.
- **DON'T** read substrate state during a decomposer pass. Decomposers are producers, not consumers. (Bulk-existence-check via `IIngestionPipeline.GetExisting*Async` is allowed and required per AP-19, but that's pre-emission optimization, not state reading for decomposition logic.)
- **DON'T** emit one rating event per (edge, arena, attestation_type) tuple from a single producer turn — fan out via `ParallelChunkProcessor` for ingestion-bound work. See AP-24.

---

## Verification checklist

- [ ] Pass class is `sealed partial`, in `Hartonomous.Decomposers.Safetensors.Passes` namespace
- [ ] `PassId` follows `model.{name}` convention
- [ ] `Dependencies` lists actual dependencies only (typically `model.tokenizer_mapping`)
- [ ] Pass identifies content entities (typically `word_form` entities) via `SubstrateTextDecomposer.EmitStatic` on the model's tokenizer bytes
- [ ] Per-tensor adaptive noise floor applied before emission
- [ ] Edge emission via `session.Batch.AddEdge` with `EdgeMemberSpec` array (role-ordered participants)
- [ ] `EdgeSignificanceSpec` array per emitted edge with appropriate `attestation_type` per arena
- [ ] Initial Glicko mu derived from tensor math (not hardcoded)
- [ ] No phantom entity types emitted (verify against entity_type.sql phantom list)
- [ ] Pass registered in `SafetensorsDecomposer.BuildPassSet()`
- [ ] Determinism test passes (byte-identical output across two runs on same input)
- [ ] Row added to [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md)
- [ ] Reciprocal synthesizer added per recipe 21

---

## Cross-references

- [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §III, §V (canonical architecture)
- [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md) (the library spec)
- [`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`](../../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs) (working template)
- [`.claude/rules/45-anti-patterns.md`](../../.claude/rules/45-anti-patterns.md) AP-25, AP-26, AP-27
