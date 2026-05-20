# Hartonomous Substrate — Complete Plan (2026-05-15 consolidated)

## Context — Why this plan exists

This plan covers the complete substrate work from foundation-fix through Build-a-bear synthesis + Crystal Ball analytics. The full conversation that produced it surfaced ~25 architectural corrections and ~20 concrete ETL flaws; foundation work landed across two sessions (P1a/b/c/d/f-min/h/i + P3a + partial P3b parsers/emitters + partial P4 ISO 639-2/BCP47 parsers + 8 memory files).

**Unicode + ISO is the lynchpin, not a chore.** The substrate before Unicode ingest is an empty table. Every later attestation — every WordNet synset, every Wiktionary etymology, every Tatoeba sentence, every UD parse, every AI model's `model_attention_pattern`, every user prompt, every uploaded document — bottoms out at text decomposing through codepoints. Unicode + ISO is where attestation edges START forming and where the substrate's "universal absorbent" property gets proven. Treating it as foundation lookup tables (the conventional framing) gives a static dictionary; treating it as accumulated multi-source consensus (the invention framing) gives the substrate. See [[project-unicode-iso-as-lynchpin]] memory for the architectural framing.

## Foundation properties the plan must deliver

1. **Universal absorbent.** Every digital source ingests via the same pipeline.
2. **Content-addressed Merkle DAG.** Identical content from any source collapses by BLAKE3.
3. **Three physicality roles.** entity / firefly / content with distinct partitions + semantics.
4. **Mantissa-packed structural-identity LINESTRINGZM.** Geometry IS the indexed child manifest. O(tier) reconstruction via composite-btree hash-prefix reverse-resolve.
5. **GiST 4D bbox query primitive.** "Find every trajectory containing entity X" = one indexed bbox prune. Provenance / arena / corroboration / temporal stratification fall out.
6. **Unified Glicko-2 surface.** POS / sense / language / morph / model attestations all compete on `substrate.edge_significance` per arena. `(provenance × arena)` discriminates.
7. **No phase boundaries.** Streaming, continuously queryable. Edge geom inline at INSERT. Glicko priming inline at INSERT.
8. **Universal byte-to-structure parsing.** Tree-sitter + UAX #29 cover all digital content (Phase 2 — refactor after corpora prove pipeline).
9. **Pre-gen ≠ substrate ingestion.** Build-time deterministic-math perf cache (XML-flat-canonical for per-codepoint properties) vs. runtime substrate-content ingestion via populate functions.
10. **Build-a-bear synthesis from consensus.** Project accumulated per-arena `edge_significance` into target tensor basis. Standard safetensors output. No round-trip of any ingested source.

## Foundation landed in earlier sessions

- **P1a** schema partition reinstate: `physicality_entity_shape` (id 15) + `physicality_ingestion_trajectory` (id 16) — `sql/schema/seed/physicality_type_trajectories.sql`, `sql/schema/tables/core/physicality_entity_shape.sql`, `sql/schema/tables/core/physicality_ingestion_trajectory.sql`, `sql/schema/bootstrap.sql`.
- **P1b** IngestionBatch concrete: `AddEntityShape` / `AddIngestionTrajectory` / `AddFireflyPoint` — `src/Hartonomous.Engine/Ingestion/IngestionBatch.cs`, signature fix in `IIngestionBatch.cs`.
- **P1c** Decomposer routing: UD sentence → `AddIngestionTrajectory`; WordNet synset physicality DELETED (synsets are attested concepts not trajectories); NormalizationPrimitivePass tensor γ-scale → `AddEntityShape`.
- **P1d** attestation_type collapsed 27→3 generic rows + graceful fallback in `resolve_attestation_type_id`.
- **P1f-minimal** drain completion (not phase boundary) triggers post-passes. `StreamingIngestionPipeline.DrainPendingAsync` invokes `PopulateEdgeTrajectoriesAsync` + `PrimeAllSignificanceAsync` automatically. `SequentialPhaseRunner` no longer invokes them as phase post-passes.
- **P1h** AP-8 rewritten (classifications as edges) + new AP-37 (no phase backfill) + AP-38 (no modality-specific attestation_type).
- **P1i** Tatoeba AP-19 amplification fixed — direct EntityHandle construction for cached hashes; no redundant AddEntity calls.
- **P3a** Unicode pre-gen Linux path default (`/vault/Data/Unicode/Public/UCD/latest`).
- **P3b partial** 7 parsers + 7 emitters + main integration + umbrella includes for non-XML UCD data: NamedSequences, EmojiSequences, EmojiZwjSequences, StandardizedVariants, Confusables, IdnaMapping, CjkRadicals. Validated against UCD 17 (461 named seqs, 2339 emoji, 1614 ZWJ, 6565 confusables, 9262 IDNA, 246 CJK radicals).
- **P4 partial** ISO 639-2 parser (`Iso6392Record` + `ParseIso639_2`) + cross-source decomposer integration emitting `has_alternate_name` edges under `library_of_congress` provenance. BCP47 parser (`Bcp47Record` + `ParseBcp47Registry`) covering Type/Subtag/Description/Added/Suppress-Script/Scope/Macrolanguage/Deprecated/Preferred-Value/Prefix fields.
- **P12l partial** 8 memory files capturing durable architectural truths (three-role-physicality, content-trajectories-as-universal-shape, pre-gen-not-substrate-ingestion, broad-unicode-scope, no-modality-specific-attestation-types, no-phase-boundaries-no-backfill, unified-glicko-surface, no-bit-perfect-export); MEMORY.md index extended.

