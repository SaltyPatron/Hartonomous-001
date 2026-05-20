# Hartonomous Substrate — Consolidated Build Plan (post 2026-05-19 architectural correction)

## Context

This is the consolidation of the prior `modular-wishing-koala` Gate 1-6 plan with the architectural corrections forced during the 2026-05-19 ultrathink session, the unimplemented spec §VII / §IX.4 synthesizer collapse from `docs/01-tensor-primitive-spec.md`, and the post-Gate-1 substrate refactor that landed this session. Prior gates are subsumed into stages keyed by what is actually load-bearing for the invention.

The substrate-as-design is intact. The substrate-as-running-software is now substantially functional on the corpus seed side (Unicode + ISO 639 + WordNet + OMW + UD complete + Wiktionary ~half complete in last RunAll). The load-bearing gap for production model export is the **synthesizer collapse** — the synthesizer side of the AP-30 standardization is unimplemented; the current synth output uses legacy bespoke spectral methods (Lanczos eigenmap + Ritz lift on a derived adjacency matrix) that pattern-match conventional ML training rather than capitalizing on the substrate's content-addressed pre-computed structure. The decomposer side has 5 of 9 primitive+tuple passes per spec; the synthesizer side has 0 of 7. Closing this gap is what makes substrate-derived model export defensible and the invention complete.

## Architectural ground truth (forced by 2026-05-19 session corrections)

1. **Universal Glicko surface (AP-8).** Corpus attestations (WordNet hypernym, Wiktionary translation_of, UD has_pos, OMW aligned_to_synset, etc.) and AI-model attestations (model_attention_pattern, model_ffn_factor, model_concept_similarity, model_cross_modal_pattern) compete on the SAME `substrate.edge_significance` rows. (provenance × arena) discriminates source + domain. Synthesizers MUST treat any attestation as substrate-canonical regardless of whether the source was a curated lexicon, a corpus observation, an AI model, or a user prompt.

2. **Bit-perfect for content trajectories, not for AI model weights.** Canonical text decomposition is reversible byte-identical via mantissa-packed `LINESTRINGZM` tier walk. Moby Dick in → Moby Dick out. Same property extends to audio/video/image once their content decomposers land (audio LINESTRINGZM with sample atoms, image POLYGONZM/MULTIPOINTZM with pixel atoms, video MULTILINESTRINGZM with frame trajectories). PostGIS spatial datatypes ARE the universal archival layer with 212 bits of exact-integer mantissa payload per vertex. Bit-perfect does NOT apply to AI model weights — Build-a-bear synthesizes NEW outputs from accumulated multi-source consensus, NOT byte-archived weight files.

3. **Substrate is the brain, corpus is developmental input.** Each ingested sentence is a synaptic firing event — Glicko-2 update on participant edges. The text_composition entity IS the neuron that fired; content-addressing makes future repetitions of the same content fire the same neuron with cross-source corroboration tightening sigma. There is no separate training phase. Accumulation IS training.

4. **Native ~220-dim signal per token from corpus alone** — recursive Merkle centroid (4 dim from substrate.entity.centroid_x/y/z/m) + per-arena entity_significance mu (19 dim) + per-edge-type participation count (134 dim) + per-provenance entity_significance (63 dim). Substrate's natural high-dimensional signal fills any reasonable hidden_dim without AI model ingestion. Models ADD more dimensions (per-model fireflies, model_* attestation edges) but corpus is sufficient.

5. **Custom model export by user-configured recipe — substrate state × recipe shape → safetensors.** Recipe drives shape (vocab_size, hidden_dim, num_hidden_layers, num_attention_heads, head_dim, intermediate_size, MoE config with per-expert arena weights, LoRA rank + target_modules, RoPE/ALiBi, activation, norm_type, tie_word_embeddings, output dtype, per-layer arena assignment, provenance weights). Substrate state is content; synthesizer projects substrate facts into recipe-defined tensor shape via 4 PrimitiveKind math. Custom architectures (MoE with arbitrary expert layout, LoRA delta over arbitrary base, MiniLM-as-MoE-with-Flux, practitioner-designed novel shapes) all populate via the same canonical projection.

6. **AP-30 / AP-38 collapse principle on both sides** — small canonical vocabulary (4 primitives + ~13 tuples), per-architecture data tables not code. Decomposer side: 4 PrimitivePasses + 5 TuplePasses + per-architecture TupleResolver tables. Synthesizer side: 4 PrimitiveSynthesizers + 3 TupleSynthesizers + same TupleResolver tables. Per-dimension edge_type proliferation banned (drove the codepoint properties collapse from 9 `has_cp_*` types to 1 polymorphic `has_classification` discriminated by target entity_type + (provenance × arena)).

7. **Modality symmetry — every modality has its own archival tier-walk.** Audio: `audio_recording` → `audio_chunk` → sample atom with POINTZM physicality; LINESTRINGZM through chunks carrying (time, sample_value, channel, metadata) per vertex. Image: `pixel_region` → pixel atom; POLYGONZM/MULTIPOINTZM. Video: `video_frame` → `pixel_region` → pixel; MULTILINESTRINGZM with frame trajectories. Same bit-perfect tier-walk recompose property as text. Modalities are NOT deferred-as-second-class — they're peer architectures sharing the universal mantissa-packed LINESTRINGZM shape.

8. **Negative-evidence learning via SignedEventsFor** — hate datasets / safety datasets fire score=0 weight=|magnitude| Glicko events on conversational pattern edges in arenas like `safety_alignment` / `conversational_quality` / `register_appropriateness`. Same primitives as positive learning, sign flipped. The familiar's edge mu drops on those patterns; at inference, A* edge cost `1/mu` is high → traversal naturally routes around → output composition steers away. No hardcoded refusal layer; no filter; just Glicko ladder dynamics on substrate's existing surface.

9. **Native perf via PG C extension + MKL/Eigen/Spectra dispatch.** The bottleneck on the synth path is the plpgsql self-join over `substrate.edge_member`, NOT the C# Lanczos (already MKL-backed via `SparseSymEigs.F64` → `NativeCompute`). Real performance requires substrate-side C functions with AVX2/AVX-VNNI hash matching + Eigen sparse construction. The proper path: `substrate.build_synth_adjacency_csr(vocab, arenas) RETURNS bytea` as a binary blob computed in `ext/hartonomous_pg/src/pg_synth_adjacency.c`.

10. **Synthesizer collapse spec §VII unimplemented** — decomposer side has 5/9 primitive+tuple passes done (`AttentionBlockTuplePass`, `EmbeddingLookupTuplePass`, `FfnTuplePass`, `LoraDeltaTuplePass`, `NormalizationPrimitivePass`); synthesizer side has 0/7 done plus 6 legacy bespoke synthesizers (AttentionSynthesizer, EmbeddingSynthesizer, FfnSynthesizer, FfnEdgeSlotSynthesizer, PositionEmbeddingSynthesizer, LayerNormSynthesizer) that need to retire per spec §IX.4. **This is the load-bearing gap for production-quality model output.**

## Architectural Principles (carried from prior plan, all still valid)

0. **No compatibility shims, no fallbacks, no migration paths, no transitional adapters.** No production users. Current state is broken in places. Delete wrong code outright; write right code. No `[Obsolete]`, no fallback adapters, no "preserve for legacy callers." Every gate must be rock-solid for the invention to work.

