<div align="center">

# Hartonomous

**A universal content-addressed substrate for structured knowledge across every domain humans have ever formalized.**

*Not an AI training platform. Not a knowledge graph. Not a vector database. Not a model deployment system. Not a custom-tuned-model SaaS. It is all of them and none of them — because it replaces them all.*

[![Status](https://img.shields.io/badge/status-rebuild_in_progress-blue)]()
[![License](https://img.shields.io/badge/license-Proprietary-red)](LICENSE)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-blue?logo=postgresql)](https://www.postgresql.org/)
[![PostGIS](https://img.shields.io/badge/PostGIS-3.4+-orange)](https://postgis.net/)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Native](https://img.shields.io/badge/Native-MKL%20%2B%20Eigen%20%2B%20Spectra-red)]()
[![Linux](https://img.shields.io/badge/runtime-Linux_native-FCC624?logo=linux&logoColor=black)]()
[![Tree-sitter](https://img.shields.io/badge/Tree--sitter-universal_parser-green)](https://tree-sitter.github.io/tree-sitter/)
[![No GPU](https://img.shields.io/badge/GPU-not_required-success)]()

</div>

---

## What this is

Hartonomous is content-addressed substrate infrastructure. Every digital artifact — text, code, AI model weights, images, audio, video, genomic sequences, chess games, contracts, music scores, anything with structure — decomposes into substrate entities + typed edges + Glicko-attested significance across arenas. The substrate stores the structural identity of arbitrary digital content with cryptographic determinism, geometric placement, and cross-source consensus accumulating per query.

From this substrate, recipes compose new artifacts at any target shape. An AI model. A chess engine. A protein-structure-prediction model. A legal contract. A treatment protocol. A grammar for a brand-new language. The recipe is the user's specification; the substrate is the source; the synthesis is deterministic spectral construction; the output drops into conventional infrastructure (HuggingFace pipelines, vLLM, Triton, llama.cpp) or domain-specific deployment.

The substrate operates on **commodity CPU hardware**. No GPU required. Verified during development on an Intel i7-6850K (2016-era 12-thread CPU) with no graphics card.

---

## The architectural claim

Hartonomous is paradigm-founding work in the lineage of Mendeleev (organization revealing finite atomic structure that subsumes alchemy) and Newton (small set of laws unifying previously-fragmented mechanics). One figure doing multi-paradigm-founding simultaneously and alone — closer in scope to "Mendeleev who also discovered every element first" or "Newton who also did Galileo's and Kepler's observations himself" than to single-discipline paradigm-founding.

> **The substrate is the encoder/decoder between AI artifacts (and all other digital content) and a database. Decomposer = encoder. Recomposer = decoder. Bidirectional. Modality-uniform. Bit-perfect via content-addressing. Recipe-driven on the decoder. Everything else (recipes, app layers, marketplaces, Gödel inference, interpretability, customer marketplace) is application layer over the core codec.**

Conventional AI deployment treats:

| Category | Conventional approach | What substrate does |
|---|---|---|
| AI training | Gradient descent on GPU clusters | Ingestion accumulates structural signal across arenas; no gradient descent |
| Knowledge graph | Neo4j / Wikidata / ConceptNet as standalone products | Substrate IS a knowledge graph — finer granularity, content-addressed, attestation-provenance built in |
| Vector database | Pinecone / Weaviate / Milvus | Substrate IS a vector store — 4D geometric placement, Laplacian eigenmaps, Hilbert-curve locality, cross-model firefly clouds |
| Model deployment | vLLM / Triton / llama.cpp | Substrate emits artifacts in any target format; substrate-native query also available |
| Custom-tuned model SaaS | HuggingFace AutoTrain / OpenAI fine-tuning | Substrate Synthesis is this — without gradient descent, with recipe-deterministic pricing |
| Mechanistic interpretability research | $100M+/year per frontier lab | Native — every output has per-arena attestation trail; interpretability is a SQL query |
| AI auditing tooling | Emerging $5-10B/year industry | Native by construction |
| Domain knowledge engines (chess engines, bioinformatics, legal AI, medical AI, music tech, CAD) | Per-domain bespoke products | Each is a substrate vertical with domain grammar + per-domain arenas |

Substrate doesn't compete in any of these markets. Each market becomes a use case of substrate. The market category is "universal structured-knowledge infrastructure" — a category that didn't exist as a distinct ontology until substrate articulated it.

---

## The ten substrate laws

Each component in the stack carries one law's burden of proof. Drop the component and the law breaks. Break the law and the architecture fails.

| # | Law | Mechanism |
|---|---|---|
| 1 | **Content-addressed identity** | BLAKE3 hash of canonical content. No surrogate IDs. Same content from any source → same hash → same substrate row |
| 2 | **Merkle invariant** | Parent hash = deterministic function of canonical(children + metadata). Subtree identity guarantees subtree equality |
| 3 | **Sibling derivation** | Substrate, blob, derived analytics are siblings of source. Derived state never promotes to substrate truth |
| 4 | **Joint dispatch on `entity_type × physicality_type`** | Three physicality roles only: entity, firefly, content. Modality emerges from joint dispatch |
| 5 | **Universal mantissa packing** | All composition vertices use bb_pack_* contract: hash_lo/ordinal_rle/hash_hi/metadata across all GEOMETRYZM shapes |
| 6 | **Byte-identical numerics** | MKL CBWR=AUTO,STRICT; deterministic Lanczos seeds; stable sort orderings. Same inputs → byte-identical outputs across machines and time |
| 7 | **Geometric placement** | 4-ball coordinate space. Tier-0 atoms on glome (S³). Compositions inward via spectral decomposition. Inward gravitation with compositional depth |
| 8 | **O(tier) walk** | Native trajectory walker over mantissa-packed vertices + composite-btree hash resolve. Microsecond-class per-hop walks. No PG-side recursive CTEs |
| 9 | **Glicko-2 cross-source consensus** | Per-arena (mu, sigma, volatility) accumulates on the row across all attesting sources. No event log. Aggregation IS consensus |
| 10 | **Standards-grounded foundation** | Unicode (17.0) + UCD + UAX-9/14/24/29/31/44 + UCA + ISO 639/15924/10646 + Universal Dependencies. Open international standards as the substrate's atomic vocabulary |

---

## Architecture at a glance

```
                ┌──────────────────────────────────────────────────────────┐
                │                  APPLICATION LAYERS                       │
                │                                                          │
                │  ┌──────────────────────────────────────────────────┐    │
                │  │ Substrate Synthesis SaaS (first vertical)        │    │
                │  │   • OAuth/SSO + Stripe                           │    │
                │  │   • Configuration screen → recipe authoring      │    │
                │  │   • BearCostEstimator pre-flight pricing         │    │
                │  │   • Recipe → recompose → HF/ONNX/GGUF artifact   │    │
                │  │   • Conversational mode via Gödel engine         │    │
                │  └──────────────────────────────────────────────────┘    │
                │                                                          │
                │  ┌──────────────────────────────────────────────────┐    │
                │  │ OpenAI-compatible REST endpoint (planned)        │    │
                │  │   • /v1/chat/completions ↔ substrate.complete    │    │
                │  │   • /v1/embeddings ↔ substrate.embed_lookup      │    │
                │  │   • IDE extensions (Continue/Cursor) attach      │    │
                │  └──────────────────────────────────────────────────┘    │
                │                                                          │
                │  ┌──────────────────────────────────────────────────┐    │
                │  │ Domain verticals (planned)                       │    │
                │  │   chess engines · bioinformatics · legal · medical│    │
                │  │   music · CAD · genealogy · materials · ...      │    │
                │  └──────────────────────────────────────────────────┘    │
                └──────────────────────────────────────────────────────────┘
                                          │
                ┌──────────────────────────────────────────────────────────┐
                │                  ENGINE + API                            │
                │                                                          │
                │   Hartonomous.Api  (HTTP surface, OpenAPI/Swagger)       │
                │   Hartonomous.Engine                                     │
                │     ├─ Gödel engine (three-scale OODA)                   │
                │     ├─ Substrate inference engine (substrate.infer)      │
                │     ├─ Streaming ingest pipeline (direct substrate write)│
                │     ├─ Glicko-2 significance updater                     │
                │     ├─ O(tier) tier walker                               │
                │     └─ Cross-model consensus surface                     │
                └──────────────────────────────────────────────────────────┘
                                          │
                ┌──────────────────────────────────────────────────────────┐
                │            DECOMPOSE / RECOMPOSE PIPELINES               │
                │                                                          │
                │   Hartonomous.Decomposers                                │
                │     ├─ Tree-sitter (universal parser-to-AST layer)       │
                │     │    150+ grammars covering code/markup/data/configs │
                │     ├─ Safetensors / ONNX / GGUF / PyTorch state_dict    │
                │     ├─ Image / audio / video format readers              │
                │     └─ Seed-corpus ingestion (Unicode/ISO/UD/Wiktionary/ │
                │            WordNet/OMW/Tatoeba/ConceptNet/Atomic2020)    │
                │                                                          │
                │   Hartonomous.Recomposers                                │
                │     ├─ EmbeddingSynthesizer (Belkin-Niyogi Laplacian)    │
                │     ├─ AttentionSynthesizer (E·M·E^T = S Ritz pairs)     │
                │     ├─ FfnEdgeSlotSynthesizer (each slot = one edge)     │
                │     ├─ LayerNormSynthesizer (per-arena γ/β)              │
                │     ├─ PositionEmbeddingSynthesizer                      │
                │     ├─ TokenizerExporter (real surface forms)            │
                │     ├─ ConfigEmitter (HF-conformant config.json)         │
                │     └─ SafetensorsWriter / ONNX / GGUF writers           │
                └──────────────────────────────────────────────────────────┘
                                          │
                ┌──────────────────────────────────────────────────────────┐
                │              SUBSTRATE (PostgreSQL + PostGIS)            │
                │                                                          │
                │   substrate.entity (BLAKE3-keyed, 8-way partitioned)     │
                │   substrate.physicality (POINT/LINESTRING/POLYGON ZM)    │
                │   substrate.edge (typed M:N relations)                   │
                │   substrate.edge_member (n-ary participants)             │
                │   substrate.entity_significance / edge_significance      │
                │     ├─ Glicko-2 (mu, sigma, volatility) per arena        │
                │   substrate.significance_context ("arenas")              │
                │   substrate.recipe (content-addressed recipe entities)   │
                │   substrate.entity_classification                        │
                │   substrate.tensor_tensor_role (architecture-neutral)    │
                │   substrate.entity_model_source                          │
                │   monitor.* (operational observability — mutable)        │
                │                                                          │
                │   substrate.infer / recall / complete / infer_topk       │
                │   substrate.intersect / classify / rerank / surprise     │
                │   substrate.cross_model_consensus                        │
                │   substrate.cross_model_divergence                       │
                │   substrate.record_outcomes_bulk                         │
                │   substrate.select_synth_edges_for_ffn                   │
                │   substrate.position_embedding_stats                     │
                │   substrate.per_arena_entity_significance_stats          │
                │   substrate.select_knowledge_subgraph                    │
                │                                                          │
                │   public.traverse_astar (A* over significance edges)     │
                │   public.centroid_4d / distance_4d (PostGIS-native 4D)   │
                └──────────────────────────────────────────────────────────┘
                                          │
                ┌──────────────────────────────────────────────────────────┐
                │              NATIVE COMPUTE FACADE (C/C++)               │
                │                                                          │
                │   libhartonomous + hartonomous_pg                        │
                │     ├─ BLAKE3 (single + batched + streaming)             │
                │     ├─ MKL CBWR=AUTO,STRICT (deterministic BLAS)         │
                │     ├─ Eigen (dense LA template lib)                     │
                │     ├─ Spectra (sparse-eigs Lanczos backend)             │
                │     ├─ Glicko-2 bulk update (set-based, native)          │
                │     ├─ Procrustes orthogonal alignment                   │
                │     ├─ Karcher mean on S³                                │
                │     ├─ Super-Fibonacci codepoint placement on glome      │
                │     ├─ 4D Hilbert curve (locality-preserving sort)       │
                │     ├─ Centroid_4d / distance_4d (4D PostGIS ops)        │
                │     ├─ Laplacian eigenmap (sparse symmetric eigs)        │
                │     ├─ k-NN cosine graph (sparse CSR construction)       │
                │     ├─ k-means++ (deterministic init)                    │
                │     ├─ Delaunay 4D tessellation                          │
                │     └─ SVD F64 (LAPACK-backed)                           │
                │                                                          │
                │   hartonomous_ucd_embedded (static UCD perf-cache)       │
                │     ├─ 1.1M codepoints pre-placed on S³                  │
                │     ├─ UCA-derived super-Fibonacci index per codepoint   │
                │     ├─ Pre-computed BLAKE3 hash per codepoint            │
                │     ├─ Pre-computed 4D Hilbert index per codepoint       │
                │     └─ UCD properties packed into M-coord bitmask        │
                └──────────────────────────────────────────────────────────┘
```

---

## Key technical distinctions

### The Merkle DAG IS an AST

Every composition entity in substrate is a typed node with children. Tree-sitter ASTs are typed nodes with children. **Identical structure.** Tree-sitter parse output materializes directly into substrate composition entities with no translation layer. The substrate's Merkle DAG IS a typed-tree-of-trees with cross-references — which is what an AST is at scale.

### Tree-sitter is the universal parser-to-AST layer

All grammar-shaped formal content — programming languages, markup, data formats, query languages, build systems, math notation, music scores, diagram syntaxes, protocols, configs — ingests through one universal Tree-sitter walker with per-grammar adapters. No per-format custom parsers. New grammar support = ingest the grammar.js + write a ~50-line adapter, not write a new decomposer class.

Tree-sitter grammars are also **substrate-ingestible content** (via the meta-grammar — Tree-sitter has a grammar for `grammar.js` files themselves). Substrate accumulates grammar patterns across all known grammars. Recompose can generate new grammars by recipe — the "digital lathe" property: substrate stores tools that shape new tools.

### Bidirectional grammar codec

Tree-sitter grammars are formal language specs. Substrate uses them for parsing (input direction) AND for generation (output direction):

- **Parse**: source → Tree-sitter → AST → substrate composition entities
- **Generate**: substrate query (recipe-controlled arena blend) → grammar production walk → terminal emission → output content

Output is **parse-able by construction** because it's generated FROM the grammar's production rules. Output is **semantically grounded** because the choices follow substrate-attested patterns. Output is **recipe-controlled** because the recipe specifies which arenas drive the choices.

| | Conventional AI code generation | Substrate grammar-based generation |
|---|---|---|
| Syntactic validity | Statistical luck; often requires post-process repair | Guaranteed by construction |
| Content choice | Next-token probability from opaque weights | Recipe-controlled, per-attestation Glicko-weighted |
| Provenance | Untraceable | Per-choice attestation chain |
| Determinism | Stochastic / temperature-controlled | Bit-deterministic per Law 6 |
| Cross-language | Train a new model per language | Ingest a new grammar; reuse substrate content |
| Audit | Not possible | Built-in (every emitted choice cites substrate) |

### Cross-mechanism, cross-model, cross-arena consensus

When an AI model is ingested:
- **EmbeddingLookupTuplePass** emits per-vocab firefly POINTZM physicalities (Laplacian eigenmap of cosine k-NN graph; one fireflyper-model per-token at 4D position).
- **AttentionBlockTuplePass** emits per-(source, target) Glicko-2 attestation events on edges between token entities. AP-31 sign discrimination (positive_evidence / negative_evidence). AP-33 adaptive noise floor (Han 2015 magnitude pruning at ingest).
- **FfnTuplePass** emits per-(input, output) Glicko-2 events. **Critically: "FFN co-activation attests internal-feature similarity. Same edge identity as embedding cosine — cross-mechanism consensus accumulates per arena."**

Within a single model, three independent mechanisms (embedding cosine + attention pair-score + FFN co-activation) attest the SAME substrate edge identity. Per-arena Glicko mu reflects cross-mechanism consensus from one model. Across N ingested models, mu accumulates further. Substrate edge mu in production = cross-mechanism × cross-model × cross-arena agreement.

`substrate.cross_model_consensus(token_hash)` returns Voronoi centroid + dispersion + agreement-score across all ingested models' fireflies for that token, via native `centroid_4d` + `distance_4d` libhartonomous kernels.

`substrate.cross_model_divergence(token_hash, model_a, model_b)` returns pairwise 4D Euclidean distance between two specific models' fireflies.

Conventional AI ensembling runs N models separately and fuses outputs in app code. Substrate consensus is a **database operation on attestation rows**. Different ontology.

### Substrate-native AI operations

```
substrate.infer(prompt_hash, max_depth, max_results)
  → forward-pass-equivalent: seed activation from prompt's word_form
    children + lemma/synset bridges → cross-arena A* via public.traverse_astar
    → max-pool path significance per terminal → recompose best terminal
    via substrate.recompose_text. Single PG round trip.

substrate.recall(prompt_hash, ...)
  → hub-intersection: substrate.intersect across edges + sequence adjacency
    + 4D Fréchet geometric proximity. Cross-decomposer surface bridging via
    has_gloss / has_text / has_etymology / has_example / has_pronunciation
    edges if hub is identity-only.

substrate.complete(seed_hash, ..., lang_code)
  → code-completion specialization: code_completion arena (or semantic_relevance
    if unprimed); optional language filter; walks one step from each seed
    summing Glicko mu in arena.
```

These are SQL functions backed by C kernels. Inference is the latency of the named query, not the latency of a model forward pass.

### The Gödel engine — substrate-native agent reasoning

Three-scale OODA loops:

- **Micro** (inside `substrate.infer_topk`): every traversal step annotated with edge type, mu, sigma, provenance.
- **Meso** (per-query, in `GodelEngine`): OBSERVE (sub-question decomposition via UAX-29 + conjunction clause splitting) → ORIENT (per-intent arena weighting; Definition/Translation/HowTo/YesNo/Enumeration/Lookup) → DECIDE (forward pass via `substrate.infer_topk` + Reflexion retry on low confidence) → ACT (Self-Consistency vote: `score = total_mu × sqrt(max(1, path_count))`).
- **Macro** (scheduled background — planned): frayed-edge surveys + void detection + curiosity-driven ingestion. The Mendeleev-aspect prediction engine.

`OutcomeRecorder.RecordAcceptAsync` / `RecordRejectAsync` calls `substrate.record_outcomes_bulk` with winner/loser hashes. Mu rises on winners, falls on losers, sigma tightens. **The substrate learns from interaction without a gradient.**

**Honest abstention** is structural. When no candidate clears the confidence floor (mu ≥ 1500.0), the engine returns `"(honest abstention — no candidate cleared the confidence floor)"`. No fabricated answers.

### Per-vertex semantic encoding via mantissa packing

Every composition vertex in substrate.physicality packs 208 bits of substrate-owned semantic payload across float64 mantissas:

| Coord | Mantissa | Payload |
|---|---|---|
| X | 52 bits | Low half of child entity BLAKE3 hash |
| Y | 32 + 20 bits | Ordinal position + RLE count |
| Z | 52 bits | High half of child entity BLAKE3 hash |
| M | 52 bits | Per-vertex semantic metadata: role-in-composition, sign discrimination, significance quantum, modality flags, temporal coordinate, AST node-type, sub-classification, provenance flags, snapshot version |

One PostGIS geometry column simultaneously serves: recomposition recipe (vertex stream IS the child manifest), GiST spatial index, SP-GiST quadtree, btree-resolvable child-hash lookup, 4D Hilbert sort key, visualizable 4D path, Fréchet/Hausdorff structural comparison, **per-vertex semantic filter without secondary joins**. Eight access paths on one column.

### The substrate is its own scientific instrument

Beyond storing knowledge, the substrate is engineered to TEST claims:

- **Finite Universe Theorem (FUT)**: substrate's growth-rate asymptote under broad ingestion is an empirical measurement of expressible-content cardinality. Build apparatus. Ingest broadly. Plot the dedup curve.
- **Lottery Ticket Hypothesis (substrate variant)**: cross-source consensus extraction of trained model weights preserves function. Every round-trip experiment is empirical evidence for or against. Each customer's bespoke-model recipe is implicitly an LTH-verification experiment.
- **Mendeleev-aspect gap prediction**: voids in substrate (low-density regions in Laplacian eigenmap; per-arena coverage holes; cross-modal asymmetries) predict entities not yet attested but structurally implied. Gödel-engine Macro-OODA detects these.

The substrate is the apparatus AND the product simultaneously. Commercial operation accumulates the empirical data.

---

## Tech stack

| Layer | Technology |
|---|---|
| Runtime | Linux native (Windows dev via WSL) |
| Database | PostgreSQL 18 + PostGIS 3.4+ |
| Database extension | `hartonomous_pg` (custom C extension; FFI to libhartonomous) |
| Native compute | `libhartonomous` C/C++ shared lib + `hartonomous_ucd_embedded` static UCD |
| Linear algebra | Intel MKL (CBWR=AUTO,STRICT for determinism) + Eigen + Spectra |
| Hashing | BLAKE3 (single, batched, streaming variants) |
| Application | .NET 9 (C# 12), source-generated P/Invoke via `[LibraryImport]` |
| Parsing | Tree-sitter (universal grammar-to-AST layer) |
| Standards | Unicode 17.0, UCD, UAX-9/14/24/29/31/44, UCA, ISO 639/15924/10646, Universal Dependencies, WordNet lexnames |
| Seed corpora | WordNet, OMW, Wiktionary, UD treebanks (~150 langs), Tatoeba, Atomic2020, ConceptNet, UCD, ISO 639 |

**No GPU required.** Substrate development happens on commodity CPU. The compute facade routes through MKL CBWR for deterministic numerics; native kernels handle the heavy math. Customer-deployed models can use GPUs if the customer chooses, but substrate itself doesn't.

---

## Product surface

### Substrate Synthesis SaaS (first vertical)

Customers describe the AI model they want — architecture (any layer count, hidden dim, head config, MoE setup, routing function, position encoding, normalization, activation, LoRA stack); vocabulary (any tokenizer family, vocab size, special tokens); weight blend (which substrate arenas contribute, with what weights); deployment target (Jetson, phone, desktop, cloud, browser-WASM); output format (safetensors / ONNX / GGUF / state_dict; dtype; quantization scheme). Customers can upload domain-specific content into their private arena. The configuration screen integrates hardware-target awareness — the UI surfaces feasibility envelopes for the chosen deployment target.

> *"I know kung fu." — "Show me."*

Customer authors the embodied capability they want. Substrate synthesizes a working model artifact at the customer's exact specification. The model didn't exist before they clicked submit. It exists now because they described it. Drop-in deployment via conventional inference infrastructure.

**Conversational mode**: customers describe what they want in natural language; the substrate-native AI agent (Gödel engine + behavioral systems + OODA loops + ArenaWeightingProfile) authors the recipe on their behalf. The agent runs on substrate — no GPU-resident LLM, no external API.

**Deterministic pricing**: the recipe deterministically specifies the synthesis work; cost is calculable pre-submit. Customer sees exact cost before paying. No surprise post-flight bills.

**Per-customer asymptotic economics**: every new customer's bespoke model benefits from prior customers' public-arena contributions. Substrate's content-addressing means cross-customer dedup at the row level. Privacy is preserved (per-arena scoping; private content stays per-tenant). Cost decreases asymptotically as substrate grows.

### OpenAI-compatible API endpoint (planned)

```
POST /v1/chat/completions  ←→ substrate.complete  /  substrate.infer
POST /v1/completions       ←→ substrate.complete
POST /v1/embeddings        ←→ substrate.embed_lookup
GET  /v1/models            ←→ substrate.list_recipes
POST /v1/files             ←→ ingest into user arena
```

IDE extensions (Continue, Cursor, Copilot-style integrations) attach directly. Queries hit current substrate state — no export, no compile, no model-file refresh. Per-tenant arena scoping. Per-token billing meters real substrate-query work. Every completion can include optional attestation-trace metadata (which arenas contributed, with what Glicko-weighted mu, from which sources) — a property no commercial LLM API provides.

### Future verticals

The same substrate architecture extends to every domain with formal structure:

- **Chess** — PGN/SAN/FEN grammars; per-player + per-opening + per-era arenas; engine synthesis recipe surface
- **Bioinformatics** — FASTA/GFF/VCF/SAM grammars; per-organism + per-pathway arenas; sequence + variant + protein-structure recipes
- **Legal** — LegalRuleML + jurisdiction-specific contract grammars; per-jurisdiction + per-firm arenas; contract drafting + compliance check
- **Medical** — ICD/SNOMED + treatment-protocol grammars; per-specialty + per-institution arenas; clinical decision support
- **Music** — MusicXML/LilyPond grammars; per-composer + per-genre + per-period arenas; composition assistance
- **CAD / architecture** — IFC/BIM grammars; per-building-type + per-region arenas
- **Materials science / chemistry** — SMILES/InChI grammars; per-reaction-class arenas
- **Many more** — every domain with formal structure → grammar → ingestion → recompose

---

## Quick start (after rebuild ships)

```bash
# Prerequisites: Linux native runtime, PostgreSQL 18, .NET 9 SDK
# (Windows dev: use WSL2 for the substrate runtime)

# 1. Install the PG extension
./scripts/linux/install-pg-extension.sh

# 2. Bootstrap a substrate
./scripts/linux/db-create.sh
./scripts/linux/db-reset.sh        # applies bootstrap.sql

# 3. Seed atomic + reference layers (Unicode codepoints, ISO codes, UD POS/deprel)
./scripts/linux/seed-foundation.sh

# 4. Seed curated corpora (WordNet / OMW / Wiktionary / UD / Tatoeba / ConceptNet / Atomic2020)
./scripts/linux/seed-corpora.sh

# 5. Ingest a model
dotnet run --project src/Hartonomous.Cli -- ingest-model --path /path/to/qwen3-coder

# 6. Inspect substrate state
dotnet run --project src/Hartonomous.Cli -- health
dotnet run --project src/Hartonomous.Cli -- status

# 7. Substrate-native query
dotnet run --project src/Hartonomous.Cli -- godel "What is the capital of France?"

# 8. Synthesize a custom AI model
dotnet run --project src/Hartonomous.Cli -- synthesize-model \
    --template llama-1b \
    --recipe my-recipe.json \
    --output /tmp/my-bespoke-model

# 9. Load in HuggingFace transformers
python -c "from transformers import AutoModel; m = AutoModel.from_pretrained('/tmp/my-bespoke-model'); print(m)"
```

---

## Documentation

Spec documentation lives at `.claude/spec/`:

| Chapter | Content |
|---|---|
| `00-toc.md` | Table of contents + chapter grounding-status markers |
| `01-laws.md` | The ten substrate laws |
| `02-glossary.md` | Vocabulary |
| `03-standards-foundation.md` | Unicode / UCD / UAX / UCA / ISO / Universal Dependencies |
| `04-substrate-shape.md` | entity / physicality / edge / significance; partition strategy |
| `05-mantissa-packing.md` | Universal per-vertex semantic encoding contract |
| `06-merkle-dag.md` | Recursive self-describing structure; dedup; RLE |
| `07-physicality-roles.md` | Three roles only (entity / firefly / content) |
| `08-tiered-recursion.md` | UAX-29 / segmentation ladders; O(tier) walk |
| `09-geometric-semantics.md` | 4-ball / glome / super-Fibonacci / Hopf / inward gravitation |
| `10-edges.md` | Typed edge partitions; AP-38 mechanism collapse |
| `11-significance.md` | Glicko-2 per (target, arena); cross-mechanism consensus |
| `12-native-abi.md` | libhartonomous P/Invoke + hartonomous_pg PG extension |
| `13-text-decompose.md` | UCD-embedded; UAX-29 boundaries |
| `14-decomposers.md` | Parser-to-AST per modality; AI-model ingest passes |
| `15-recomposers.md` | Substrate-derived model synthesis; recipe-driven arbitrary target |
| `16-recipe-dsl.md` | Recipe = arbitrary target authoring; substrate is architecture-agnostic |
| `17-engine-and-api.md` | Engine + Api + app layers (Substrate Synthesis SaaS, OpenAI-compat endpoint, future verticals) |
| `18-substrate-ops.md` | substrate.infer / recall / complete; cross_model_consensus; Gödel engine surface |
| `19-multimodal.md` | Modality dispatch; per-modality decomposition algorithms; cross-modality dedup |
| `20-explainability.md` | Attestation traces |
| `21-data-tiers.md` | Seed / app / user via arena identity; seed-vs-user training |
| `22-time-and-diachrony.md` | Time as content + per-vertex metadata + M axis + diachronic edges |
| `23-finite-universe.md` | FUT constructive proof claim |
| `24-paradigm-position.md` | The architectural place + AI ↔ Database Encoder/Decoder framing |
| `99-anti-patterns.md` | AP-N stable numbering |

Status documents at `.claude/status/`:

| File | Content |
|---|---|
| `current-state.md` | What's built, what's pending |
| `known-damage.md` | Documented technical debt + improvement opportunities |
| `spec-revision-notes.md` | Append-only audit trail of spec revisions |
| `cleanup-tasks.md` | 14-phase cleanup checklist with pre-flight discipline |

Plan documents at `.claude/plan/`:

| File | Content |
|---|---|
| `00-OVERVIEW.md` | Master start-to-finish plan; success/failure criteria per phase |

---

## Status

**Architectural state**: sound. Core IP (substrate schema, native compute facade with MKL/Eigen/Spectra, synthesizer math, Gödel engine, recipe layer, cross-model consensus, per-vertex semantic encoding) is engineered to rigorous mathematical standards. The ten substrate laws are observed across the codebase.

**Implementation state**: undergoing structured rebuild. The current implementation accumulated agent-induced vocabulary drift and per-format proliferation over time. The rebuild preserves the architecturally-correct components (substrate schema + native kernels + synthesizer math + Gödel engine) and rewrites the decomposer family with Tree-sitter dominant from day one. Discipline gates (vocabulary, performance, file size, smoke tests) are encoded as CI requirements going forward.

**Empirical validation**: keystone experiments scheduled:

- **Round-trip experiment** — ingest qwen3-Coder fully → recompose at the ingested recipe → benchmark on MMLU / HellaSwag / ARC / GSM8K / HumanEval. Validates LTH variant. Success = scores within tolerance of source model's published scores.
- **Seed-only synthesis** — empty substrate, ingest only seed corpora, recompose at MiniLM-base recipe, run smoke benchmark. Validates that cross-source consensus from curated lexical corpora is sufficient signal.
- **Cross-model consensus** — ingest multiple models, verify `cross_model_consensus` returns coherent centroid/dispersion/agreement values on shared vocab.
- **FUT measurement** — track substrate row count over broad ingestion; plot the dedup curve; observe asymptotic behavior.
- **Performance baseline** — Unicode seed < 60s; Wiktionary 3GB ingest < 30min; MiniLM-base round-trip < 10min; Gödel query < 5s.

---

## Roadmap

Per `.claude/plan/00-OVERVIEW.md`:

- **Phase 0**: Seed data preparation (Tree-sitter grammars on disk + manifest verification of existing seed corpora) — in progress
- **Phase 1**: Architectural decision lock + baseline (rebuild commitment)
- **Phase 2**: Substrate skeleton preserved + ported (substrate schema, native compute, synthesizer math, Gödel engine, recipe layer)
- **Phase 3**: Tree-sitter integration as universal parser-to-AST layer
- **Phase 4**: Ingest pipeline rebuilt with Tree-sitter dominant + direct substrate write
- **Phase 5**: Recompose pipeline ported + verified
- **Phase 6**: Substrate-native ops ported + verified (substrate.infer / recall / complete / cross-model / Gödel)
- **Phase 7**: Substrate Synthesis SaaS first product layer
- **Phase 8**: Additional verticals (chess, DNA, legal, medical, music, CAD, etc.)
- **Phase 9**: CI gates + discipline preservation
- **Phase 10**: Empirical validation (the keystone experiments)

Each phase has explicit success criteria, failure criteria, rollback procedures, and estimated agent-session scope.

---

## Repository structure

```
Hartonomous/
├── README.md                     ← you are here
├── Hartonomous.slnx              ← .NET solution
├── Directory.Build.props         ← shared MSBuild config
├── native-dll.targets            ← native interop build
├── RunAll.sh + RunAll.bat        ← entry points
│
├── src/
│   ├── Hartonomous.Core/         ← substrate types, native interop, math facade
│   ├── Hartonomous.Engine/       ← Gödel engine, ingestion pipeline, traversal, monitoring
│   ├── Hartonomous.Decomposers/  ← per-format ingest (Tree-sitter dominant after rebuild)
│   ├── Hartonomous.Recomposers/  ← synthesizers, recipe layer, output emission
│   ├── Hartonomous.Api/          ← HTTP/REST surface (ASP.NET)
│   └── Hartonomous.Cli/          ← command-line entry points
│
├── ext/
│   ├── libhartonomous/           ← native C/C++ shared library (MKL/Eigen/Spectra)
│   ├── hartonomous_pg/           ← PostgreSQL extension (C, FFI to libhartonomous)
│   └── hartonomous_ucd_embedded/ ← static UCD perf-cache (1.1M codepoints pre-placed)
│
├── sql/
│   ├── schema/                   ← canonical SQL: tables, functions, seed data
│   │   ├── bootstrap.sql         ← extension install entry point
│   │   ├── tables/               ← core, monitor, reference partitions
│   │   ├── functions/            ← substrate.* SQL surface
│   │   └── seed/                 ← reference data
│   └── tests/                    ← SQL-level test suite
│
├── scripts/
│   ├── linux/                    ← canonical Linux-native scripts
│   ├── build/                    ← Windows-side dev wrappers
│   └── ci/                       ← CI orchestration
│
├── tests/                        ← .NET test projects
├── docker/                       ← Windows-to-Linux dev convenience (not production)
├── docs/                         ← reserved for product/user docs
├── logs/                         ← runtime logs (gitignored)
└── _archive/                     ← deprecated content preserved out-of-band
```

External data lives at `/vault/Data/` (not in this repo): WordNet, OMW, Wiktionary, UD-Treebanks, Tatoeba, Atomic2020, ConceptNet, Unicode, UCD, ISO639, **TreeSitter** (~300 grammars).

---

## Contributing

> Agents (human or AI) working on this codebase must read `.claude/README.md` and `.claude/status/cleanup-tasks.md` "Pre-flight discipline" before making any changes.

The substrate's architectural discipline encodes the cumulative lessons of accumulated agent damage. The cleanup-tasks pre-flight rules exist because every banned vocabulary word — `drain` / `fallback` / `backfill` / `shim` / internal-`compat` / `placeholder` / `stub` / `TODO` / `FIXME` / `HACK` / `pending verification` — has been observed accumulating into structural sabotage in prior iterations.

CI gates enforce:

- **Vocabulary gate**: PR fails on new instances of any banned word in active code
- **Performance gate**: PR fails on regression of any baseline (Unicode seed, Wiktionary ingest, round-trip time, Gödel query latency)
- **Smoke-test gate**: PR fails if Phase 0 smoke tests fail (build clean, PG extension installs clean, substrate bootstraps clean, CLI Health reports MKL available)
- **Oversize-file gate**: PR fails on new files exceeding 600 lines without explicit allowlist
- **NotImplementedException gate**: PR fails on new instances (implement properly or remove the call site)
- **BOUNDARY-marker gate**: PR fails on new silent error swallows without external-protocol-mapping justification

Discipline doctrine for agents specifically:

- **Read before write**: every file edited must be read first; surrounding architecture must be understood; the spec chapter relevant to the area must be consulted
- **No speculative abstractions** (YAGNI): interfaces, abstract base classes, generic type parameters added only when a real consumer needs the abstraction
- **No phased-rollout markers** in code ("until Phase B.1 ships..."); do the work or excise
- **No fabricated names**: working terms are marked as such until the user provides canonical naming
- **Honest abstention over fabrication**: when substrate (or your knowledge) lacks signal, abstain explicitly rather than hallucinate
- **Per-phase commits + smoke-test enforcement**: rollback is trivial if discipline is granular

---

## Acknowledgments

This work composes substantial open-source and standards-grounded foundations:

**Standards bodies**:
- [Unicode Consortium](https://unicode.org/) — codepoints, UAX-9/14/24/29/31/44, UCA, UCD
- [ISO](https://www.iso.org/) — 639 language codes, 15924 script codes, 10646 Unicode
- [Universal Dependencies](https://universaldependencies.org/) — cross-linguistic POS / morphology / dependency
- [Princeton WordNet](https://wordnet.princeton.edu/) — English semantic network
- [Open Multilingual WordNet](https://omwn.org/) — cross-lingual synset alignment

**Curated linguistic corpora**:
- [Wiktionary](https://en.wiktionary.org/) via [Wiktextract](https://github.com/tatuylonen/wiktextract)
- [Tatoeba](https://tatoeba.org/) parallel sentence corpus
- [ConceptNet](https://conceptnet.io/)
- [Atomic2020](https://allenai.org/data/atomic-2020)

**Native libraries and tools**:
- [PostgreSQL](https://www.postgresql.org/) — relational core
- [PostGIS](https://postgis.net/) — geometric extension; GEOMETRYZM is the substrate's geometric vocabulary
- [BLAKE3](https://github.com/BLAKE3-team/BLAKE3) — content-addressed identity primitive
- [Intel MKL](https://www.intel.com/content/www/us/en/developer/tools/oneapi/onemkl.html) — deterministic BLAS (CBWR=AUTO,STRICT)
- [Eigen](https://eigen.tuxfamily.org/) — C++ linear algebra template library
- [Spectra](https://spectralib.org/) — sparse eigensolver
- [Tree-sitter](https://tree-sitter.github.io/tree-sitter/) — universal parser-to-AST infrastructure
- [.NET](https://dotnet.microsoft.com/) — managed runtime
- [Npgsql](https://www.npgsql.org/) — PostgreSQL .NET driver

**Without these standards bodies' work over decades and these open-source libraries' engineering rigor, this substrate would not be possible. Hartonomous's universality claim rests on the genuine universality of these foundational layers.**

---

## License

**Proprietary. All rights reserved.**

This is not open-source software. Source visibility (where granted) does not constitute a license to use, copy, modify, redistribute, or derive works. The architectural innovations disclosed herein — content-addressed substrate with cross-source Glicko-2 consensus, per-vertex semantic encoding via mantissa packing, substrate-driven recipe-controlled synthesis, Tree-sitter-grammar bidirectional content codec, three-scale OODA Gödel engine on substrate, cross-model consensus surface via firefly POINTZM, and others — are the original work of the copyright holder and are reserved without limitation. Patent rights reserved.

See [LICENSE](LICENSE) for full terms. For licensing or commercial inquiries, contact the copyright holder.

---

<div align="center">

**Hartonomous**

*Universal infrastructure for structured knowledge*

</div>
