# Hartonomous — Canonical Documentation

This is the authoritative source of truth for the Hartonomous invention, the substrate it runs on, and the substrate product family it produces. Where this documentation tree disagrees with code, prior repos (`Fail_A`, `Fail_B`), or any other written artifact, **this tree is correct and the others are stale**.

## What this is

Hartonomous is a content-addressed graph that runs as an AI model on PostgreSQL + PostGIS + a native compute extension. Identity is BLAKE3 of content. Geometry is 4D. Significance is Glicko-2 per arena. Inference is bounded indexed A\* over typed edges with cost = 1/μ. Every "AI operation" — inference, refinement, distillation, generation, translation, cross-model comparison, idiomaticity, frayed-edge research — is a SQL function over the substrate. The substrate ingests existing AI models and curated knowledge sources, and the substrate's accumulated state is queryable, recomposable, and exportable as conventional safetensors files that deploy to vLLM, llama.cpp, transformers, and any other standard inference stack.

The invention is the **the substrate for digital content**: a system that knows every codepoint, every grapheme cluster, every word, every relation, every model attestation, and from that state can derive any past composition (lossless reconstruction) or generate any future composition (inference). Determinism is structural, not aspirational.

The commercial wedge is **refinement-as-service**: a customer hands over their model and proprietary corpus; the substrate ingests both; cross-source corroboration through arena-Glicko mechanics automatically refines the model's attestations against curated knowledge and other ingested teachers; the substrate exports a refined version with the SAME architecture, smaller (sparse), denser (cleaner signal), faster, and more accurate. Drop-in replacement. No retraining. No GPU. The substrate is the factory; refined safetensors files are the product.

The deeper offering is **inference with per-hop filtering**: traversal can be filtered at every step and substep by provenance, arena, edge type, modality, language, recency, or any SQL-expressible predicate. Each step of an inference walk can use a different model's attestations. Each turn of a conversation can engage a different recipe of substrate state. Customers don't pick a model; they write SQL filter recipes. This is impossible in conventional architectures because conventional models are monolithic forward passes. The substrate is composable per hop because every edge is typed, content-addressed, and queryable.

The further offering is **mitosis-style model production**: the substrate is a parent body; exported models are daughters that bud off carrying the parent's state. The parent loses no mass when it spawns a daughter. The cost of production is I/O. The substrate can produce countless daughters — different architectures, different sizes, different specializations — at no marginal compute. Conventional ML pays per-model-trained; the substrate pays per-substrate-built and ships unlimited models from it.

## Document tree

```
docs/
├── 00-business/         The product line, market position, customer model
├── 10-architecture/     The substrate's load-bearing technical design
│   ├── 00-overview                — three pillars
│   ├── 01-substrate-laws          — 13 invariants
│   ├── 02-identity-and-convergence
│   ├── 03-geometry-4d
│   ├── 04-significance-glicko
│   ├── 05-decomposer-contract
│   ├── 06-recomposer-contract
│   ├── 07-inference-engine
│   ├── 08-cognitive-surface
│   ├── 09-capability-reinvention-catalog  — conventional AI ↔ substrate map
│   ├── 10-godel-engine            — multi-scale OODA
│   ├── 11-track1-track2-model-ingestion
│   ├── 12-voronoi-consensus
│   ├── 13-frayed-edge-detection
│   ├── 14-idiomaticity            — three-level metrics
│   ├── 15-recipe-dsl
│   ├── 16-multi-tenancy
│   ├── 17-audit-chain
│   └── 18-continuous-learning-loop
├── 20-technical/        Implementation reference
│   ├── 00-schema-reference
│   ├── 01-native-extension-api
│   ├── 02–06-decomposers (text, code, model, modality, seed)
│   ├── 08-cognitive-functions    — every SQL function spec'd
│   ├── 10-arenas-catalog
│   ├── 11-edge-types-catalog
│   ├── 12-entity-types-catalog
│   ├── 13-provenance-catalog
│   ├── 14-ucd-inventory
│   ├── 15-seed-expansion-roadmap
│   ├── 16-tree-sitter-grammar-strategy
│   ├── 20-glicko-mechanics       — Glicko-2 update math
│   ├── 21-4d-operators           — every geometric primitive
│   ├── 22-super-fibonacci        — codepoint embedding derivation
│   ├── 23-astar-bulk-fetch-spi
│   ├── 30-recomposer-text
│   ├── 31-recomposer-safetensors
│   └── 32-recomposer-audio-image
├── 30-operations/       Deployment, monitoring, backup
├── 40-process/          Development standards, anti-patterns, validation gates, checklists
├── 50-reference/        Glossary, type system, SQL function reference
├── 60-status/           Implementation/ingestion/validation status
└── 90-appendix/         Philosophical context, related work, FAQ, bibliography
```