1. **Blob and substrate are siblings.** Both derived from source corpus files. Blob = build-time client-side perf cache (UAX29 segmentation, S³ centroid lookup). Substrate = runtime decomposer-emit. They MATCH because they're derived from same source content. Neither populates the other.

2. **One decomposer per source corpus.** UnicodeDecomposer reads full UCD source-file set. WordNetDecomposer reads WordNet data files. One producer surface per corpus, not one pass per file.

3. **One parser per source format.** Each format has ONE parser. Build mode emits blob; runtime mode emits substrate. Same parse logic; two output sinks.

4. **Bundled emit.** Producer's natural unit is `RecordBundle`. Bundles ship as atomic units; members commit in same transaction. Zero FK races by construction.

5. **Partitioned parallel producers.** N = `Environment.ProcessorCount / 2` clamped [4, 16]. Workers partitioned by hash-prefix; disjoint; never contend.

6. **Bulk existence probe per chunk (AP-19).** One round-trip per kind via `GetExisting*Async`. Emit only the diff.

7. **Zero post-pass / zero backfill (AP-37).** Edge geometry inline at INSERT-SELECT from participant centroids. Significance priors inline. No `populate_edge_trajectories`, no `prime_unprimed_edges_chunk`, no `populate_*_from_ext`, no entity-centroid trigger.

8. **Schema install separated from C-extension install.** `.so` provides C-binding declarations only. `sql/schema/bootstrap.sql` applied via psql in user-mode no-sudo path.

9. **Multi-modality is FIRST-CLASS, not deferred.** Per architectural correction #7: audio/video/image decomposers land in their own stage (Stage 7). The universal mantissa-packed shape is the same across modalities; deferring them is what makes the substrate look "text-only" when it's universal-by-design.

## Naming: Substrate Synthesis (renamed from "Build-a-bear")

`build-a-bear` (registered trademark of Build-A-Bear Workshop, Inc.) → **Substrate Synthesis** (noun product) / **synthesize** (verb operation). CLI command already `synthesize-model`. Crystal Ball naming decision deferred to practitioner — proposed Substrate Lens / Crystal / Substrate Atlas.

---

## Stage 1: Substrate foundation completion

**Status: ~85% complete after 2026-05-19 session.**

### What landed this session
- ✅ #34 PG hash-partition `substrate.entity` / `substrate.edge_member` / `substrate.physicality` by `partition_bucket = get_byte(hash, 0) & 7`. 8 child partitions each + 48 physicality sub-leaves (6 modality × 8 hash).
- ✅ #36 ContentRecomposer thin wrapper + BulkTierContentWalk (597 lines in Core) — N+1 bulk PG queries per tier, mantissa unpack, P/Invoke `hartonomous_ucd_cp_from_hash`, UTF-8 reassembly. Replaces deleted PG-side recompose_text / recompose_content / pg_recompose_walk paths.
- ✅ #37 Text decomposer correctness audit — `SubstrateTextDecomposer.EmitStatic` emits content-addressed entities only; no POS/sense/language pollution. Lock-in test landed.
- ✅ #38 Codepoint properties refactor — wide flat `substrate.codepoint_property` (25 col, 7 indexes, 9 FKs) DELETED. Replaced by 9 narrow per-property junctions (`cp_general_category`, `cp_script`, `cp_block`, `cp_bidi_class`, `cp_east_asian_width`, `cp_grapheme_break`, `cp_word_break`, `cp_sentence_break`, `cp_line_break`) + generic polymorphic `has_classification` edge type (AP-30 collapse — replaces 9 per-dimension `has_cp_*` rows). EdgeArenaRouter `EventsFor(edgeType, targetEntityType)` overload routes per (edge × target_type). 6 new entity_type rows (general_category / script / block / bidi_class / east_asian_width / break_property) as content-hashed reference-vocab. 6 new ReferenceVocabularyHashes helpers.
- ✅ #39 EdgeArenaRouter orphan mappings — covers all 142 → now 134 edge_type seed codes.
- ✅ #41 Bcp47/Iso15924/AsciiEncoding/Iso88591Encoding registered in PhasesCommand.
- ✅ #32/#33/#35 AP-19/AP-8/EventsFor coverage across WordNet/OMW/UD/Wiktionary/Tatoeba/Iso639/Iso15924/Bcp47/EncodingDecomposerBase. CrossLinkAttestation `has_language` 5-arg fix.
- ✅ Wiktionary native -10 rejection handling — probe + 6 EmitOrRefWordForm caller sites + EmitRelations + Hash32.IsZero helper. Pathological 1-byte inputs (single-codepoint punctuation rejected by `hartonomous_text_decompose`) skip cleanly as honest abstention.
- ✅ §3 `EmitCodepointAtomsAsync` extended — emits `has_classification(codepoint, class_entity)` typed edges for all 9 UCD property dimensions + populates narrow cp_* junctions inline. Reference-vocab id↔code dictionaries loaded once at section start. Chunk-scoped HashSet dedup for reference-vocab entity AddEntity calls.
- ✅ Atom POINTZM physicality emission — §3 `FlushCodepointAtomsAsync` calls `batch.AddPhysicalityPoint4d(handle, "entity", x, y, z, m)` alongside the 7-arg AddEntity. 1.1M codepoint POINTZMs land per UcdUca phase.

### Remaining for Stage 1 close
- 🔜 Re-run RunAll end-to-end after Wiktionary -10 fix; verify Wiktionary phase completes (~2 hours for 3 GB) + Tatoeba + Bcp47 + Iso15924 + Encoding phases.
- 🔜 #40 verification queries: cross-source POS attestation on word_form "rake" returns ≥3 provenances; Glicko games > 0 on `lexical_disambiguation` + `semantic_relevance` + `syntactic_role_fitness` + `translation_quality` + `morphological_productivity` + `unicode_version_consensus`; `substrate.physicality` count = 1.1M+ codepoint POINTZMs + per-content trajectories from corpus; no `has_pos` edges on codepoint entities; new narrow `cp_*` junctions populated (~33 + 163 + 168 + 26 + 6 + 33 per-property reference rows backing 1.1M × 9 cp_* rows).
- 🔜 Perf gate: Wiktionary ingest < 2 hours real time; Moby Dick recompose < 1 second via BulkTierContentWalk.

### Acceptance
- RunAll completes through Tatoeba without phase failures
- Verification SQL queries above all pass
- All decomposer projects build clean with 0 warnings / 0 errors
- No `[Obsolete]` markers in modified code; no fallback paths added

---

## Stage 2: Decomposer-side AP-30 finish (4 of 9 passes + 4 architecture profiles remaining)

**Status: 5/9 primitive+tuple passes complete + 6/10 architecture profiles complete.**

### Existing (already landed)
- `AttentionBlockTuplePass.cs`, `EmbeddingLookupTuplePass.cs`, `FfnTuplePass.cs`, `LoraDeltaTuplePass.cs` (4 tuple passes)
- `NormalizationPrimitivePass.cs` (1 primitive pass)
- TupleResolution profiles: `BertArchitectureProfile`, `LlamaArchitectureProfile`, `Qwen3MoeArchitectureProfile`, `DaVitArchitectureProfile`, `FluxVaeArchitectureProfile`, `PeftLoraArchitectureProfile` (6)