## Phase 1 followups (deferred large refactors)

**P1e-followup** — drop `attestation_type` column from `substrate.edge_significance` + `substrate.entity_significance` hard removal. Schema migration + IIngestionBatch signature + all decomposer call sites + populate functions + struct cleanup. ~6-10h.

**P1f-followup** — INSERT-time inline geom build + Glicko priming (no NULL-geom window even briefly). Rewrite edge drain INSERT-SELECT to compute geom from `pg_temp.edge_member_inflight` JOIN `substrate.entity` composite-btree in one statement; cross-product priming inline. ~8-12h.

**P1g-followup** — classifications as edges (large refactor). Reference vocabulary rows become content-hashed substrate entities; `has_pos` / `has_language` / `has_morph_feature` / `has_deprel_pattern` edges replace `entity_pos` / `pattern_deprel` / `entity_language` / `entity_morph_feature` junctions. UdDecomposer / WiktionaryDecomposer / WordNetDecomposer / OmwDecomposer migrate from `AddJunction` to `AddEdge`. ~12-20h.

## Phase 2 — Tree-sitter universal byte-to-structure decomposer (REFACTOR — happens after Phases 3-7)

Replaces hand-rolled parsers with tree-sitter grammars. Refactor — not foundation. Scheduled AFTER corpora + AI model ingestion proves the pipeline at scale.

- P2a Tree-sitter native binding (`src/Hartonomous.Core/Native/TreeSitterNative.cs`).
- P2b TreeSitterDecomposer class.
- P2c Per-grammar tier-mapping data tables.
- P2d Replace WiktionaryJsonl / UdConllU / TatoebaCsv / Iso639 / Omw / WordNet / ModelConfig / ModelCard parsers.
- P2e Per-language code decomposers (Python / TypeScript / Rust / C / C++ / Java / Go / Ruby / Shell).
- P2f Markup decomposers (HTML / Markdown / LaTeX / AsciiDoc / BibTeX / org-mode).
- P2g Config + IDL decomposers (JSON / YAML / TOML / XML / Protobuf / Thrift / GraphQL / SPARQL / OpenAPI).

**Effort: 30-50h** including grammar libraries vendored / built.

## Phase 3 — Unicode substrate-content completion (LYNCHPIN — full scope)

### XML-flat is canonical per-codepoint source

Use `ucd.all.flat.xml` (NOT grouped) — flat is self-contained per-char with no group-inheritance state machine; parser simplicity wins over the ~2 MB compressed-size advantage of grouped. Rename `ext/libhartonomous/codegen/gen_ucd_grouped.c` → `gen_ucd_flat.c`; drop group-default tracking; extend to emit all ~100 UAX #44 attributes via SAX/iterparse over `<char>` elements.

### P3b-f remaining work (per-family vertical slices)

For each UCD family, the vertical slice is: parser (DONE for 7 non-XML families this session) → emitter (DONE for same 7) → SRF C wrapper (`ext/hartonomous_pg/src/pg_ucd_*_pg.c`) → SQL declaration (`hartonomous--1.0.sql.in`) → populate_unicode_*_from_ext PG function → C# UCD pass (plugs into `UnicodePassOrchestrator`).

