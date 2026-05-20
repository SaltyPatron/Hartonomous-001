# Implementation Roadmap

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineering leadership, contributors, anyone tracking what's done and what's next.

---

## Roadmap principles

The roadmap is structured to avoid repeating the failure modes of Fail_A (extensive infrastructure before any inference loop closed) and Fail_B (24-hour-pipeline iteration cycles). Each milestone has:

- **Goal**: what is mechanically possible after this milestone
- **Inputs**: doc references and prerequisites
- **Outputs**: concrete artifacts (code, schema, decomposers, recomposers, validation gates passing)
- **Gate**: the SQL or test command that proves milestone completion
- **Anti-criteria**: what does NOT count as completion

Milestones are sequential where dependencies require it; otherwise parallelizable.

The first commercial deliverable (refinement-as-service for an ingested model + Laplace-Linguistics original) requires all milestones M1 through M9.

---

## M0 — Foundations (PostgreSQL + native extension shell)

**Goal:** Postgres 18 + PostGIS 3.6+ + hartonomous_pg extension installable on a clean machine. Schema migrations apply cleanly. Native types exist.

**Inputs:** `10-architecture/00-overview.md`, `20-technical/00-schema-reference.md`, `20-technical/01-native-extension-api.md`.

**Outputs:**
- `ext/hartonomous_pg/` C/C++ extension building via CMake/PGXS
- `ext/hartonomous_pg/sql/hartonomous_pg--1.0.sql` defining types, GiST opclasses, function declarations
- `schema/migrations/00*` SQL migrations through reference table seed
- Docker compose config running PG 18 + PostGIS + hartonomous_pg
- CI workflow building the extension, applying migrations, running gates F1–F5

**Gate:**
```bash
docker compose up -d
psql -c "CREATE EXTENSION hartonomous_pg;"
# F1, F2, F3, F4, F5 all pass per validation-gates.md
```

**Anti-criteria:** "It builds." Building isn't enough. F1–F5 must pass.

---

## M1 — Identity layer (BLAKE3 + entity/edge insert)

**Goal:** Substrate accepts content via the canonical identity functions; entities and edges with deduplicated content addressing.

**Inputs:** `10-architecture/02-identity-and-convergence.md`, `20-technical/00-schema-reference.md`.