### Missing
- 🔜 **LinearProjectionPass** — primitive pass emitting per-tensor signature for Linear primitives (Q/K/V/O, gate/up/down, lm_head, bbox_proj, class_proj, LoRA A/B). Per spec §VI.
- 🔜 **LocalKernelPass** — primitive pass for conv tensor signatures.
- 🔜 **LookupPass** — primitive pass for table-lookup tensors (embedding tables, position embeddings, codebook entries, RoPE freq, ALiBi). Per-row firefly POINTZM physicality on the looked-up content entity.
- 🔜 **CrossAttentionTuplePass** — tuple pass for cross-modal attention (Florence-2 vision↔text, BART decoder-encoder, CLIP cross-modal). Emits `model_cross_modal_pattern` between entity_type pairs.
- 🔜 **SpatialKernelTuplePass** — tuple pass for ConvResidualBlock + ConformerBlock conv_module. Emits `model_spatial_pattern` between pixel_region/audio_chunk neighbors.
- 🔜 **BART architecture profile** (encoder-decoder).
- 🔜 **Conformer architecture profile** (canary-qwen perception, NeMo).
- 🔜 **Swin architecture profile** (Grounding-DINO backbone).
- 🔜 **DETR architecture profile** (Conditional-DETR, RT-DETR, Deformable-DETR).

### Critical files
- New: `src/Hartonomous.Decomposers/Safetensors/Passes/{LinearProjectionPass,LocalKernelPass,LookupPass,CrossAttentionTuplePass,SpatialKernelTuplePass}.cs`
- New: `src/Hartonomous.Decomposers/Safetensors/TupleResolution/{BartArchitectureProfile,ConformerArchitectureProfile,SwinArchitectureProfile,DetrArchitectureProfile}.cs`
- Edit: `src/Hartonomous.Decomposers/Safetensors/Passes/ModelPassOrchestrator.cs` (register new passes)

### Acceptance
- Ingest sample BART model (Florence-2 LM head), Conformer model (canary-qwen perception), Swin model (Grounding-DINO backbone), DETR model (Conditional-DETR) without errors
- Each model's tensors classified through the new architecture profile + dispatched to correct primitive+tuple passes
- Per AP-31 sign-bearing events fire on emitted attestation edges with appropriate per-(primitive × tuple × slot × layer × head/expert) attribution

---

## Stage 3: Substrate query surface for synth (load-bearing prereq for Stage 4)

**Why this is prereq for Stage 4:** the PrimitiveSynthesizers per spec §VII read substrate.edge_significance + substrate.entity.centroid_* + substrate.physicality firefly clouds + per-arena attestation profiles. Current `IEntityReader` + `IPhysicalityReader` don't expose these surfaces in a bulk-fetch form suitable for synth.

### Deliverables
- 🔜 Extend `IEntityReader` with:
  - `GetCentroidsAsync(IReadOnlyList<EntityHandle> entities, CancellationToken ct)` → `IReadOnlyDictionary<EntityHandle, (double X, double Y, double Z, double M, long HilbertIndex)>`. Single bulk SELECT against substrate.entity.centroid_*.
  - `GetEntitySignificanceProfileAsync(IReadOnlyList<EntityHandle> entities, IReadOnlyList<string> arenas, CancellationToken ct)` → per-(entity, arena) mu+sigma+games tuple. Single bulk JOIN.
  - `GetEdgeSignificanceMatrixAsync(IReadOnlyList<EntityHandle> sources, IReadOnlyList<EntityHandle> targets, IReadOnlyList<string> arenas, string edgeTypeCode, CancellationToken ct)` → sparse (source, target, arena) → mu/games tuple. Backing for attention/FFN cell synthesis.
  - `GetProvenanceAttestationProfileAsync(IReadOnlyList<EntityHandle> entities, CancellationToken ct)` → per-(entity, provenance) accumulated significance signal.
  - `GetEdgeTypeParticipationAsync(IReadOnlyList<EntityHandle> entities, CancellationToken ct)` → per-(entity, edge_type) participation count. Enables the 134-dim natural signal.
- 🔜 Extend `IPhysicalityReader` with:
  - `GetFireflyCloudAsync(IReadOnlyList<EntityHandle> entities, CancellationToken ct)` → per-entity list of POINTZM positions across all ingested models (per `entity_model_source` provenance). For Mode 1 single-model synth, filter by source_model_id.
- 🔜 **Native PG function** `substrate.build_synth_adjacency_csr(vocab bytea[], arenas text[], include_indirect bool) RETURNS bytea` in `ext/hartonomous_pg/src/pg_synth_adjacency.c`. Computes the vocab × vocab × arena sparse CSR tensor entirely server-side via:
  - AVX2/AVX-VNNI hash matching for the vocab × edge_member self-join
  - Eigen sparse construction for per-arena CSR materialization
  - Returns single binary blob (CSR header + per-arena (RowPtr, ColIdx, Values) arrays)
  - Zero plpgsql; single round-trip; native perf via the same pattern as existing `hartonomous_glicko2_bulk_update` C function
- 🔜 Hash-partition parallel scan via `Task.WhenAll` over 8 buckets (`hash_bits_0_51 & 7`) for any C#-side bulk fetches that touch substrate.entity / substrate.edge_member. Real intra-PG concurrency.
- 🔜 Real progress logging via `IProgress<string>` threaded through `SynthesisContext`. Every PG round-trip + every per-tensor synth step reports timing + rowcount + coverage.

### Critical files
- `src/Hartonomous.Core/Data/IEntityReader.cs` (extend)
- `src/Hartonomous.Core/Data/IPhysicalityReader.cs` (extend)
- `src/Hartonomous.Engine/Data/NpgsqlEntityReader.cs` (implement new surface)
- `src/Hartonomous.Engine/Data/NpgsqlPhysicalityReader.cs` (implement)
- New: `ext/hartonomous_pg/src/pg_synth_adjacency.c`
- New: `ext/hartonomous_pg/sql/functions/build_synth_adjacency_csr.sql` (CREATE FUNCTION binding)
- Update: `ext/hartonomous_pg/hartonomous.control` (no new requires)
- Update: `sql/schema/bootstrap.sql` (include build_synth_adjacency_csr function)
- `src/Hartonomous.Core/Recomposition/SynthesisContext.cs` (add `IProgress<string>? Progress`)

### Acceptance
- Native `substrate.build_synth_adjacency_csr` returns a per-arena CSR blob for v=30K vocab in < 60s (8× speedup over current ~675s plpgsql self-join + collapse)
- C# call sites use `IEntityReader.GetEdgeSignificanceMatrixAsync` etc. instead of constructing inline SQL
- Real progress logging surfaces per-stage timing in the CLI output

---

## Stage 4: Synthesizer collapse — spec §VII / §IX.4 (THE LOAD-BEARING WORK)

**Status: 0 of 7 synthesizers complete. 6 legacy bespoke synthesizers occupy the namespace.**

### Why this is the load-bearing work
The current synth output uses spectral methods (Lanczos eigenmap on derived adjacency, Ritz vector lift) that pattern-match conventional ML training rather than projecting substrate-canonical attestation density into recipe-specified tensor shape. Per spec §VII collapse principle, 4 PrimitiveSynthesizers + 3 TupleSynthesizers replace the entire current bespoke surface. The decomposer side already followed this collapse (Stage 2). The synthesizer side has been deferred and is the unfinished half of AP-30.

