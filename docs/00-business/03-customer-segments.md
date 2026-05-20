# Customer Segments

**Status:** Canonical (initial)
**Last verified:** 2026-04-29

---

## Segment 1 — Companies with deployed fine-tuned LLMs (Refinement-as-Service primary buyers)

**Profile:** Mid-to-large enterprises that have invested in fine-tuning an open-weight model (Llama, Qwen, Mistral, etc.) on their proprietary data. The fine-tuned model is deployed on vLLM/TGI/llama.cpp/their own stack. They want continuous quality improvement without recurring fine-tuning compute.

**Pain points:**
- Fine-tuning is expensive (GPU hours per cycle).
- Catastrophic forgetting risk on each fine-tune.
- Stale model decay as their domain evolves.
- No audit trail on model behavior for compliance.

**Substrate value:**
- Refinement-as-service: ingest their model + their corpus → re-export refined.
- Recurring service: re-export every quarter as substrate accumulates more evidence; their deployed model gets better without new GPU spending.
- Provenance traces for compliance.

**Pricing tier:** Annual contract with base ingestion fee + per-export fee. Typical contract value: $250K–$2M ARR per customer.

## Segment 2 — Specialized AI startups (Custom Architecture buyers)

**Profile:** AI-product startups whose core product is a specialized AI capability (legal analysis, medical document understanding, code review). They need a model fitted to their domain that doesn't exist in the open ecosystem.

**Pain points:**
- Building a model from scratch is multi-million-dollar.
- Fine-tuning a generalist model under-fits their domain.
- They lack ML infra teams to manage training cycles.

**Substrate value:**
- Custom-Architecture-Synthesis: customer specifies architecture; substrate fills it from substrate state filtered to their domain.
- No GPU costs.
- Continuous improvement via re-export.
- Audit trail for regulatory positioning.

**Pricing tier:** Engineering consult + per-export. Typical contract: $500K–$5M for first model + recurring per-export fees.

## Segment 3 — Compliance-regulated enterprises (Inference-as-Service buyers)

**Profile:** Regulated industries (financial services, healthcare, legal, government) facing AI auditability mandates (EU AI Act, US executive orders, FINRA, HIPAA, GDPR Article 22).

**Pain points:**
- Conventional LLM APIs offer no provenance traces.
- Internal LLM deployments require expensive infrastructure.
- Audit chains aren't structurally available from training data → weights → outputs.

**Substrate value:**
- Inference-as-service with full provenance traces per response.
- Per-hop filtering recipes can restrict inference to compliance-vetted source set.
- Substrate's audit chain meets regulatory inspection requirements.

**Pricing tier:** Per-query plus enterprise SLA. Typical: $50K–$500K MRR for high-volume regulated workloads.

## Segment 4 — On-premise enterprise (Substrate-as-Product buyers)

**Profile:** Enterprises whose data cannot leave their premises (defense, intelligence, sovereign workloads, ultra-confidential corporate data). They want substrate capabilities but on their own infrastructure.

**Pain points:**
- SaaS AI is a non-starter due to data sovereignty.
- Internal AI infrastructure is expensive and underdelivered.
- No cross-customer learning (substrate elsewhere doesn't help them; their substrate doesn't help others).

**Substrate value:**
- Substrate distributable: full PG + extension + decomposers + recomposers + cognitive surface.
- One-time install with substrate priors from Hartonomous's accumulated state.
- All customer data stays internal; their substrate becomes their competitive moat.

**Pricing tier:** License + support. Typical: $1M–$10M license + $250K–$1M annual support.

## Segment 5 — AI research labs (substrate product family early adopters)

**Profile:** Academic and corporate research labs benchmarking model architectures, studying training dynamics, doing model analysis research.

**Pain points:**
- Cross-architecture comparison is hard (different tokenizers, different training).
- Model analysis tools are model-specific and can't be combined.
- Reproducibility issues with stochastic training.

**Substrate value:**
- Cross-model analysis queries (Hausdorff over fireflies, antipodal violations, etc.) as SQL.
- Laplace originals as research baselines: deterministic, reproducible, architecture-flexible.
- Substrate-as-research-tool: ingest a frontier model, compare to consensus, publish.

**Pricing tier:** Academic discount. Typical: $25K–$100K annual licenses.

## Cross-references

- Product line: `00-business/01-product-line.md`
- Pricing model: `00-business/04-pricing-model.md`
- Market positioning: `00-business/02-market-positioning.md`