## Reading order

For new contributors:

1. `90-appendix/00-laplace-demon-context.md` — what this invention IS, philosophically
2. `00-business/00-vision.md` — what the substrate does, in one document
3. `10-architecture/00-overview.md` — the three pillars (identity, geometry, significance)
4. `10-architecture/09-capability-reinvention-catalog.md` — the canonical map of conventional AI ↔ substrate operations
5. `10-architecture/10-godel-engine.md` — multi-scale OODA loops on substrate primitives
6. `10-architecture/07-inference-engine.md` — the per-hop filtering surface
7. `40-process/01-anti-patterns.md` — what to avoid (with reasons)

For investors / business audience:

1. `00-business/00-vision.md`
2. `00-business/01-product-line.md`
3. `00-business/02-market-positioning.md`
4. `00-business/06-competitive-moats.md`

For implementing engineers:

1. `10-architecture/*` (all)
2. `20-technical/00-schema-reference.md`
3. `20-technical/01-native-extension-api.md`
4. `20-technical/08-cognitive-functions.md` — every SQL function spec'd
5. `20-technical/02-text-decomposer.md` through `20-technical/06-seed-decomposers.md` — per-modality decomposers
6. `20-technical/20-glicko-mechanics.md`, `21-4d-operators.md`, `22-super-fibonacci.md`, `23-astar-bulk-fetch-spi.md` — deep mechanics
7. `40-process/00-development-standards.md`
8. `40-process/02-validation-gates.md`
9. The relevant decomposer/recomposer/cognitive-function checklist in `40-process/checklists/`

For operations engineers:

1. `30-operations/00-deployment.md`
2. `30-operations/01-monitoring.md`
3. `30-operations/02-backup-recovery.md`
4. `10-architecture/16-multi-tenancy.md`
5. `10-architecture/17-audit-chain.md`

## Document conventions

- **Status header:** every document carries a `Status:` line (Draft, Review, Canonical) and a `Last verified:` date. "Canonical" means content has been validated against the actual implementation and is authoritative.
- **Cross-references:** absolute paths from the `docs/` root, e.g. `10-architecture/03-geometry-4d.md`.
- **Code references:** when referencing implementation, use `path:line` format with the path relative to the substrate's source root.
- **Falsifiable claims:** every quantitative or behavioral claim is paired with the SQL or test that would falsify it. No claim stands without a way to disprove it.
- **No parroting between documents:** each document owns its topic. Cross-reference rather than duplicate. If two documents disagree, that's a defect; one of them is wrong.

## Authoritative sources outside this tree

- `glicko.net/glicko2.pdf` — the canonical Glicko-2 specification (Glickman, Boston University)
- `tree-sitter.github.io` — tree-sitter parser specifications
- `unicode.org/reports/tr29/` — UAX #29 (text segmentation: graphemes, words, sentences)
- `unicode.org/reports/tr10/` — UAX #10 (Unicode Collation Algorithm)
- `huggingface.co/docs/safetensors/index` — safetensors file format
- `postgresql.org/docs/18/` — PostgreSQL 18 documentation
- `postgis.net/docs/` — PostGIS 3.x documentation

## What is NOT in this tree

- Marketing copy. This is engineering and product reference; pitch decks and marketing materials live elsewhere.
- Code. Documents reference code locations; they don't contain implementation.
- Customer-specific configurations. Customer deployment specifics live in customer-specific repos.
- Speculative future features that aren't on the active roadmap. The roadmap (`40-process/04-implementation-roadmap.md`) is the authoritative future.