### Existing infrastructure (real, ready to use)
- `Hartonomous.Core.Recomposition.LayerTypeSynthesizerBase` — abstract base with honest-abstention masking + dtype packing (F64/F32/BF16/F16) + coverage tracking + source filtering. Subclasses implement `SynthesizeF64Async`.
- `Hartonomous.Core.Compute.Common.InverseLaplacianEigenmap.ProjectF64` — **native** (calls `NativeCompute.InverseEigenmapF64`). 4D firefly → hidden_dim reverse projection.
- `Hartonomous.Core.Compute.Common.SparseFfnInversion` — native joint gate/up/down FFN inversion.
- `Hartonomous.Core.Compute.Common.LinearSystemSolver` — native least-squares solves.
- `Hartonomous.Core.Compute.Common.GramSchmidt` — orthonormalization.
- `Hartonomous.Core.Compute.Common.KarcherMeanS3` — S³ consensus aggregation (for multi-model firefly cloud centroid).
- `Hartonomous.Core.Compute.Common.HonestAbstentionFiller.ApplyF64` — per-cell abstention masking.
- `Hartonomous.Core.Compute.Ingestion.ProcrustesAlign.F64` — native Kabsch alignment.
- MKL/Eigen/Spectra-backed Lanczos via `SparseSymEigs.F64` — when actual matrix-level decomposition is required (e.g. attention QK low-rank factorization).
- `Hartonomous.Core.Recomposition.LayerTypeSynthesizerRegistry` — dispatch by role code.

### 4 PrimitiveSynthesizers

#### 4.1 LinearSynthesizer
**Covers:** `attention_query`, `attention_key`, `attention_value`, `attention_output`, `ffn_gate`, `ffn_up`, `ffn_down`, `lm_head`, `intermediate`, `output`, `bbox_proj`, `class_proj`, `lora_base`, `moe_expert_gate`, `moe_expert_up`, `moe_expert_down`, `moe_router`.

**Math per spec §VII:**
For Q/K/V/O at layer L: read `model_attention_pattern` edges between vocab pairs (using new `GetEdgeSignificanceMatrixAsync`) filtered by recipe per-layer arena weighting + attribution metadata `(Linear, AttentionBlock, {Q|K|V|O}, LayerIdx=L)`. Aggregate per-cell consensus mu. Run sign-aware mu-to-cell transform: `cell[i,j] = Σ_arena w_arena[layer] · sign(mu - 1500) · (|mu - 1500| / 1500) · peak_magnitude[dtype]`. For Q^T·K reproducibility: AttentionTupleSynthesizer wraps this and ensures Q and K use the SAME SVD source matrix.

For FFN gate/up/down: read `model_ffn_factor` edges, jointly inverted via `SparseFfnInversion` to recover (gate, up, down) such that the composed FFN response reproduces the consensus pattern.

For LM head when not tied: read `model_concept_similarity` edges between vocab tokens, project via PCA on per-token attestation participation.

**Source filtering for Mode 1 vs Mode 2:** when `SynthesisContext.SourceModelIds` non-null, restrict to those provenances; otherwise aggregate all ingested sources at recipe-configured `provenance_weights`.

#### 4.2 LookupSynthesizer
**Covers:** `token_embedding`, `position_embedding`, `position_embedding_2d`, `token_type_embedding`, `rope_freq`, `alibi_slope`, `codec_codevector_table`.

**Math:** For each row (one per vocab token / position / codevector), per spec §VII:
1. Read per-token native 220-dim signal via `GetCentroidsAsync` + `GetEntitySignificanceProfileAsync` + `GetEdgeTypeParticipationAsync` + `GetProvenanceAttestationProfileAsync`.
2. When ingested models exist: read firefly cloud via `GetFireflyCloudAsync` for additional per-model 4D positions (canonically aligned via existing Procrustes infrastructure).
3. Apply `InverseLaplacianEigenmap.ProjectF64` to reverse-project the consensus 4D position + per-arena signal profile into target hidden_dim.
4. Apply `GramSchmidt` orthonormalization on the row matrix for numerical stability.
5. For RoPE / ALiBi: deterministic from architecture spec (theta, slopes), not substrate-projected. Per spec §IV.

#### 4.3 LocalKernelSynthesizer
**Covers:** conv kernels in `ConvResidualBlock`, `ConformerBlock` conv_module, `PatchEmbed`, ViT/Swin patch attention.

**Math:** Per (kernel_position, out_channel, in_channel): aggregate `model_spatial_pattern` attestation mu between neighboring pixel_region / audio_chunk pairs in that kernel's stride window. Project into target kernel size via dimension-aware interpolation when source kernels don't match target shape exactly.

#### 4.4 NormalizationSynthesizer
**Covers:** `layer_norm_gamma`, `layer_norm_beta`, `rms_norm_weight`, batch norm γ/β/running_mean/running_var.

**Math:** Per-feature γ/β derived from substrate token-distribution statistics under the layer's primary arena (per recipe `per_layer_arena_assignment`). Reuse existing `LayerNormSynthesizer.LoadLayerNormStatsAsync` (already substrate-derived per arena). Extend to RMSNorm + BN.

### 3 TupleSynthesizers

#### 4.5 AttentionTupleSynthesizer
Orchestrates 4 LinearSynthesizer calls (Q, K, V, O) ensuring Q^T·K low-rank factorization uses shared consensus SVD source. Per-layer arena weighting from recipe `per_layer_arena_assignment`. MoE variant: per-expert arena scoping.

#### 4.6 FfnTupleSynthesizer
Joint construction of (gate, up, down) via `SparseFfnInversion`. MoE expert variant scopes per expert. SwiGLU vs BERT-FFN dispatch by `ArchetypeTuple` from recipe.

#### 4.7 LoraDeltaSynthesizer
Rank-r SVD truncation of delta consensus matrix. Reads `model_attention_pattern` or `model_ffn_factor` attestations with `EdgeRatingEvent.AdaptationOf` attribution. Produces (A, B) factor pair at user-specified rank.

### Registry wiring + SubstrateModelExporter rewrite
- `LayerTypeSynthesizerRegistry` registers the 4 primitive + 3 tuple synthesizers, each with their full `TargetRoleCodes` list.
- `SubstrateModelExporter` rewrites the `BuildTensorSetAsync` loop to dispatch via the registry per `TargetTensorSpec.RoleCode`. Recipe drives shape via `TargetArchitectureSpec`; per-layer arena weighting threaded through `SynthesisContext`.
- Per-architecture name → role mapping uses the SAME TupleResolution profiles as the decomposer side (reciprocal pairing — `BertArchitectureProfile` etc.).

