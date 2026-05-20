# V1 Demo — Custom Model Construction From Universal Substrate

This file is the deliverable for Phase 6 of the V1 plan
(`C:\Users\ahart\.claude\plans\dapper-whistling-grove.md`). It records the
end-to-end demonstration: seed corpora ingested, AI models ingested,
substrate queried, three example custom models recomposed and verified.

---

## Status — actual, not aspirational

Pre-v1 the substrate is bootstrap-only: `Drop → Create → Bootstrap` apply
path, no incremental migrations, `sql/schema/` is the single source of truth.
Active deployment pipeline lives in `RunAll.bat` and ends at
`scripts/db/Bootstrap.ps1`.

### Phase 0 — Verified baseline
**Done.** See `BASELINE.md` for last captured row counts. Baseline now applies
via bootstrap, not migrate.

### Phase 1 — Schema additions
**Done in canonical schema/ form:**
- `physicality_type 'embedding_firefly'` registered
  (`sql/schema/seed/physicality_type_embedding_firefly.sql`).
- Arena machinery installed (`substrate.create_arena`,
  `substrate.create_model_trust_arena` —
  `sql/schema/functions/create_arena.sql`,
  `sql/schema/functions/create_model_trust_arena.sql`).
- `sql/tests/schema_completeness_tests.sql` covers the canonical surface
  (schemas, extensions, domains, reference / core / junction / staging /
  monitor / model tables, substrate functions, monitor procedures,
  arena round-trip).

The original V1 plan called for additional new entity_types and edge_types
for fireflies / model roots / firefly_consensus / etc. Per the audit corrections
memory, fireflies are *physicalities* of `bpe_token` entities — not separate
entity_types. The scope was trimmed to the load-bearing additions only.

### Phase 2 — Decomposer rewrite
**Not started.** None of the planned files exist:
- `Architectures/{IArchitectureHandler, ArchitectureHandlerRegistry,
  DecoderOnlyHandler, MoeHandler, VisionTransformerHandler,
  VisionLanguageHandler, AudioEncoderHandler, AudioLanguageHandler,
  DiffusionHandler, EmbeddingRerankerHandler, LoraAdapterHandler}.cs`
- `Passes/{ModelRootPass, ArchitectureEdgesPass, TokenizerCompositionPass,
  QuantizationVariantPass, LoraAdapterPass, FireflyConsensusPass,
  MultiComponentPass, NativeEmbeddingPass}.cs`
- The existing `EmbeddingFireflyPass` was not rewritten for multi-tier
  emission.
- `QuantizationDecode.cs` not added.

### Phase 3 — Universal substrate query surface
**Done.** Eight functions installed canonically:
`model_inventory`, `model_vocab_recovered`, `cross_model_consensus`,
`cross_model_divergence`, `preview_target_arch`, `refinement_summary`,
`tensor_provenance_chain`, `recompose_audit_walk`.

Layer / head / expert counts in `model_inventory` are deferred until the
decomposer populates `tensor_position_index` (Phase 2 work).

### Phase 4 — Recomposer rewrite
**Partial.** Done:
- `RecompositionOptions` rewritten as full recipe DSL
  (`Mode`, `RefinementPolicy`, `QuantizationPolicy`, `LoraPolicy`,
  `MaxShardBytes`, `ProvenanceFilter`, `ArenaCodes`,
  `SignificanceThreshold`, `NoiseFloor`, `TargetArchSpecJson`,
  `VocabSubsetTokenHashes`, `HardwareProfileJson`, `RecipeId`).
- `SafetensorsWriter` extended with `__metadata__` audit-chain support
  (recipe id, recomposer version, mode, policies, arena codes, thresholds).
- `SafetensorsRecomposer` extended with `RecomposeFilteredAsync`,
  `RecomposeToShardsAsync`, audit-metadata builder.
- `ShardSplitter.cs` (5 GB shards, layer-coherent grouping,
  `model.safetensors.index.json` emit).
- `RecipeContentHash.cs` (BLAKE3 of canonical recipe JSON).

Not done:
- `PerRoleProjection.cs`
- `QuantizationConvert.cs`
- `LoraExport.cs`
- `MultiComponentRecompose.cs`
- `SubstrateStateMerkle.cs`

The recomposer therefore handles: filtered/sharded export with audit
metadata, recipe-driven options, noise-floor enforcement, lossless wire
encoding for F64/F32/F16/BF16/I8/U8/I16/I32/I64/BOOL. It does **not**
yet perform: cross-source per-role projection at differing target
dimensionality, requantization-target encoding (FP8/AWQ/GPTQ/MXFP4),
LoRA merge or separate-adapter export, multi-component (diffusion /
vision-language / audio-language) directory output, substrate-state
Merkle root in audit metadata.