**Outputs:**
- `hartonomous.atom_id`, `hartonomous.composition_id`, `hartonomous.edge_id` C functions, all passing BLAKE3 official test vectors
- Pipeline interface (Python or C# or Rust — the orchestrator language is up to engineering preference, but it must integrate with Postgres binary COPY)
- `staging.*` tables and flush procedures
- Tests verifying ON CONFLICT DO NOTHING dedup under concurrent insert

**Gate:**
```sql
-- Insert the same hash twice; expect one row
SELECT count(*) FROM substrate.entity WHERE hash = $known_test_hash;
-- Expected: 1
```

Plus convergence gate S1 from validation-gates.md.

**Anti-criteria:** Per-row INSERT loops; inline SQL in app code; dedup logic outside `ON CONFLICT`.

---

## M2 — UCD/UCA seed (codepoint atoms with S³ positions)

**Goal:** All ~150K assigned Unicode codepoints exist as atoms in the substrate, with deterministic 4D positions on S³ via UCA Super-Fibonacci spiral, plus their codepoint_property junction rows.

**Inputs:** `D:\Models\UCD\Public\UCD\latest\ucdxml\ucd.all.flat.xml`, `D:\Models\UCD\Public\UCD\latest\uca\allkeys.txt`, `10-architecture/03-geometry-4d.md` § "Codepoint atoms on S³".

**Outputs:**
- `UcdUcaDecomposer` implementation (streaming XML + allkeys parser)
- All codepoint atoms inserted with `point4d` physicality
- All codepoint_property junction rows populated
- `canonical_decomposition_of`, `case_folds_to`, `case_maps_to_lowercase`, `case_maps_to_uppercase` edges populated from UCD's mapping data
- Validation gate D1 (determinism) passing for the UCD decomposer

**Gate:**
```sql
SELECT count(*) FROM substrate.entity
  WHERE entity_type_id = (SELECT id FROM ref.entity_type WHERE code='codepoint');
-- Expected: ~150,000 (or full Unicode space ~1.114M if sparse range covered)

SELECT count(*) FROM substrate.physicality p
  WHERE physicality_type_id = (SELECT id FROM ref.physicality_type WHERE code='s3_codepoint');
-- Expected: matches entity count

SELECT count(*) FROM junc.codepoint_property;
-- Expected: matches entity count

SELECT count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
 WHERE et.code = 'canonical_decomposition_of';
-- Expected: > 0 (UCD decomposition mappings)
```

**Anti-criteria:** Eagerly priming significance for codepoint atoms; this is overhead. Codepoint atoms have no significance arena state initially.

---

## M3 — Text decomposer (universal text path)

**Goal:** UTF-8 text → codepoint atoms → grapheme clusters → words → sentences → paragraphs → text_compositions, all with linestring4d trajectories. NFC normalization at decomposer entry. UAX #29 segmentation.

**Inputs:** `20-technical/02-text-decomposer.md` (TBD), `10-architecture/05-decomposer-contract.md`.

**Outputs:**
- `text_decompose` SQL function exposed via cognitive surface
- Decomposer that produces grapheme_cluster, word_form, lemma (when lemma reference exists), text_composition entities
- Linestring4d physicality for each composition tier
- Validation gates D1–D6 passing for the text decomposer
- The "café NFC vs NFD" test case passes per `10-architecture/02-identity-and-convergence.md`

**Gate:**
```sql
-- Decompose a multilingual test corpus
SELECT hartonomous.text_decompose(convert_to('Hello', 'UTF8'), $provenance_id);

-- Verify grapheme cluster count >= codepoint count for non-ASCII (combining marks should fold)
-- Verify NFC form 'café' (U+00E9) and NFD form (U+0065 + U+0301) both produce the SAME text_composition
SELECT count(DISTINCT hash) FROM substrate.entity
  WHERE entity_type_id = (SELECT id FROM ref.entity_type WHERE code='text_composition')
    AND hash IN ($nfc_hash, $nfd_hash);
-- Expected: 1 (after NFC normalization, both decompose to the same canonical sequence)
-- BUT: at the codepoint atom level, U+00E9 and U+0065 + U+0301 are different atoms linked by canonical_decomposition_of edges
```

This is the foundational decomposer. Every other text-bearing decomposer routes through this.

---

## M3.5 — Tree-sitter + Kaitai decomposer infrastructure

**Goal:** the decomposer contract has its canonical implementations. Tree-sitter parses text-format input; Kaitai Struct parses binary formats; both produce the substrate-shaped typed AST. Subsequent decomposer milestones become "author the grammar + mapping function," not "write a custom parser."

**Inputs:** `10-architecture/05-decomposer-contract.md`, `20-technical/16-tree-sitter-grammar-strategy.md`.

**Outputs:**

- Tree-sitter library bound into the substrate's decomposer pipeline (Python bindings + C bindings via the native extension)
- Kaitai Struct support for binary formats
- Base `TreeSitterDecomposer` and `KaitaiDecomposer` classes with substrate-emit helpers (`emit_entity`, `emit_edge`, `emit_junction`, `decompose_text`, etc.)
- Pattern-A decomposer framework (flat triples / TSV / CSV / JSONL with column registry)
- Pattern-B decomposer framework (CoNLL+ multilayer with annotation column registry)
- First three custom grammars authored:
  - `tree-sitter-conllu` — foundation for UD ingestion (M5) and many later corpora (PROIEL, Vedic, GUM, PreCo, CoNLL-2003, WikiNER, Few-NERD, PARSEME-CUPT)
  - `tree-sitter-ucd-properties` — foundation for UCD seed (M2)
  - `tree-sitter-conceptnet-csv` — first Tier-1 expansion target (post-M9)
- Validation tests: parse representative samples from each format, assert substrate emit produces expected entity/edge counts

**Gate:**

```python
parser = TreeSitterDecomposer("tree-sitter-ucd-properties")
ast = parser.parse(open("D:/Models/UCD/Public/UCD/latest/ucd/UnicodeData.txt", "rb").read())
records = parser.map_to_substrate(ast, provenance_id=unicode_consortium_id)
assert len(records.entities) > 0  # codepoint atoms produced
assert len(records.edges) > 0     # at least canonical_decomposition_of edges
```

Plus: every grammar passes its own conformance tests (tree-sitter-conllu correctly parses a UD corpus into a hand-verified expected AST shape).

**Anti-criteria:** "Tree-sitter compiles and links." That is not enough — must parse representative substrate inputs and emit substrate records correctly.

This milestone sits between M3 (text decomposer's universal pipeline) and M4 (WordNet/OMW seeds). Without M3.5, every subsequent decomposer is a multi-week custom parser. With M3.5, every subsequent decomposer is a grammar + mapping function (~hours to days).

---

## M4 — ISO 639 + WordNet + OMW (the lexical backbone)

**Goal:** Language reference vocabulary populated. WordNet synsets, lemmas, hypernym/hyponym/meronym/holonym/antonym edges, has_gloss/has_example edges. OMW grafts other languages onto WordNet's synset spine.

**Inputs:** `D:\Models\ISO639`, `D:\Models\princeton-wordnet`, `D:\Models\omw`. `10-architecture/05-decomposer-contract.md`.

**Outputs:**
- `Iso639Decomposer` populating ref.language
- `WordNetDecomposer` populating synsets, lemmas, has_sense, hypernym/etc., entity_pos, entity_sense
- `OmwDecomposer` populating multilingual lemmas via text_decompose, aligned_to_synset edges, entity_language
- All decomposer gates D1–D6 passing
- Convergence gate: a sentence appearing in WordNet's gloss AND in Tatoeba (after Tatoeba's M9) lands at the same text_composition entity

**Gate:**
```sql
SELECT count(*) FROM substrate.entity
  WHERE entity_type_id = (SELECT id FROM ref.entity_type WHERE code='synset');
-- Expected: ~117,000 (Princeton WordNet synset count)

SELECT count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
 WHERE et.code = 'hypernym';
-- Expected: > 70,000 (WordNet's hypernym pointer count)

-- WSD test: 'bank' should have multiple senses
SELECT count(*) FROM junc.entity_sense WHERE entity_hash = (
    SELECT hash FROM substrate.entity
    WHERE entity_type_id = $word_form_type
    AND hash = (SELECT hartonomous.text_decompose(convert_to('bank', 'UTF8'), $provenance))
);
-- Expected: >= 5 senses
```

---

## M5 — UD treebanks (syntactic skeleton)

**Goal:** All UD treebanks ingested. POS, deprel, morph_feature reference vocabularies populated. dep_* edges between word_form entities. Sentence text routes through text_decompose.

**Inputs:** `D:\Models\ud-treebanks` (.conllu files).

**Outputs:**
- `UdDecomposer` implementation
- All decomposer gates passing
- Convergence: WordNet's gloss text and UD's sentence text that share content fold to one text_composition

**Gate:**
```sql
SELECT count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
 WHERE et.category = 'syntactic';
-- Expected: tens of millions across all treebanks

SELECT count(*) FROM ref.deprel;
-- Expected: 70+ (universal + subtypes)
```

---

## M6 — A\* traversal + Glicko-2 update + cognitive surface (inference loop)

**Goal:** End-to-end inference works on the substrate's accumulated state. `hartonomous.inference.converse` returns coherent paths with explanation traces. Outcome events drive Glicko updates correctly.

**Inputs:** `10-architecture/07-inference-engine.md`, `10-architecture/04-significance-glicko.md`, `10-architecture/08-cognitive-surface.md`.

**Outputs:**
- `traverse_astar` C function with bulk-fetch SPI
- `glicko2_update` C function passing Glickman paper's worked example
- Cognitive surface functions: `inference.converse`, `inference.outcome`, `inference.replay`, `lexical.senses_of`, `lexical.hypernym_chain`, etc.
- Recipe DSL parsing
- All cognitive function gates passing

**Gate:**
```sql
\timing on
SELECT response_text, paths, elapsed_ms FROM hartonomous.inference.converse('What is a cat?');
-- Expected: response_text non-empty
--           paths has at least one path with traceable provenance
--           elapsed_ms < 100 (cold cache); < 10 (warm)

-- Outcome event moves mu
SELECT mu FROM substrate.edge_significance WHERE edge_hash = $some_path_edge;
-- Save value as mu_before
SELECT hartonomous.inference.outcome($response_id, 'accept');
SELECT mu FROM substrate.edge_significance WHERE edge_hash = $some_path_edge;
-- Expected: mu has changed (>mu_before for selected edges, possibly <mu_before for rejected)
```

This milestone closes the inference loop. Until M6, the substrate is a knowledge representation; after M6, it's a working AI.

---

## M7 — First model ingestion (refinement substrate primer)

**Goal:** Ingest one mid-size LLM (recommended: Qwen2.5-Coder-3B at 5.8GB — small enough to iterate fast, big enough to be commercially relevant). Validate that the model's edges land in the substrate alongside curated edges and that cross-source corroboration begins.

**Inputs:** `D:\Models\hub\models--Qwen--Qwen2.5-Coder-3B-Instruct`, `10-architecture/05-decomposer-contract.md`, model-specific decomposer doc (TBD).

**Outputs:**
- `SafetensorsDecomposer` implementation handling decoder-only transformer architecture
- Tokenizer compositions converging with substrate's existing text_composition entities
- Embedding-tier physicality (both 4D fireflies for cross-model comparison AND native-dim for distillation)
- Track-2 transformation/beaten_path/embedding_similarity edges with sub-provenance `huggingface_model:qwen2.5-coder-3b`
- Per-tensor (layer, role) metadata preserved on edges for refinement
- All decomposer gates passing for the safetensors decomposer

**Gate:**
```sql
SELECT count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
  JOIN ref.provenance p ON p.id = e.provenance_id
 WHERE p.code = 'huggingface_model:qwen2.5-coder-3b'
   AND et.code IN ('beaten_path', 'transformation', 'embedding_similarity');
-- Expected: large count (millions, given the model size)

-- Cross-source corroboration: edges between vocab entities that exist in BOTH the model
-- and curated sources should have above-default mu in semantic_relevance arena
SELECT avg(mu) FROM substrate.edge_significance s
  JOIN substrate.edge e ON (s.edge_type_id, s.edge_hash) = (e.edge_type_id, e.hash)
  JOIN ref.significance_context sc ON sc.id = s.context_type_id
 WHERE sc.code = 'semantic_relevance'
   AND s.games > 0;  -- only edges that have seen Glicko activity
-- Expected: > 1500 (mean rises with corroboration)
```

---

## M8 — Recomposer engine (refinement output)

**Goal:** Recompose ingested Qwen2.5-Coder-3B back to a refined safetensors file. Output deploys to vLLM/llama.cpp/transformers.

**Inputs:** `10-architecture/06-recomposer-contract.md`.

**Outputs:**
- `SafetensorsRecomposer` implementation handling decoder-only transformer
- Per-tensor projection rules (Q/K/V/O attention, gate/up/down FFN, embedding, LM head, layer norm, position encoding)
- Sparse projection with significance threshold
- Output safetensors directory with valid config.json, tokenizer.json, model.safetensors (or shards)
- All recomposer gates R1–R6 passing

**Gate:**
```bash
# Recompose
psql -c "SELECT hartonomous.recompose.refine_model('huggingface_model:qwen2.5-coder-3b', '/tmp/refined-qwen', 0.6);"

# Loadability test
python -c "from transformers import AutoModelForCausalLM; m = AutoModelForCausalLM.from_pretrained('/tmp/refined-qwen'); print('loaded')"
# Expected: prints "loaded"

# Sample-prompt test
python -c "
from transformers import AutoModelForCausalLM, AutoTokenizer
m = AutoModelForCausalLM.from_pretrained('/tmp/refined-qwen')
t = AutoTokenizer.from_pretrained('/tmp/refined-qwen')
inputs = t('def fibonacci(n):', return_tensors='pt')
out = m.generate(**inputs, max_new_tokens=50)
print(t.decode(out[0]))
"
# Expected: coherent code completion
```

This milestone is the first commercial-deliverable gate. Refinement-as-service for one model is now possible. **STOP** here and validate quality before proceeding.

---

## M9 — Quality validation (P1 from validation gates)

**Goal:** Verify refined Qwen-Coder-3B beats or matches original on coding benchmarks (HumanEval, MBPP, etc.). Verify refined file is smaller (sparse-tensor-compressed).

**Inputs:** Standard coding benchmark suites; refined model from M8.

**Outputs:**
- Benchmark report comparing original vs refined
- File size comparison (raw and compressed)
- Documented projection-function adjustments based on findings

**Gate:** P1 from validation-gates.md.

If M9 doesn't pass, return to M8 to refine the recomposer's projection function. The substrate's accumulated state may need more sources (M10+) to produce sufficient consensus signal — that's a forward path, not a fail.

---

## M10 — Wiktionary + Tatoeba + tiny-codes (breadth)

**Goal:** Increase substrate coverage with community-curated breadth (Wiktionary), attested usage (Tatoeba), and NL↔code paired data (tiny-codes).

**Inputs:** `D:\Models\wiktionary\raw-wiktextract-data.jsonl` (single JSONL file; line count not verified, kaikki.org dumps are typically multi-million entries), `D:\Models\tatoeba\` (sentences.csv, links.csv, audio/), `D:\Models\hub\datasets--nampdn-ai--tiny-codes\snapshots\9aebe5ee8b406356d5f5f2d603bc0a1684ee8ce7\` (9 parquet shards: part_1_200000.parquet through part_9_1632520.parquet — 1,632,520 NL↔code rows total).

**Outputs:**
- `WiktionaryDecomposer` (handle large JSONL with mmap + thread-local simdjson)
- `TatoebaDecomposer`
- `TinyCodesDecomposer` (parquet streaming + tree-sitter for code AST)
- All decomposer gates passing for each
- Cross-modal NL↔code edges from tiny-codes via `implements_description` edge type

**Gate:**
```sql
-- Substantial breadth in lexical coverage
SELECT count(*) FROM substrate.entity WHERE entity_type_id = (SELECT id FROM ref.entity_type WHERE code='lemma');
-- Expected: > 1M after Wiktionary

SELECT count(*) FROM substrate.entity WHERE entity_type_id = (SELECT id FROM ref.entity_type WHERE code='tatoeba_sentence');
-- Expected: > 1M

-- Cross-modal NL↔code attestations
SELECT count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
 WHERE et.code = 'implements_description';
-- Expected: 1,632,520 (verified tiny-codes total row count)
```

---

## M11 — Multi-model ingestion (consensus substrate)

**Goal:** Ingest 5+ frontier models from `D:\Models\hub` (Llama-4-Maverick, Qwen3-Coder-480B, DeepSeek-V3.2, Florence-2-large, Granite-Speech-3.3-8B, FLUX.2-dev or subset). Cross-model arenas develop rich consensus.

**Inputs:** Models from `D:\Models\hub`. `SafetensorsDecomposer` extended to handle MoE, vision, audio, diffusion architectures.

**Outputs:**
- Decomposer extensions for each architecture family
- Substrate state with multi-model attestations
- Cross-model consensus / divergence queries return meaningful results
- AWQ/GGUF variants explicitly NOT ingested (lossy)

**Gate:**
```sql
SELECT count(DISTINCT p.code) FROM ref.provenance p
  JOIN substrate.edge e ON e.provenance_id = p.id
 WHERE p.code LIKE 'huggingface_model:%';
-- Expected: 5+

SELECT * FROM hartonomous.compare.cross_model_consensus('cat');
-- Expected: n_models = 5+, agreement_score > 0
```

---

## M12 — Laplace-Linguistics-7B (first original product)

**Goal:** Recompose Laplace-Linguistics-7B from substrate accumulated state. Architecture is Anthony's design; weights come from substrate.

**Inputs:** Architecture spec for Laplace-Linguistics (defined by Anthony). `10-architecture/06-recomposer-contract.md`.

**Outputs:**
- Architecture spec document
- Recomposer recipe targeting linguistic arenas
- Output safetensors directory deploying to standard inference stacks
- Loadability and sample-prompt gates passing
- Benchmark report comparing to comparable open-source 7B linguistic models

**Gate:** P2 from validation-gates.md.

---

## M13 — Inference-as-service productization

**Goal:** Customer-facing inference SQL surface deployed. Per-hop filtering recipe support. SLA monitoring.

**Inputs:** All prior milestones; `10-architecture/07-inference-engine.md`.

**Outputs:**
- REST/gRPC endpoint accepting cognitive surface queries
- Recipe DSL parsing and validation
- Per-tenant authentication and request quotas
- Latency monitoring per-step
- P3 from validation-gates.md passing

---

## M14 — Custom architecture synthesis (Custom-Architecture-Synthesis)

**Goal:** Customers specify novel architectures; substrate produces them. The third commercial product.

**Outputs:**
- Customer-facing API for architecture spec submission
- Generic recomposer extension hooks for novel architectures
- Engineering consultancy process for unusual specs
- Documentation and reference architecture examples

---

## M15+ — Continuous expansion

After M14:
- Additional model ingestion (any new safetensors release becomes substrate fuel)
- Additional decomposer formats (new structured data formats)
- Additional cognitive functions (customer-driven domain functions)
- On-premise substrate offering for enterprises

These aren't milestones; they're ongoing operations.

---

## Critical-path summary

The MINIMUM path to first commercial revenue is M0 → M1 → M2 → M3 → **M3.5** → M4 → M5 → M6 → M7 → M8 → M9.

That's 10 milestones. Each is gated. Failure of any gate halts progression until resolved. With focused engineering, this path is approximately 12-18 months. M3.5 (tree-sitter/Kaitai decomposer infrastructure) saves substantially more time downstream than it costs — every subsequent decomposer becomes a grammar+mapping rather than a custom parser.

M10–M12 are required for full substrate product family launch.

M13–M14 are productization milestones; technically possible with M9 substrate but commercially weak until M10–M12 fill substrate breadth.

## Cross-references

- Validation gates per milestone: `40-process/02-validation-gates.md`
- The Substrate Laws each milestone preserves: `10-architecture/01-substrate-laws.md`
- Per-component checklists: `40-process/checklists/`
- Status tracker (per-milestone progress): `60-status/00-implementation-status.md`