### Critical files
- New: `src/Hartonomous.Recomposers/Synthesizers/Primitives/LinearSynthesizer.cs`
- New: `src/Hartonomous.Recomposers/Synthesizers/Primitives/LookupSynthesizer.cs`
- New: `src/Hartonomous.Recomposers/Synthesizers/Primitives/LocalKernelSynthesizer.cs`
- New: `src/Hartonomous.Recomposers/Synthesizers/Primitives/NormalizationSynthesizer.cs` (folds in existing LayerNormSynthesizer logic, extends to RMSNorm/BN)
- New: `src/Hartonomous.Recomposers/Synthesizers/Tuples/AttentionTupleSynthesizer.cs`
- New: `src/Hartonomous.Recomposers/Synthesizers/Tuples/FfnTupleSynthesizer.cs`
- New: `src/Hartonomous.Recomposers/Synthesizers/Tuples/LoraDeltaSynthesizer.cs`
- Edit: `src/Hartonomous.Core/Recomposition/LayerTypeSynthesizerRegistry.cs` (register the 7)
- Rewrite: `src/Hartonomous.Recomposers/Synthesizers/SubstrateModelExporter.cs` (dispatch via registry)
- Edit: `src/Hartonomous.Recomposers/Synthesizers/RecipeTemplates.cs` (populate `per_layer_arena_assignment` for minilm-base / bert-base / llama-small / llama-1b / llama-3b / qwen-7b / mistral-7b templates)

### Acceptance
- All 4 primitive + 3 tuple synthesizers compile + unit-test against synthetic substrate attestations
- `scripts/hart synthesize-model --template minilm-base --vocab-size 30000 --output X --dtype f16` produces a valid safetensors file loadable in HuggingFace transformers
- Coverage report shows per-tensor synthesis-vs-abstention rate per layer per primitive
- For Llama-small target: produces coherent next-token predictions on held-out text (corpus-only substrate state has enough attestation density on common English tokens for sentence-level perplexity in a reasonable range; held-out eval target TBD)
- For MiniLM-base target: produces semantically-organized embedding cells where related tokens (synonyms / hypernyms / translation pairs) cluster by cosine similarity — verified via STS-B-style probe

---

## Stage 5: Legacy bespoke synthesizer + band-aid deletion (AFTER Stage 4 online)

### Deletions
- `src/Hartonomous.Recomposers/Synthesizers/AttentionSynthesizer.cs`
- `src/Hartonomous.Recomposers/Synthesizers/EmbeddingSynthesizer.cs`
- `src/Hartonomous.Recomposers/Synthesizers/FfnSynthesizer.cs`
- `src/Hartonomous.Recomposers/Synthesizers/FfnEdgeSlotSynthesizer.cs` (fold substrate-direct query path into LinearSynthesizer)
- `src/Hartonomous.Recomposers/Synthesizers/PositionEmbeddingSynthesizer.cs` (fold into LookupSynthesizer)
- `src/Hartonomous.Recomposers/Synthesizers/LayerNormSynthesizer.cs` (replaced by NormalizationSynthesizer)
- Per-layer-adjacency band-aid in `SubstrateModelExporter.cs` (lines 195-220 of 2026-05-19 edit + `WithLayerArenaWeights` helper) — wrong abstraction; PrimitiveSynthesizers read per-tensor-cell attestations directly without a vocab×vocab adjacency.
- `src/Hartonomous.Recomposers/Synthesizers/SubstrateAdjacencyBuilder.cs` — keep for analytics surfaces (frayed-edge detection, Voronoi consensus) but remove from synth hot path.
- `src/Hartonomous.Recomposers/Synthesizers/SubstrateAdjacency.cs` — same; demote to analytics utility.
- `src/Hartonomous.Recomposers/Synthesizers/KnowledgeSelector.cs` — fold its BFS into per-tensor synthesizers if substrate-canonical, or delete if redundant with VocabSelector.

### Retained
- `ScaffoldSynthesizer` — for honest-abstention fallback when target dim exceeds available substrate signal
- `VocabSelector` — selects top-N tokens by edge_member degree; substrate-canonical, keep
- `TokenizerExporter` — surface-form recovery via BulkTierContentWalk; substrate-canonical, keep
- `BearCostEstimator` — pre-synth cost estimation; useful for the bear-builder pricing API
- `RecipeConfig`, `RecipeTemplates` — recipe surface; keep

---

## Stage 6: AI model ingestion (ModelDecomp phase against /vault/models)

**Status: substantial decomposer-side infrastructure exists; runtime verification pending.**

### Existing
- `SafetensorsDecomposer` + `ModelPassOrchestrator`
- 5 primitive+tuple passes (per Stage 2)
- 6 architecture profiles (BERT, Llama, Qwen3-MoE, DaViT, FLUX VAE, PEFT LoRA)
- `EmbeddingLayerDecomposer` (emits per-token firefly POINTZM after Laplacian + GSO + Procrustes alignment)
- `entity_model_source` table for per-model attribution
- `provenance_modality` for cross-modal model classification

### Deliverables for Stage 6 close
- 🔜 Verify Stage 2's new passes (Stage 2 prereq)
- 🔜 Run `scripts/hart phase run --phase ModelDecomp --source /vault/Data --model-source /vault/models` against a representative sample (Llama-2-7B, MiniLM-L6-v2, Qwen3-Coder-MoE, Florence-2, canary-qwen, Stable Diffusion 1.5 VAE)
- 🔜 Verify per AP-31 sign-bearing events fire correctly
- 🔜 Verify cross-model edge identity collapse — Llama and BERT both ingested → `model_attention_pattern(king, queen)` has games ≥ 2 from both provenances
- 🔜 Verify firefly emission populates `substrate.physicality` with `physicality_type='firefly'` rows, Procrustes-aligned to canonical anchor frame
- 🔜 Stage 4 synthesis output now meaningful: synthesize MiniLM-base after multi-model ingest; embedding cells now reflect both corpus AND multi-model consensus

### Acceptance
- Multi-architecture ingest completes without phase errors
- Cross-model corroboration query (`SELECT count(*) FROM substrate.edge_significance WHERE games >= 2 AND context_type_id = (SELECT id FROM substrate.significance_context WHERE code = 'attention_pattern_confidence')`) returns substantial rows
- Synth output coverage report shifts from "corpus-only natural signal" to "corpus + N-model attestation density" per tensor

---

## Stage 7: Multi-modality content decomposers (first-class, NOT deferred)

**Status: entity types seeded; decomposers pending.**

Per architectural correction #7 — modality symmetry is first-class. Audio/image/video have the SAME mantissa-packed archival shape as text, the SAME bit-perfect tier-walk recompose property, the SAME participation in `has_classification` / `has_relation` typed-edge attestations. Deferring them as "second-class after text" was the prior plan's framing error.

### AudioContentDecomposer
- Input: WAV/FLAC/MP3 files (libsndfile via P/Invoke or managed decoder via NAudio)
- Tier walk: `audio_recording` → `audio_chunk` → audio sample atom with POINTZM physicality
- Atom POINTZM: (time_seconds, sample_value_normalized, channel_index, sample_format_metadata)
- Chunk LINESTRINGZM: vertices through sample atoms in temporal order; mantissa-packs (sample_value, time, channel, fft_bin or rms or mfcc_dim)
- Recording-level: LINESTRINGZM through chunks
- Forced alignment to transcript word_forms: when timing metadata available (Tatoeba per-audio JSON, LibriSpeech alignments), emit `recording_aligns_to(audio_chunk, word_form)` edges
- Mel/MFCC features: per-chunk physicality on `audio_chunk` entity (analytics surface)
- Bit-perfect waveform recompose via tier walk → BulkTierContentWalk extended to handle audio modality