XML-flat families (extending `gen_ucd_flat.c` for each, then SRF + populate + C# pass):
- gc, ccc, dt + dm (decomposition), nt + nv (numeric), bc (bidi class), bpt + bpb (paired bracket), Bidi_M, bmg (mirroring), suc/slc/stc/uc/lc/tc/scf/cf (case mapping full), jt/jg (joining type/group — Arabic shaping), ea (east asian width), lb (line break), sc + scx (script + extensions), hst (Hangul syllable type), age, GCB / WB / SB (segmentation), NFC_QC/NFD_QC/NFKC_QC/NFKD_QC + NFKC_CF/NFKC_SCF (normalization quick-check + casefold), InSC (Indic syllabic), InPC (Indic positional), blk, na1 (Unicode 1.0 name), ~50 binary flags (Dash/WSpace/QMark/Radical/Ideo/UIdeo/Hex/...), `<name-alias>` child elements, `<name>` attribute.

Non-XML families (already parsed + emitted this session): NamedSequences, EmojiSequences, EmojiZwjSequences, StandardizedVariants, Confusables, IdnaMapping, CjkRadicals, UCA (`allkeys.txt` already wired).

Plus 13 currently-empty `substrate.edge_type` Unicode rows + new ones (has_collation_weight, has_named_sequence, has_emoji_sequence, has_emoji_zwj_sequence, has_canonical_decomposition, has_compatibility_decomposition, canonical_composes_to, has_full_case_mapping, has_standardized_variant, confusable_with, idna_maps_to, has_bidi_mirroring_glyph, unihan_variant, unihan_reading, unihan_source, has_radical_stroke, has_script_extension, has_indic_syllabic_category, has_arabic_shaping_class, has_idna_status, has_ideographic_variant) wired through edge_type seed + populate functions + C# passes.

**Effort: 25-40h** depending on test depth.

### P3g — multi-version Unicode ingestion (cross-version corroboration)

Ingest all 30 UCD versions in `/vault/Data/Unicode/Public/{ver}/` — each as separate provenance row (`unicode_consortium_v1_1`, `unicode_consortium_v2_0`, ..., `unicode_consortium_v18_0`). Cross-version disagreement accumulates as Glicko events on shared codepoint entities under `unicode_version_consensus` arena. Tracks Unicode's evolution natively. 30 × ~1.1M codepoint attestations = ~33M cross-version events.

**Effort: 8-12h.**

### P3h — ISO 15924 script codes + CLDR locale data

`/vault/Data/Unicode/iso15924/` HTML/.txt extracted to script_name entities tied to UCD `sc`/`scx` attributes. `/vault/Data/Unicode/Public/cldr/` for full CLDR locale data — language ↔ script ↔ region ↔ calendar ↔ number format. Cross-source attestations on `script_name` + `region_name` entities.

**Effort: 5-8h.**

### P3i — IVD (Ideographic Variation Database)

`/vault/Data/Unicode/ivd/` — adobe-japan1 / caaph / hanyo-denshi / krname / moji_joho / msarg per-glyph variant data + image collections (gif/, png/, pri/). Emits `has_ideographic_variant` edges from unified ideograph codepoints to per-variant entities. Image content via image-modality decomposer (depends on P5 image content pipeline; defer pixel_region content if image decomposer not yet ready).

**Effort: 5-10h** (without image decomposer); +10-15h once image decomposer lands.

### P3j — Consortium working documents (L2 + IRG + WG2 + reports + notes + review + errata + standard)

~16K documents totaling ~20 GB. Each = `document` content trajectory via SubstrateTextDecomposer. Extract `has_topic` edges (codepoints / scripts / blocks discussed). Per-document `has_author` / `has_proposal_number` / `has_decision_date` attestation metadata. Tree-sitter HTML/PDF text extraction (depends on Phase 2 tree-sitter universal decomposer for fully automated; manual per-format parsers for initial batch).

**Effort: 15-25h** (post-P2) or 20-30h (pre-P2, with hand-rolled parsers).

### P3k — UTR / UAX / UTS reports

`/vault/Data/Unicode/reports/` — 1886 files (tr1 through tr61). Authoritative text content trajectories with cross-references between annexes (UAX #29 references UAX #14, etc.). Same ingestion shape as P3j; smaller scope.

**Effort: 5-8h.**

### P3l — Charts + visualization PDFs

`/vault/Data/Unicode/charts/` (2221 files) + `/vault/Data/Unicode/emoji/charts*/` (96+ files). Per-block visualization PDFs/HTML. Text content via PDF extraction; image content via image-modality decomposer attaching pixel_region content to relevant codepoint entities via `has_chart_rendering` edges. Multimodal grounding surface.

**Effort: 10-15h** (depends on image decomposer).

## Phase 4 — ISO 639 + language identity completion

Decomposer integrations (parsers DONE for 639-2 + BCP47 this session):
- BCP47 decomposer wire-up with cross-source corroboration on shared alpha-2/alpha-3 codes (parser done; integration ~80 lines).
- ISO 639-5 from `loc/iso639-5.json` (50-80 lines).
- CLDR `supplementalData.xml` language alias data (80-150 lines).
- SIL `change_requests/*.html` (audit-trail only, optional) (50-80 lines).
- LoC RDF (xml/skos.rdf/json variants) (50-80 lines).

All accumulate attestations on `language_name` entities under different provenance rows; `language_identity_consensus` arena tracks cross-source agreement.

**Effort: 5-8h.**

## Phase 5 — Corpus content trajectory completion

- **P5c** Tatoeba complete (12M sentences as content trajectories; depends on P1i AP-19 fix — DONE). Acceptance: `COUNT(*) FROM substrate.entity_classification WHERE entity_type = text_composition AND provenance = tatoeba >= 12M`. ~5-8h.
- **P5e** UD all v2.17 treebanks (each language separate provenance). Sentence trajectories via `AddIngestionTrajectory` (DONE in P1c). Per-token `has_pos` / `has_morph_feature` / `has_deprel` edges (depends on P1g-followup). ~8-12h.
- **P5d** OMW cross-lingual content (gloss/definition text in 100+ languages as content trajectories per language; cross-lingual `aligned_to_synset` edges accumulate per-language attestations). ~6-10h.
- **P5f** WordNet complete with cross-source corroboration via OMW + Wiktionary. ~5-8h.
- **P5a-b** Wiktionary content trajectories (citation/etymology/definition/example sentences as text_compositions via SubstrateTextDecomposer; attach via has_citation/has_etymology/has_definition/has_example edges). Tree-sitter JSONL parsing depends on P2 tree-sitter decomposer; can use existing hand-rolled WiktionaryJsonlParser meanwhile. ~10-15h.

**Effort: 35-55h.**

## Phase 6 — AI model ingestion

- P6a SafetensorsContainerDecomposer scope-narrowed to layout + dispatch.
- P6b Per-tensor dtype-decode lossless to f64 (BF16/F32/F64/AWQ-Q4/GGUF/FP8).
- P6c Per-architecture TupleResolver tables (data, not code) mapping HF tensor names to `(primitive, tuple, tuple-slot)` per `docs/01-tensor-primitive-spec.md`.
- P6d 4 primitive passes (Linear, LocalKernel, Normalization-DONE, Lookup).
- P6e 14 tuple-attestation passes (AttentionBlock, SwiGluFfn, BertFfn, MoeRouter, MoeExpert, EmbeddingLookup, LmHead, LoraDelta, CrossAttention, ConvResidual, Conformer, SwinWindowAttn, PatchEmbed, DetectionHead, BnState, VaeAttnBlock).
- P6f Sign-preserving Glicko emission per AP-31.
- P6g Per-tensor adaptive magnitude floor per AP-33 (threshold-only LTH; no top-K).
- P6h Direct weight decomposition only per AP-34 (no synthetic prompts, no GPU at ingest).
- P6i AWQ-Q4 cross-precision verification (Qwen2.5-Coder F32 + AWQ produce identical edge_hash).
- P6j Multi-model corroboration validation (Llama + Qwen + DeepSeek + Florence + CLIP + Whisper + MusicGen ingest verifies cross-model Glicko sigma tightening on shared edge identities).

**Effort: 40-60h.**

## Phase 7 — Firefly Procrustes alignment

- P7a EmbeddingAlignmentPass per AP-35 / build-plan #51.
- P7b `substrate.embedding_alignment_anchor` reference table.
- P7c SQL surfaces (`get_firefly_coords`, `apply_firefly_rotation`, `claim_or_get_embedding_anchor`).
- P7d Native procrustes.c + C# `ProcrustesAlign` binding.

Unblocks Mode 1 Build-a-bear embedding synthesis.

**Effort: 8-12h.**

## Phase 8 — Build-a-bear synthesis recomposer

- P8a Delete `SafetensorsRecomposer.AssembleTensorBytesAsync:239-373` phantom-scatter dead code.
- P8b `TargetArchitectureSpec` data model (fully arbitrary architecture).
- P8c `RecompositionOptions` (arena weighting, significance threshold, source filter, quantization target, recipe identifier).
- P8d 9 layer-type synthesizers (AttentionQkv, AttentionVo, Ffn, Embedding, LmHead, LayerNorm, MoeRouter, MoeExpert, LoRAAdapter) + specialist synthesizers (Conv, ViTPatch, CodecRVQ, DetectionHead, CrossAttention, DiffusionUnet).
- P8e Honest abstention (under-attested cells stay zero).
- P8f Standard safetensors output HF/vLLM/llama.cpp compatible.
- P8g Determinism per spec §XI.2 (relaxed synthesis-time; opt-in probabilistic synthesis).

**Effort: 25-40h.**

## Phase 9 — Inference engine + Gödel orchestrator

- P9a substrate.infer/infer_topk fixes per docs/audit/flow-inference.md GAP-1 through GAP-8.
- P9b Gödel Engine OODA three scales (micro/meso/macro).
- P9c Reasoning patterns emerge from OODA (CoT/ToT/Reflexion/ReAct/Self-Consistency/GoT/hypothesis-driven).
- P9d Operational boundaries.
- P9e Frayed-edge surveys + ingestion proposal generation.

**Effort: 25-35h.**

## Phase 10 — Crystal Ball analytics + visualization

- P10a-m 13 analytics caches (materialized views, rebuildable).
- P10n SQL primitives (mech interp / bias audit / capability tomography / provenance + contamination + theft detection / hallucination diagnosis / marketplace economics).
- P10o Visualization primitives (4D mantissa-packed content_trajectory bbox query → 2D/3D projection with provenance/arena/corroboration/temporal coloring; firefly Voronoi cell viz; frayed-edge atlas heatmap; cross-model architectural similarity dendrogram).

**Effort: 30-45h.**

## Phase 11 — Live ingest + user prompt absorbent

- P11a User prompts as content trajectories via SubstrateTextDecomposer with `user_session` provenance.
- P11b Conversation history accumulates as substrate content; future inference traverses via `has_source` edges to prior turns.
- P11c Uploaded documents (PDF after text extract, Markdown, code repos, image+caption pairs, audio recordings) ingest via tree-sitter / UAX29 / per-modality decomposers.
- P11d Real-time Glicko-2 priming.
- P11e Session-scoped arenas.

**Effort: 15-25h.**

## Phase 12 — Rules + docs + memory artifact alignment (continuous)

Update across:
- `.claude/rules/{00,10,15,20,25,30,35,40,45}-*.md`
- Root `CLAUDE.md` + `.claude/CLAUDE.md`
- `.github/copilot-instructions.md` + `.github/instructions/*.instructions.md`
- `.claude/agents/*.md` + `.github/agents/*.agent.md`
- `.claude/skills/hartonomous-semantic-eval/{SKILL,cases,rubric}.md`
- `/home/ahart/.claude/projects/-home-ahart-Projects-Hartonomous-001/memory/*.md` (8 new memory files written this session; index updated)
- `docs/00-substrate-spec.md` + `docs/01-tensor-primitive-spec.md` + `docs/familiar-principle.md`

**Effort: 15-25h** total, spread across sessions as architectural truths consolidate.

## Dependency-respecting execution order

1. **P1 followups (E + F + G)** — foundation cleanup. Independent of each other; each ~6-20h.
2. **P3 + P4 (Unicode + ISO lynchpin)** — LOAD-BEARING. Core (UCD per-codepoint + missing edges + ISO 639 multi-source) = ~40-65h; full scope including L2/IRG/WG2/IVD/CLDR/charts = 100-160h. Most of this parallelizable across streams.
3. **P5 (corpus content trajectories)** — depends on Unicode floor being solid. Tatoeba can validate AP-19 fix at scale immediately.
4. **P7 (Procrustes alignment)** — small, unblocks Mode 1 Build-a-bear.
5. **P6 (AI model ingestion)** — accumulates per-cell attestation surface on corpus-grounded content entities.
6. **P11 (live ingest)** — integrates everything.
7. **P2 (tree-sitter universal decomposer)** — refactor; replaces hand-rolled parsers after pipeline proven at scale.
8. **P8 (Build-a-bear)** — synthesis from accumulated consensus.
9. **P9 (inference engine + Gödel)** — fixes GAP-1 through GAP-8.
10. **P10 (Crystal Ball)** — analytics + visualization.
11. **P12 (rules + docs + memory)** — continuous side-channel alongside everything.

**Total honest effort: ~280-430h focused work.** ~8-12 weeks dedicated.

Critical-path-trimmed (just to functional substrate + Build-a-bear): P1 followups + P3 core + P4 core + P5c + P7 + P6 core + P8 core = ~150-200h, ~4-6 weeks dedicated.

## Critical files (where the work lands)

### Schema
- `sql/schema/bootstrap.sql` (include manifest)
- `sql/schema/seed/attestation_type.sql` (P1d done)
- `sql/schema/tables/core/entity_significance.sql` + `edge_significance.sql` (P1e column drop)
- `sql/schema/seed/edge_type.sql` (P1g + P3 new edge types)
- `sql/schema/tables/reference/embedding_alignment_anchor.sql` (P7b)

### Pipeline
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` (P1f-followup inline edge geom + Glicko priming)
- `src/Hartonomous.Engine/Ingestion/IngestionBatch.cs` (P1b done; P1g extensions)
- `src/Hartonomous.Core/Ingestion/IIngestionBatch.cs` (P1g classification edge methods)

### Decomposers
- `src/Hartonomous.Decomposers/Ud/UdDecomposer.cs` (P1c done; P1g classification edges)
- `src/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs` (P1c done; P5f content trajectories + P1g classification edges)
- `src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs` (P5a content trajectories + P1g)
- `src/Hartonomous.Decomposers/Tatoeba/TatoebaDecomposer.cs` (P1i done; P5c complete)
- `src/Hartonomous.Decomposers/Omw/OmwDecomposer.cs` (P5d content)
- `src/Hartonomous.Decomposers/Iso639/Iso639Decomposer.cs` (P4 ISO 639-2 done; remaining sources)
- `src/Hartonomous.Decomposers/Iso15924/Iso15924Decomposer.cs` (new — P3h)
- `src/Hartonomous.Decomposers/Ucd/UnicodePassOrchestrator.cs` (P3 new passes)
- `src/Hartonomous.Decomposers/Safetensors/Passes/NormalizationPrimitivePass.cs` (P1c done)
- `src/Hartonomous.Decomposers/Safetensors/Passes/EmbeddingLookupTuplePass.cs` (P6e AddFireflyPoint migration)
- New per-tuple passes in `src/Hartonomous.Decomposers/Safetensors/Passes/` (P6e — 14 files)
- New `src/Hartonomous.Decomposers/TreeSitter/TreeSitterDecomposer.cs` (P2b)
- New `src/Hartonomous.Decomposers/Unicode/` decomposers for L2 / reports / charts / IVD content trajectories (P3i-l)

### Recomposers
- `src/Hartonomous.Recomposers/SafetensorsRecomposer.cs` (P8a delete dead path)
- New per-layer-type synthesizers (P8d — 9 universal + 6 specialist)

### Pre-gen
- `scripts/build/generate_unicode_tables.py` (P3a done; P3b partial done; XML-flat refactor optional — most per-codepoint work lives in C codegen)
- `ext/libhartonomous/codegen/gen_ucd_flat.c` (rename from gen_ucd_grouped.c; extend to all UAX #44 attributes via XML-flat walker)
- `ext/hartonomous_pg/src/generated/pg_ucd_*.{c,h}` (auto-generated tables — commit after pre-gen runs)
- `ext/hartonomous_pg/sql/hartonomous--1.0.sql.in` (SRF declarations)

### Native
- `ext/libhartonomous/src/procrustes.c` (P7d)
- `src/Hartonomous.Core/Native/TreeSitterNative.cs` (P2a)
- `src/Hartonomous.Core/Compute/Ingestion/ProcrustesAlign.cs` (P7d)

### Inference + Gödel
- `sql/schema/functions/infer.sql` + `infer_topk.sql` (P9a recipe DSL)
- `src/Hartonomous.Engine/Inference/SubstrateInferenceEngine.cs` (P9a path chain + inference_trace)
- `src/Hartonomous.Engine/Godel/GodelEngine.cs` (P9b-e)

### Rules / docs / memory (P12)
- `CLAUDE.md` (root) — partial done
- `.claude/CLAUDE.md`
- `.claude/rules/{00,10,15,20,25,30,35,40,45}-*.md` — partial done across sessions
- `.claude/agents/*.md` — partial done
- `.claude/skills/hartonomous-semantic-eval/{SKILL,cases,rubric}.md` — partial done
- `.github/copilot-instructions.md` — partial done
- `.github/instructions/*.instructions.md`
- `.github/agents/*.agent.md` — partial done
- `/home/ahart/.claude/projects/-home-ahart-Projects-Hartonomous-001/memory/*.md` — 8 new files this session; MEMORY.md index extended

### Existing functions / utilities to reuse
- `MantissaPacking.PackHashLo / PackHashHi / PackOrdinalRle / PackMetadata`
- `TrajectoryVertex.FromHash`
- `Geometry4dPayloadBuilder.Point / LineString`
- `Point4D.TryMean`
- `Blake3.Hash32 / ComputeAtomicStringHash / ComputeEdgeHash / HashPrefix104`
- `SubstrateTextDecomposer.EmitStatic` — canonical text path for all text content trajectories
- `substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])` — composite-btree reverse-resolve
- `substrate.get_composition_children`, `composition_at`, `composition_range`, `composition_subtrajectory`, `composition_parents`, `recompose_text` — read path for mantissa-packed trajectories
- `substrate.bb_pack_*` / `bb_unpack_*` — SQL mantissa helpers
- `populate_codepoint_atoms` + `populate_codepoint_property_range_from_ext` + `populate_unicode_case_edges_from_properties` — template for new populate functions
- `ParallelChunkProcessor.RunAsync` — parallel decomposer dispatch
- Existing `ucd_codepoints()` SRF + 12 BlobUcdPropertyAccessor exports — template for new SRFs

## Verification

Per-priority acceptance criteria are concrete SQL or build/test gates:

- **P1a**: `scripts/hart build extension-sql && scripts/hart db bootstrap`; `SELECT code FROM substrate.physicality_type WHERE code IN ('entity_shape', 'ingestion_trajectory')` = 2 rows.
- **P1c**: integration test ingest UD sample → `SELECT count(*) FROM substrate.physicality WHERE physicality_type_id = 16` > 0; `SELECT count(*) FROM substrate.get_composition_children(some_sent_hash)` returns sentence token count.
- **P1f**: ingest corpus, assert `COUNT(*) FROM substrate.edge WHERE geom IS NULL = 0` throughout.
- **P1g**: `SELECT count(*) FROM substrate.edge WHERE edge_type = has_pos` accumulates as UD/Wiktionary/WordNet ingest.
- **P1i**: ingest Tatoeba, assert producer-side entity emission count / unique hash count < 2.
- **P3**: `substrate.ucd_materialization_counts()` returns expected counts for all 17+ Unicode edge types; cross-version corroboration test verifies multi-source attestations on shared codepoint entities.
- **P4**: shared alpha-3 code (e.g., `eng`) lands on same `language_name` entity with `ParseIso639_2` + `ParseBcp47Registry` + CLDR provenance rows attached.
- **P5c**: 12M+ Tatoeba sentences as text_composition entities under `tatoeba` provenance.
- **P6i**: Qwen F32 + AWQ ingest produces overlapping edge_hash set on shared token pairs.
- **P7**: post-Procrustes cluster centroid for shared anchor tokens has tight sigma.
- **P8**: Build-a-bear synthesize Qwen-2.5-Coder-7B-arch → HF transformers forward pass → non-NaN logits.
- **P9a**: recipe DSL JSONB threading test — pass recipe with per-hop arena filter, observe arena-scoped traversal in inference_trace.

End-to-end demo: user ingests a multi-page Markdown document via tree-sitter → asks "explain the architecture" → `substrate.infer_topk` returns recomposed text answer traced through ingested document's content trajectories + cross-corroborated by ingested AI models' attestations + classification consensus from corpora, with full audit chain in `inference_trace` composition entity. All grounded in the Unicode + ISO foundational attestation surface.