### Phase 5 — Integration tests
**Scaffolded, not exhaustive.** Files present:
- `tests/Hartonomous.Integration.Tests/Fixtures/RoundTripFixture.cs`
- `tests/Hartonomous.Integration.Tests/Fixtures/ModelLoadabilityHarness.cs`
- `tests/Hartonomous.Integration.Tests/VerticalSlice/SafetensorsRoundTripTests.cs`
- `tests/Hartonomous.Integration.Tests/VerticalSlice/MixAndMatchTests.cs`
- `tests/Hartonomous.Integration.Tests/VerticalSlice/CrossSourceCorroborationTests.cs`

Not present:
- `RecomposeCorrectnessTests.cs`
- `RefinementIntelligenceTests.cs`

### Phase 6 — End-to-end demonstration
**Blocked.** Cannot run without Phase 2 (decomposer architecture handlers
+ passes) and the missing Phase 4 pieces. Three recipes are committed in
`examples/recipes/` for when the rest lands:
`refined-qwen-consensus.json`, `custom-13b-moe-8-experts.json`,
`fp8-quantized-qwen.json`.

---

## Runbook (when Phases 2 + 4 land)

### Step 1 — Apply canonical substrate

```pwsh
.\scripts\Docker\Down.ps1 -RemoveVolumes -Force
.\scripts\Docker\Build.ps1
.\scripts\Docker\Up.ps1 -Rebuild
.\scripts\build\Dotnet.ps1
.\scripts\db\Drop.ps1 -Force
.\scripts\db\Create.ps1
.\scripts\db\Bootstrap.ps1
.\scripts\test\Brain.ps1                 # includes schema_completeness_tests.sql
```

`RunAll.bat` chains the above plus the seed corpora.

### Step 2 — Seed corpora

```pwsh
.\scripts\seed\Ucd.ps1         -SourceRoot D:\Models
.\scripts\seed\Iso639.ps1      -SourceRoot D:\Models
.\scripts\seed\WordNetOmw.ps1  -SourceRoot D:\Models
.\scripts\seed\UniversalDeps.ps1 -SourceRoot D:\Models
.\scripts\seed\Wiktionary.ps1  -SourceRoot D:\Models
.\scripts\seed\Tatoeba.ps1     -SourceRoot D:\Models
```

### Step 3 — Ingest at least one decoder-only AI model

```pwsh
.\scripts\seed\Safetensors.ps1 -SourceRoot D:\Models -ModelFilter "Qwen/Qwen2.5-0.5B-Instruct"
```

### Step 4 — Find the model's architecture hash

```pwsh
psql -h localhost -p 5433 -U hartonomous -d hartonomous -c "
SELECT encode(em_src.entity_hash, 'hex') AS arch_hash, count(*) AS tensor_count
  FROM substrate.edge_member em_src
  JOIN substrate.edge_type et ON et.id = em_src.edge_type_id AND et.code = 'has_tensor'
  JOIN substrate.edge_role er ON er.id = em_src.edge_role_id  AND er.code = 'source'
 GROUP BY em_src.entity_hash
 ORDER BY tensor_count DESC;
"
```

### Step 5 — Query substrate inventory

```pwsh
dotnet run --project src/Hartonomous.Cli -- query-substrate `
    --arch-hash <ARCH_HASH_HEX>
```

### Step 6 — Recompose (when Phase 4 completes)

```pwsh
dotnet run --project src/Hartonomous.Cli -- export-model `
    --arch-hash <ARCH_HASH_HEX> `
    --recipe examples/recipes/refined-qwen-consensus.json `
    --shard `
    --output D:\exports\refined-qwen-consensus
```

### Step 7 — Verify (loadability, audit chain, reproducibility)

```pwsh
python -c "from transformers import AutoModelForCausalLM; AutoModelForCausalLM.from_pretrained('D:\\exports\\refined-qwen-consensus'); print('OK')"

dotnet run --project src/Hartonomous.Cli -- audit-walk `
    --output-dir D:\exports\refined-qwen-consensus
```

---

## What this proves (when complete)

1. The substrate ingested seed corpora + at least one AI model into one
   universal frame.
2. Token entities collapsed across sources.
3. Cross-source attestations accumulated on shared edges with arena Glicko
   priming open-vocabulary across all registered arenas.
4. The query surface answered every architectural question.
5. Three example custom models — refined-source, novel-arch Laplace original,
   quantization-converted — were produced from the substrate, are byte-
   identical on re-run, and load in conventional inference stacks.

That is the V1 product. Today the schema and query surface are real and
canonical; the decomposer rewrite (Phase 2) and the projection / quantization
/ LoRA / multi-component recomposer pieces (Phase 4) are the remaining work.