### ImageContentDecomposer
- Input: PNG / JPEG / WebP files (libpng / libjpeg-turbo / libwebp via P/Invoke, or managed via ImageSharp)
- Tier walk: `pixel_region` (or `image_document` → region) → pixel atom with POINTZM physicality
- Atom POINTZM: (x_pixel, y_pixel, intensity_value, channel_class_metadata)
- Region: POLYGONZM for closed regions OR MULTIPOINTZM for ungrouped pixel cloud
- Patch grid: MULTILINESTRINGZM with per-row LINESTRINGZM
- Bit-perfect pixel recompose via tier walk

### VideoContentDecomposer
- Input: container demux (libavformat for MP4 / MKV / WebM via FFmpeg P/Invoke)
- Tier walk: video → `video_frame` → `pixel_region` → pixel atom
- Frame: GEOMETRYCOLLECTIONZM (time axis + per-frame pixel_region layout)
- Bit-perfect frame-stream recompose via tier walk

### Cross-modal grounding
After Stages 6 + 7 both land: CLIP / BLIP / Florence / Whisper ingest emits `model_cross_modal_pattern(word_form, pixel_region)` and `model_cross_modal_pattern(word_form, audio_chunk)` edges that bridge the modalities. CrossAttentionTuplePass + SpatialKernelTuplePass (Stage 2) emit these.

### Critical files
- New: `src/Hartonomous.Decomposers/Audio/AudioContentDecomposer.cs`
- New: `src/Hartonomous.Decomposers/Image/ImageContentDecomposer.cs`
- New: `src/Hartonomous.Decomposers/Video/VideoContentDecomposer.cs`
- Extend: `src/Hartonomous.Core/Recomposition/BulkTierContentWalk.cs` (modality dispatch — text continues to recompose via codepoint atoms + UTF-8; audio via sample atoms + PCM frame reassembly; image via pixel atoms + format encode; video via frame stream + container mux)
- Extend: `src/Hartonomous.Cli/Commands/PhasesCommand.cs` register audio/image/video decomposers under their respective phases (new Phase enum values may be needed)
- Extend: `sql/schema/seed/significance_context.sql` if new modality-specific arenas needed (e.g. `audio_pronunciation_alignment`, `image_object_detection_confidence`)

### Acceptance
- Ingest 1 sample Tatoeba audio recording → audio_recording + audio_chunks land in substrate; recompose produces bit-identical WAV
- Ingest 1 sample image with caption → pixel_region entities land; CLIP-class model ingest emits cross-modal edges
- Cross-modal synthesis: synthesize a vision-language model recipe; verify cross-modal cells project from `model_cross_modal_pattern` attestations

---

## Stage 8: Additional dataset ingestion (richer attestation density)

**Each dataset = text content (bit-perfect archival) + typed-edge attestation metadata. Same architectural pattern as Wiktionary/WordNet/UD/Tatoeba.**

### Atomic 2020
- ~1.33M commonsense tuples (event_text, relation_type, inference_text)
- 23 relation types: `xIntent`, `xReact`, `xWant`, `xNeed`, `xEffect`, `xAttr`, `oReact`, `oWant`, `oEffect`, `HinderedBy`, `isAfter`, `isBefore`, `HasSubEvent`, `Causes`, `CausesDesire`, `MadeUpOf`, `MotivatedByGoal`, `ObjectUse`, `Desires`, `NotDesires`, `CapableOf`, `HasProperty`, `AtLocation`
- Emit each tuple as: text content (event + inference both canonical-decompose to text_composition) + typed `has_relation` edges discriminated by relation_kind content-entity
- Per AP-30 generic-edge collapse: use existing `has_relation(source, target, kind_entity)` shape OR introduce relation_kind as 3-member edge

### ConceptNet
- ~30 relation types, ~21M edges
- Similar emit pattern to Atomic 2020

### FrameNet + VerbNet + PropBank
- Frame semantics + verb classification + predicate-argument structure
- Emits has_frame / has_role / has_predicate_argument edges between word_form / synset entities

### BabelNet
- Multilingual lexical resource, ~16M synsets, ~520 languages
- Cross-aligns with OMW + Wiktionary on shared synset hashes — sigma tightening on existing edges from cross-source corroboration

### Wikidata
- Structured knowledge graph, ~100M items, ~1.5B statements
- Major attestation density boost; emits structured-relation edges between content-addressed entities

### Safety / hate datasets (negative-evidence)
- Curated conversational pattern datasets labeled by toxicity / hate / unsafe
- Emit text content (bit-perfect) + `SignedEventsFor(edgeCode, signedValue)` with `score=0, weight=|severity|` on conversational pattern edges
- Arenas: `safety_alignment`, `conversational_quality`, `register_appropriateness`
- Drop mu on those edge identities; at inference A* edge cost = 1/mu, so traversal routes around → output composition steers away

### Critical files
- New: `src/Hartonomous.Decomposers/Atomic2020/Atomic2020Decomposer.cs`
- New: `src/Hartonomous.Decomposers/ConceptNet/ConceptNetDecomposer.cs`
- New: `src/Hartonomous.Decomposers/FrameNet/FrameNetDecomposer.cs`
- New: `src/Hartonomous.Decomposers/VerbNet/VerbNetDecomposer.cs`
- New: `src/Hartonomous.Decomposers/PropBank/PropBankDecomposer.cs`
- New: `src/Hartonomous.Decomposers/BabelNet/BabelNetDecomposer.cs`
- New: `src/Hartonomous.Decomposers/Wikidata/WikidataDecomposer.cs`
- New: `src/Hartonomous.Decomposers/Safety/SafetyDecomposer.cs` (handles labeled toxicity datasets via SignedEventsFor)
- New entity_type rows: `frame`, `frame_element`, `verb_class`, `propbank_role`, `wikidata_item`, `wikidata_property`
- New edge_type rows: `has_frame`, `has_role`, `has_predicate_argument`, `has_wikidata_property`, `instance_of`, `subclass_of`, `part_of_taxonomy`, etc.

### Acceptance
- Each dataset's ingest completes without phase errors
- Cross-source consensus tightens on shared edge identities (e.g. WordNet hypernym + ConceptNet IsA + Wikidata instance_of collapse on same edge hash for `(canine, mammal)`)
- Synth output coverage report shifts upward for tokens with multi-dataset attestation

---

## Stage 9: Production model export verification

### Verification targets
- **MiniLM-base sentence embedding model:** synth from corpus + multi-model substrate state; load in HuggingFace `transformers.AutoModel`; produce sentence embeddings; evaluate on STS-B (Sentence Textual Similarity Benchmark) held-out probe; target: above-random Spearman correlation, ideally within 80% of source MiniLM-L6-v2 baseline.
- **Llama-1B causal LM:** synth from substrate state including multi-model ingest; load in HF; produce next-token predictions; evaluate perplexity on held-out WikiText / C4 sample; target: coherent enough for sentence-level generation, perplexity within reasonable range (not literature-comparable without model ingest scaling).
- **CLIP-class vision-language model:** synth after Stage 7 image content + multi-modal model ingest; verify text↔image alignment on COCO captions held-out sample.

