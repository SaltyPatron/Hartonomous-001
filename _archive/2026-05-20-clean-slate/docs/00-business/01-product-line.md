# Product Line — Substrate Family

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Sales, product, customers, investors. Engineering reference for what each product is, mechanically.

---

## The product line in one frame

The substrate is the factory. Every product is a query against substrate state, materialized through the appropriate recomposer. The factory operates once; the products are spawned at I/O cost. Pricing reflects the asymmetry: high cost to bring a customer's content into the substrate (one-time ingestion), near-zero cost per spawned daughter.

Five product surfaces today. Each is a SQL function with documented signature, contract, and SLA.

---

## Product 1 — Refinement-as-Service

**Description:** Customer hands over a safetensors file and (optionally) a proprietary corpus. Substrate ingests both. Cross-source corroboration through arena-Glicko mechanics automatically refines the model's attestations against the substrate's accumulated curated knowledge and other ingested teachers. Substrate exports a refined version preserving the original architecture exactly.

**Output:** A safetensors file with the same architecture, same tokenizer, same `config.json` semantics. Below-significance-threshold weights are zero (sparse). Above-threshold weights carry consensus signal across sources. Drop-in replacement for the original.

**Customer experience:**
1. Upload model + (optional) corpus to ingestion endpoint
2. Substrate ingests (one-time cost; varies with model size, typically minutes to a few hours)
3. Substrate exports refined model
4. Customer downloads, deploys identically to current setup

**Pricing model:** Tiered by ingestion compute (model size) plus optional storage retainer. Re-export from improved substrate state in subsequent quarters: lower tier (just I/O).

**Mechanism:** see `10-architecture/06-recomposer-contract.md` for how the recomposer reproduces the source architecture from substrate state. The substrate doesn't decide what to refine — it refines automatically through content-addressed edge convergence and Glicko-2 arena resolution. Export is the demonstration.

**Differentiation from existing methods:**
- Not student distillation: same model, same identity, smaller/cleaner.
- Not pruning: substrate ADDS signal where curated sources fill in.
- Not fine-tuning: no gradient descent, no destabilization of unrelated capabilities.
- Not quantization: no precision loss; sparsity from significance threshold, not bit reduction.

**Falsifiable contract:** Customer's deployment infrastructure (vLLM, llama.cpp, transformers, TGI, custom) loads the refined safetensors with no code changes. Refined model passes the customer's existing test suite at equal or better scores. Refined file is smaller than original after sparse-tensor compression.

---

## Product 2 — Laplace Originals

**Description:** Anthony-designed architectures populated by substrate state. Each Laplace original is a production-grade safetensors file producing inference outputs derived from the substrate's accumulated knowledge across all ingested sources.

**The family roster (initial):**

| Family | Variants | Modality | Notes |
|---|---|---|---|
| Laplace-Linguistics | -S, -M, -L, -XL | Text/multilingual | First commercial deliverable. Linguistic-arena-driven distillation. |
| Laplace-Coder | -S, -M, -L, -XL, -MoE | Code | Programming-domain distillation; tiny-codes + ingested coding LLMs as primary sources. |
| Laplace-Reason | -S, -M, -L, -XL, -MoE | Text/general | Reasoning-arena-driven; consensus across ingested frontier LLMs. |
| Laplace-VL | -S, -M, -L | Vision-Language | Cross-modal alignment from Florence + Grounding-DINO + Qwen-VL family. |
| Laplace-Vision | -Detect, -Classify, -Segment | Vision | Detection-transformer family from DETR variants. |
| Laplace-Audio | -ASR, -TTS, -Music, -Foundation | Audio | Speech, music, sound understanding from SAM-audio, Granite, Canary, Fish, Music-Flamingo. |
| Laplace-Multimodal | -Frontier | All modalities | Joint substrate distillation; cross-modality arenas. |
| Laplace-Embed | -Text, -Code, -VL, -Multimodal | Embedding | Embedding distillations for downstream retrieval. |
| Laplace-Rerank | -Text, -Code, -VL | Reranker | Cross-encoder relevance scoring. |
| Laplace-Diffuse | -Image, -Video | Generative | Conditional diffusion derived from FLUX-class evidence. |

**Customer experience:** Standard model release. Customer downloads from registry (HuggingFace-format directory: `config.json`, `tokenizer.json`, `model.safetensors` or sharded equivalent). Deploys to standard inference infrastructure. Same as any LLM/VL/etc. release.

**Pricing model:** Per-download licensing or open-source release per family. Variants may have different licensing tiers.

**Mechanism:** Anthony specifies the architecture; the recomposer fills it from substrate state filtered by the relevant arena recipe (linguistic for Laplace-Linguistics, coding for Laplace-Coder, etc.). Each release is a frozen snapshot of substrate state at that moment. Future releases (Laplace-Linguistics 1.0 → 1.1 → 2.0) follow substrate accumulation.

**Differentiation:** Every weight has provenance. Cross-source consensus structurally beats single-source training noise. Models are produced from a common substrate, so cross-family compatibility (e.g., Laplace-Linguistics's tokenizer being compatible with Laplace-Coder's tokenizer because both derive from the same substrate vocabulary) is automatic.

---

## Product 3 — Inference-as-Service (Live Substrate Queries)

**Description:** Customer connects to a substrate instance and issues SQL queries that drive inference, transformation, generation, comparison, or analysis. The substrate runs A\* over its edge graph with per-hop filtering specified by the customer. Every response includes a provenance trace.

