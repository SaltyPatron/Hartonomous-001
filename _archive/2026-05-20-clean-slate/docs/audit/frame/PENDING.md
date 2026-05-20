# Pending — surfaces I have not yet read

Reading queue (Phase B continues). Anything here means the audit-frame is incomplete for that surface; new findings update the relevant per-area `frame/*.md` or create a new one.

## Doc-read backlog

### docs/ root
- `docs/substrate-bond.md` (partially absorbed; full read needed; user noted name/framing is borrowed metaphor, not foundation)
- `docs/architecture.md` — **DONE 2026-05-19** (lines 1-688; contains deprecated details flagged in AUDIT-STATUS)
- `docs/build-plan.md`
- `docs/index.md`
- `docs/glossary.md`
- `docs/flow-inventory.md`
- `docs/type-system.md`
- `docs/README.md`

### docs/10-architecture/
- `00-overview.md` — **DONE 2026-05-19** (three pillars: Identity / Geometry / Significance; aligns with current rule 25)
- `02-identity-and-convergence.md`
- `03-geometry-4d.md`
- `04-significance-glicko.md`
- `05-decomposer-contract.md`
- `06-recomposer-contract.md`
- `07-inference-engine.md`
- `09-capability-reinvention-catalog.md` (skimmed earlier; full read needed)

### docs/20-technical/ (22 files)
- Schema reference, native API, per-decomposer docs (text/code/model/modality/seed), cognitive functions catalog, arenas catalog, edge types catalog, entity types catalog, provenance catalog, UCD inventory, seed expansion roadmap, tree-sitter grammar strategy, Glicko mechanics, 4D operators, Super-Fibonacci, A* bulk-fetch SPI, per-recomposer docs

### docs/30-operations/ (3 files)
- Deployment, monitoring, backup-recovery

### docs/40-process/ (11 files including 7 checklists)
- Development standards, anti-patterns stub, validation gates, implementation roadmap, 7 per-feature checklists

### docs/50-reference/ (2 files)
- Glossary, data-asset-paths

### docs/60-status/ (3 files)
- Implementation status, known issues, decisions log

### docs/90-appendix/ (4 files)
- the substrate context (partially absorbed; full read), related work, FAQ, bibliography

### docs/audit/
- `flow-inference.md` (existing audit doc)

### docs/recipes/ (23 files)
- vertical-slice, fresh-setup, add-{entity-type, edge-type, physicality-type, junction-table, reference-table, provenance-class, decomposer, analysis-pass, recomposer, sql-function, sql-procedure, migration, native-operator, pinvoke-surface, governance-rule, test, cli-command, phase, layer-type-decomposer, layer-type-synthesizer}, README

### docs/reference/ (5 files)
- allowed-dependencies, anti-patterns stub, file-layout, naming, README

### docs/specs/csharp/ (11 files)
- analysis-passes, api-layer, base-classes, compute-facade, decomposers, error-handling, ingestion-pipeline, interfaces, phase-runner, project-structure, recomposers

### docs/specs/decomposers/ (11 files — per-corpus)
- analysis-passes
- iso639 (pending)
- layer-type-library (pending)
- omw (pending)
- safetensors — **DONE 2026-05-19** (two-track ingestion, 12 architecture classes, distillation = NEW student model; phantom edge type names in consolidated table are deprecated per AP-25)
- tatoeba (pending)
- tokenizers (pending)
- ucd-uca — **DONE 2026-05-19** (full UCD source inventory, per-codepoint entity model, S³ projection formula)
- ud — **DONE 2026-05-19** (339 treebanks, CoNLL-U 10-column, 17 UPOS + 70+ DEPREL + 68+ morph features; phantom ud_sentence/ud_token entity types are deprecated)
- wiktionary — **DONE 2026-05-19** (20.4GB JSONL streamed; per-entry word/lang/pos/senses/forms/sounds/etymology/translations/relations; Wikidata preserved; phantom wikt_sense type deprecated)
- wordnet — **DONE 2026-05-19** (full WordNet 3.0 source format; 25+ pointer types; 35 verb frames; sense frequency; 45 lexnames; trust prior High)

### docs/specs/engine/ (7 files)
- arenas-and-significance (DONE), embedding-physicality (DONE), generation-and-transformation (DONE), godel-engine (DONE), inference (DONE), multi-model-perspective-query (DONE), substrate-governance (DONE)

### docs/specs/modalities/ (4 files)
- audio, image, text, video

### docs/specs/native/ (7 files)
- 4d-type-and-index, build-system, compute-library, geometry4d-composition, pg-extension, shared-library, synthesis-hardware-integration

### docs/specs/operations/ (5 files)
- configuration, deployment, monitoring, sessions, testing

### docs/specs/recomposers/ (4 files)
- algorithms/embedding-synthesis-from-fireflies, algorithms/ffn-kv-inversion, algorithms/lottery-ticket-foundations, synthesis-library

### docs/specs/sql/ (12 files)
- domains-and-types, functions, indexing, infrastructure-vs-substrate, junction-tables, mantissa-exploitation (DONE), migrations, partitioning, reference-tables, seed-scripts, stored-procedures, views

### docs/specs/ (top)
- seed-strategy, text-decomposer-unification

### docs/standards/ (10 files)
- ai-agent-workflows, configuration-and-errors, csharp-conventions, dependency-injection, design-principles, ingestion-pipeline, native, README, sql, testing

### docs/00-business/ (8 files)
- vision (partially absorbed; full read needed), product-line, market-positioning, customer-segments, pricing-model, go-to-market, competitive-moats, risk-register

### .claude/
- agents/{hartonomous-implementer,hartonomous-planner,hartonomous-reviewer,hartonomous-semantic-auditor}
- skills/hartonomous-semantic-eval/{cases,rubric,SKILL}

### Memory (cross-validate with current substrate state)
- 27 files under `/home/ahart/.claude/projects/.../memory/`

## Phase C — source reading
- `sql/schema/bootstrap.sql` + all included files
- `src/Hartonomous.*/` end-to-end
- `ext/libhartonomous/src/` + `ext/hartonomous_pg/src/` end-to-end

## Estimated overall progress
- Read end-to-end against current code/rules: ~38 docs (architecture-load-bearing core + 5 decomposer specs + 2 infra/native specs + 00-overview)
- Pending: ~175 docs + source code
- AUDIT-FRAME modularization: 29 / 29 per-area files written + PENDING (modularization complete)
- frame/30 (DECOMPOSER-MULTI-SINK-ARCHITECTURE) flagged as suspect — tree-sitter recommendation for UCD XML codegen is unfounded pattern-match; correct shape is shared C parser library in libhartonomous called from both build-time codegen and runtime C# decomposer via Hartonomous.Core.Compute.* facade. Needs rewrite or deletion at Phase D.
- Several frame docs need Procrustes correction overlay: substrate's S³/Super-Fibonacci/UCA-rank-derived word_form centroids ARE the canonical anchor frame; AI model fireflies project IN via Laplacian eigenmap + Procrustes alignment to substrate's canonical positions (NOT to "first model wins" frame as currently misframed in frame/06).
- docs/architecture.md and docs/specs/native/geometry4d-composition.md have deprecated implementation details (substrate.sequence table; dual-type GEOMETRY4D + GeometryZM 3-column physicality shape; phantom ud_sentence/ud_token/wikt_sense/bpe_token/attention_pattern entity types) overridden by rule 15 + rule 25 + 2026-05-08 phantom entity correction.