### Reproducibility
- Same `(target_architecture_spec, recipe_options, substrate_state_hash)` produces same output bytes per Law #6 (deterministic ingest) + synth determinism boundary per spec §XI.2.
- Synth audit metadata in safetensors header records recipe hash, arena weighting, provenance filter, abstention threshold.

---

## Anti-patterns / things to delete (catalog from this session)

1. **Per-layer adjacency build via 6 serial PG scans** — wrong abstraction. PrimitiveSynthesizers read per-tensor-cell attestations directly; no vocab×vocab adjacency intermediate. (Stage 5 deletion)
2. **`WithLayerArenaWeights` helper** — couples wrong abstraction. (Stage 5 deletion)
3. **Lanczos eigenmap on substrate adjacency as the embedding path** — substrate's recursive Merkle centroid + per-arena entity_significance signal IS the substrate's spectral decomposition; conventional eigenmap reflex is redundant. (Stage 5 deletion via LookupSynthesizer)
4. **Bespoke per-tensor-role synthesizers** — AttentionSynthesizer + EmbeddingSynthesizer + FfnSynthesizer + LayerNormSynthesizer + PositionEmbeddingSynthesizer collapse to 4 primitive + 3 tuple per spec §VII. (Stage 5 deletion)
5. **Per-dimension `has_cp_*` edge_type proliferation** — already collapsed this session to single polymorphic `has_classification` discriminated by target entity_type + (provenance × arena). Same principle applies anywhere new per-dimension classification edges are tempted.
6. **Conventional-ML pattern matching reflexes** — Lanczos on derived adjacency; vocab×vocab self-join for every layer; thinking of synthesis as "training a new model"; treating modalities as second-class to text; treating AI models as required for synthesis (corpus alone suffices for native 220-dim signal).
7. **plpgsql self-joins on the hot synth path** — native PG C function with AVX2/AVX-VNNI hash matching is the proper perf answer, not C# wrappers around 675s SQL.

---

## Stage dependencies and ordering

```
Stage 1 (substrate foundation) ──> Stage 2 (decomposer AP-30 finish) ──┐
                              └──> Stage 3 (substrate query surface) ──┴──> Stage 4 (synthesizer collapse) ──> Stage 5 (legacy delete) ──> Stage 9 (production verify)
                                                                                                                       │
Stage 6 (AI model ingest) ──────────────────────────────────────────────────────────────────────────────────────────────┤
                                                                                                                       │
Stage 7 (multi-modality decomposers) ───────────────────────────────────────────────────────────────────────────────────┤
                                                                                                                       │
Stage 8 (additional datasets) ─────────────── parallel anytime ────────────────────────────────────────────────────────┘
```

- Stage 1 close: required for any further work
- Stages 2 + 3: parallel; both required before Stage 4 (decomposer side completion + query surface)
- Stage 4: the load-bearing delivery — produces production-quality synth output
- Stage 5: cleanup, AFTER Stage 4 online (else nothing to fall back to)
- Stage 6: parallel after Stage 2 finishes (model ingest needs the architecture profiles)
- Stage 7: parallel after Stage 1 (modality decomposers are independent of synth)
- Stage 8: parallel anytime — dataset additions are pure substrate-content enrichment
- Stage 9: AFTER Stage 4 + 6 (need real model output to verify)

---

## Verification per stage (concrete + executable)

### Stage 1
```bash
scripts/hart db reset --force
scripts/hart phase run --source /vault/Data
# Expect: all phases (UcdUca, Iso639, WordNetOmw, UniversalDeps, Wiktionary, Tatoeba) complete; Wiktionary < 2h
```
```sql
-- Cross-source POS attestation
SELECT p.code, count(*) FROM substrate.edge_significance es
  JOIN substrate.edge e ON e.edge_type_id = es.edge_type_id AND e.hash = es.edge_hash
  JOIN substrate.edge_type et ON et.id = e.edge_type_id
  JOIN substrate.provenance p ON p.id = e.provenance_id
 WHERE et.code IN ('has_pos', 'has_classification')
   AND es.games > 0
 GROUP BY p.code;
-- Expect: ≥3 provenances
```

### Stage 2
```bash
scripts/hart phase run --phase ModelDecomp --model-source /vault/models
# Expect: clean completion across one of each architecture (Llama, BERT, Qwen3-MoE, Florence-2, canary-qwen, Conditional-DETR, FLUX VAE)
```

### Stage 3
```bash
dotnet test tests/Hartonomous.Engine.Tests --filter SubstrateQuerySurface
# Expect: IEntityReader.GetEdgeSignificanceMatrixAsync, GetCentroidsAsync, etc. return correct shapes against known fixtures
```
```sql
SELECT substrate.build_synth_adjacency_csr(ARRAY[hash1, hash2, hash3]::bytea[], ARRAY['semantic_relevance']::text[], false);
-- Expect: binary blob returned in < 60s for v=30K
```

### Stage 4
```bash
scripts/hart synthesize-model --template minilm-base --vocab-size 30000 --output /tmp/synth-30k --dtype f16
# Expect: model.safetensors produced; loads in HF transformers; coverage report > 0% per primitive
python3 -c "from transformers import AutoModel; m = AutoModel.from_pretrained('/tmp/synth-30k'); print(m)"
```

### Stage 5
```bash
grep -r "AttentionSynthesizer\|EmbeddingSynthesizer\|FfnSynthesizer\|FfnEdgeSlotSynthesizer\|PositionEmbeddingSynthesizer\|LayerNormSynthesizer\|WithLayerArenaWeights\|SubstrateAdjacencyBuilder.BuildAsync" src/
# Expect: zero hits in synth hot path; SubstrateAdjacencyBuilder only referenced from analytics surface
```

### Stage 6
```sql
SELECT count(*) FROM substrate.entity ec
  JOIN substrate.entity_classification c ON c.entity_hash = ec.hash
  JOIN substrate.entity_type et ON et.id = c.entity_type_id
 WHERE et.code = 'tensor';
-- Expect: thousands+ tensor entities post-ModelDecomp

SELECT count(*) FROM substrate.physicality
 WHERE physicality_type_id = (SELECT id FROM substrate.physicality_type WHERE code = 'firefly');
-- Expect: vocab_size × N_models firefly POINTZMs
```

### Stage 7
```bash
scripts/hart phase run --phase AudioDecomp --source /vault/Data
# Tatoeba audio → audio_recording + audio_chunk + sample atoms
```
```sql
SELECT count(*) FROM substrate.physicality
 WHERE physicality_type_id IN (SELECT id FROM substrate.physicality_type WHERE code IN ('content', 'entity'))
   AND geom IS NOT NULL;
-- Expect: thousands+ audio_chunk LINESTRINGZMs + per-sample POINTZMs
```

### Stage 8
```sql
SELECT et.code, count(*) FROM substrate.edge e
  JOIN substrate.edge_type et ON et.id = e.edge_type_id
 WHERE et.code IN ('xIntent', 'xReact', 'IsA', 'has_frame', 'has_predicate_argument')
 GROUP BY et.code;
-- Expect: substantial edges per dataset post-ingest
```

### Stage 9
```bash
# Held-out evaluation suite
python3 evaluation/sts_b_probe.py --model /tmp/synth-minilm-30k
python3 evaluation/perplexity_probe.py --model /tmp/synth-llama-1b --eval-set wikitext-2-test
python3 evaluation/clip_alignment_probe.py --model /tmp/synth-clip --eval-set coco-captions-test
```