**Customer experience:** REST or gRPC endpoint accepting SQL queries (or higher-level operation requests that compile to SQL). Returns JSON results with entity hashes, paths, provenance chains, and confidence scores. Latency target: <100ms per query (warm cache, modest depth) for production deployments.

**Pricing model:** Per-query, with tiered traversal depth and substrate-snapshot freshness. Enterprise SLA available with dedicated substrate instances.

**Mechanism:** see `10-architecture/07-inference-engine.md`. The customer can specify per-query traversal recipes — which arenas to consult, which provenance to consult, edge type filters, modality filters, language filters, recency filters. Different requests can use different recipes; the customer's API key may carry default recipes; per-request overrides are first-class.

**Differentiation from chat-completion APIs:**
- Provenance traces, not opaque responses
- Per-hop filtering control: customers can specify which substrate slice to consult
- No context window limits: prompts are substrate content, history is graph state
- Hallucination-impossible: responses come from edge traversal, not probability sampling
- Architecture-free: customers don't pick a "model"; they pick a query recipe

---

## Product 4 — Custom Architecture Synthesis (Custom-Architecture-Synthesis)

**Description:** Customer specifies a target architecture (any shape, any modality combination, any size) and a substrate selection recipe. The substrate produces a model matching the spec.

**Customer experience:** Customer submits an architecture specification (`config.json`-like, with layer count, hidden dimensions, head counts, MoE structure, attention variant, position encoding choice, vocabulary, etc.) plus a substrate recipe (which arenas, which provenance, which significance threshold). Substrate runs the recomposer; emits the safetensors directory; customer deploys.

**Pricing model:** Engineering consult plus per-export. Higher tier than refinement because the architecture choices may require recomposer engineering work for novel structures.

**Mechanism:** Universal recomposer driven by architecture spec. For known architecture families (decoder transformer, vision transformer, MoE variants), existing recomposer paths apply. For genuinely novel architectures, recomposer extension is required as part of the engagement.

**Differentiation:** Heterogeneous-expert MoE (different expert sizes per cluster), cross-architecture distillation (encoder ↔ decoder), MoE↔monolith conversion (dense↔sparse), modality crossover (text+vision merge into joint architecture). None of these are achievable with conventional ML methods at production quality.

---

## Product 5 — Substrate-as-Product (Enterprise On-Premise)

**Description:** Enterprise customer runs their own substrate instance on-premise. The customer's content stays internal; their refined models are produced by their substrate; their inference is local.

**Customer experience:** Substrate distributable (PostgreSQL extension + decomposers + recomposers + cognitive surface + monitoring). Customer deploys per their infrastructure standards. Optional managed-substrate offering with operator support.

**Pricing model:** License plus support tier. Customer also receives ingestion priors from the central Hartonomous substrate (the substrate's accumulated curated knowledge) on first install.

**Mechanism:** Deploy entire stack as documented in `30-operations/00-deployment.md`. Customer's substrate accumulates evidence from their content; when sufficient mass exists, it can produce arbitrary daughters.

**Differentiation:** No customer data leaves the premises. Ingestion of customer-confidential corpora and customer-proprietary models becomes part of their substrate, used only for their own products.

---

## Product offering matrix

|  | Refine Customer Model | Laplace Originals | Live Inference | Custom Architecture | On-Prem Substrate |
|---|---|---|---|---|---|
| **Substrate runs at:** | Hartonomous | Hartonomous | Hartonomous | Hartonomous | Customer |
| **Output format:** | safetensors | safetensors | SQL responses | safetensors | full stack |
| **Customer architecture knowledge required:** | None | None | None | High | Operational |
| **Marginal cost per output:** | Near-zero (I/O) | Near-zero (I/O) | Per-query | Per-export plus consult | Substrate compute |
| **Customer dependence on Hartonomous infra:** | One-time ingest | One-time download | Continuous | One-time | None |
| **First commercial deliverable:** | Yes | Yes (Laplace-Linguistics) | Yes | Yes | Future |

## What this catalog implies for engineering priorities

The first three products (Refinement-as-Service, Laplace-Linguistics original, Inference-as-Service) share a common substrate; the differences are entirely in the recomposer specs and the SQL query surface. Engineering should produce ALL THREE as a single integrated effort, not as separate products.

Specifically: the same recomposer engine that builds refined-Llama-4-Maverick from substrate state also builds Laplace-Linguistics from substrate state. The same A\* traversal engine that powers live inference also powers the recomposer's edge-fetching. The same SQL functions that customers call for inference are used internally by recomposers for query specs.

This means the path to first-customer revenue is: build the substrate, ingest enough sources to produce meaningful exports, validate the recomposer on one ingested model (refinement-of-Llama-4-Maverick gate), ship refinement-as-service and Laplace-Linguistics simultaneously, then expose the live inference surface as a third product on the same backend. Three products, one engineering effort.

## Cross-references

- How the recomposer produces refined models: `10-architecture/06-recomposer-contract.md`
- How per-hop filtering enables custom inference recipes: `10-architecture/07-inference-engine.md`
- Why competitors can't replicate any of this: `00-business/06-competitive-moats.md`
- Pricing model details: `00-business/04-pricing-model.md`
- Customer segmentation and use cases: `00-business/03-customer-segments.md`