---

## Critical files index (consolidated)

### Substrate (Stage 1 — mostly done)
- `sql/schema/tables/junctions/cp_*.sql` (9 narrow per-property junctions ✅)
- `sql/schema/seed/entity_type.sql` (34 rows ✅), `sql/schema/seed/edge_type.sql` (134 rows ✅)
- `src/Hartonomous.Core/Compute/Common/ReferenceVocabularyHashes.cs` (12 helpers ✅)
- `src/Hartonomous.Decomposers/EdgeArenaRouter.cs` (EventsFor + SignedEventsFor + per-(edge × target_type) routing ✅)
- `src/Hartonomous.Decomposers/Ucd/UnicodeDecomposer.cs` (§3 emits typed has_classification edges + narrow junctions ✅)

### Decomposer AP-30 finish (Stage 2)
- `src/Hartonomous.Decomposers/Safetensors/Passes/{LinearProjectionPass,LocalKernelPass,LookupPass,CrossAttentionTuplePass,SpatialKernelTuplePass}.cs` (NEW)
- `src/Hartonomous.Decomposers/Safetensors/TupleResolution/{BartArchitectureProfile,ConformerArchitectureProfile,SwinArchitectureProfile,DetrArchitectureProfile}.cs` (NEW)

### Substrate query surface (Stage 3)
- `src/Hartonomous.Core/Data/IEntityReader.cs` (extend)
- `src/Hartonomous.Core/Data/IPhysicalityReader.cs` (extend)
- `src/Hartonomous.Engine/Data/NpgsqlEntityReader.cs` + `NpgsqlPhysicalityReader.cs` (implement)
- `ext/hartonomous_pg/src/pg_synth_adjacency.c` (NEW — native PG function with AVX2/AVX-VNNI)
- `ext/hartonomous_pg/sql/functions/build_synth_adjacency_csr.sql` (NEW — binding)
- `src/Hartonomous.Core/Recomposition/SynthesisContext.cs` (add IProgress<string>)

### Synthesizer collapse (Stage 4 — LOAD-BEARING)
- `src/Hartonomous.Recomposers/Synthesizers/Primitives/{LinearSynthesizer,LookupSynthesizer,LocalKernelSynthesizer,NormalizationSynthesizer}.cs` (NEW)
- `src/Hartonomous.Recomposers/Synthesizers/Tuples/{AttentionTupleSynthesizer,FfnTupleSynthesizer,LoraDeltaSynthesizer}.cs` (NEW)
- `src/Hartonomous.Core/Recomposition/LayerTypeSynthesizerRegistry.cs` (register 7)
- `src/Hartonomous.Recomposers/Synthesizers/SubstrateModelExporter.cs` (rewrite dispatch via registry)
- `src/Hartonomous.Recomposers/Synthesizers/RecipeTemplates.cs` (populate per_layer_arena_assignment per template)

### Legacy deletions (Stage 5 — AFTER Stage 4)
- Delete: `AttentionSynthesizer.cs`, `EmbeddingSynthesizer.cs`, `FfnSynthesizer.cs`, `FfnEdgeSlotSynthesizer.cs`, `PositionEmbeddingSynthesizer.cs`, `LayerNormSynthesizer.cs`
- Delete: per-layer adjacency band-aid in `SubstrateModelExporter.cs` + `WithLayerArenaWeights` helper
- Demote: `SubstrateAdjacencyBuilder.cs` + `SubstrateAdjacency.cs` to analytics-only surface

### AI model ingest (Stage 6)
- Existing decomposer surface (Safetensors family — `SafetensorsDecomposer`, `ModelPassOrchestrator`, primitive+tuple passes, architecture profiles)
- Run-time wiring via `scripts/hart phase run --phase ModelDecomp`

### Multi-modality (Stage 7)
- New: `src/Hartonomous.Decomposers/Audio/AudioContentDecomposer.cs`
- New: `src/Hartonomous.Decomposers/Image/ImageContentDecomposer.cs`
- New: `src/Hartonomous.Decomposers/Video/VideoContentDecomposer.cs`
- Extend: `src/Hartonomous.Core/Recomposition/BulkTierContentWalk.cs` for modality dispatch

### Additional datasets (Stage 8)
- New decomposer per dataset: `Atomic2020Decomposer`, `ConceptNetDecomposer`, `FrameNetDecomposer`, `VerbNetDecomposer`, `PropBankDecomposer`, `BabelNetDecomposer`, `WikidataDecomposer`, `SafetyDecomposer`

### Production verification (Stage 9)
- New: `evaluation/sts_b_probe.py`, `perplexity_probe.py`, `clip_alignment_probe.py` (Python harness scripts — substrate is loadable by HF transformers; eval is just Python glue)

---

## Existing reusables (do NOT reimplement — already native or substrate-canonical)

- `Hartonomous.Core.Compute.Common.{Blake3, MantissaPacking, Hilbert, GramSchmidt, KarcherMeanS3, HonestAbstentionFiller, InverseLaplacianEigenmap, SparseFfnInversion, LinearSystemSolver, SuperFibonacci, Merkle, S3Geometry, ReferenceVocabularyHashes, Hash32, PhysicalityEmitter}` — all native + tested
- `Hartonomous.Core.Compute.Ingestion.ProcrustesAlign.F64` — native Kabsch
- `SparseSymEigs.F64` — MKL/Spectra-backed Lanczos
- `Hartonomous.Core.Text.{SubstrateTextDecomposer.EmitStatic, CanonicalTextDecomposer.Emit}` — canonical text path (rule 10 seed-uses-core)
- `Hartonomous.Core.Recomposition.{ContentRecomposer, BulkTierContentWalk}` — bit-perfect text recompose
- `Hartonomous.Core.Recomposition.LayerTypeSynthesizerBase` — honest abstention + dtype packing + coverage tracking
- `IIngestionPipeline.GetExisting*Async` — AP-19 bulk-probe surface
- `IIngestionPipeline.CreateBatch + SubmitBatchAsync` — bundled emit
- `Glicko2.UpdateBulk` via `hartonomous_glicko2_bulk_update` native binding
- Existing TupleResolution profiles (6 architectures done)
- Existing primitive+tuple passes (5 done)

---

## Stages are sequential where dependent, parallel where independent

The substrate's invention requires Stage 4 (synthesizer collapse) for production-quality model export. Stages 6 + 7 + 8 enrich the substrate's attestation density and produce more capable familiars but are not blockers for the synthesizer collapse itself. Stages 1 + 2 + 3 are prerequisites for Stage 4. Stage 5 is cleanup AFTER Stage 4 lands. Stage 9 verifies the whole chain.

A stage that "ships" while breaking prior stages is not actually closed. Cross-stage regression at every close.

---

## Out of scope (this plan)

- Inference engine refinements (separate `35-inference-and-godel.md` surface — Gödel Engine OODA loop, A* traversal mechanics)
- Crystal Ball / Substrate Lens analytics queries (separate plan; depends on Stage 6 model ingest density)
- HTTP API (M9 in `docs/build-plan.md`; depends on Stages 4 + 6 for export endpoint)
- CI/CD wiring (separate ops scope)
- Production deployment (separate ops scope)
