# Pricing Model

**Status:** Canonical (initial); subject to market validation
**Last verified:** 2026-04-29

---

## Pricing principles

The substrate's cost structure is asymmetric:
- High one-time cost: building substrate (engineering + ingestion compute + curated data acquisition).
- High per-customer cost: ingesting their model and corpus (compute + storage).
- Near-zero per-export cost: producing daughters from established substrate state.

Pricing reflects this. Up-front commitments fund substrate-build. Per-export fees are profitable margin.

## Refinement-as-Service (Segment 1 customers)

**Annual contract structure:**

| Tier | Annual fee | Includes |
|---|---|---|
| Basic | $250K | One model class (e.g., 7B-class), quarterly re-export, standard SLA |
| Professional | $750K | Up to three model classes, monthly re-export, priority support, customer-specific provenance |
| Enterprise | $2M+ | Multiple model classes, on-demand re-export, dedicated substrate slice, custom arenas, on-call support |

**Per-export fee** (above contract minimums): $5K–$50K per export depending on model size and whether substrate state has materially advanced since last export.

## Laplace originals (open-source vs commercial)

**Open-source releases:** Selected Laplace family models released under permissive licenses (Apache 2.0 or similar). Drives ecosystem adoption.

**Commercial releases:** Premium variants (frontier-scale, custom-architecture) released under commercial license. Pricing per organizational deployment:

| License tier | Annual fee | Use case |
|---|---|---|
| Startup | $50K | <100 employees, internal use only |
| Mid-market | $250K | 100–1000 employees, internal + customer-facing products |
| Enterprise | $1M+ | >1000 employees or unrestricted use |

## Inference-as-Service (Segment 3 customers)

**Per-query pricing:**

| Tier | Per-query | Latency SLA | Volume |
|---|---|---|---|
| Basic | $0.001 | <100ms p99 | unlimited |
| Professional | $0.0005 + $5K base/month | <50ms p99 | 100M queries/month |
| Enterprise | $0.0001 + $25K base/month | <20ms p99 | 1B+ queries/month |

Pricing structurally lower than competitive LLM APIs ($0.01–$0.10 per token) because substrate inference is CPU-only.

## Custom Architecture (Segment 2 customers)

**Engineering consult model:**

| Phase | Fee | Deliverable |
|---|---|---|
| Architecture design | $100K–$500K | Reviewed and approved architecture spec |
| First export | $250K–$2M | Production-ready custom model + recomposer extensions |
| Annual support | $250K+ | Re-exports, updates, performance tuning |

## Substrate-as-Product (Segment 4 customers)

**Per-deployment licensing:**

| Component | Fee |
|---|---|
| Substrate license | $1M–$10M one-time |
| Annual support | $250K–$1M |
| Per-update fee | included in support tier |
| Initial substrate priors | $250K (first install) |

## Research / academic

**Discounted licensing:**

- Academic Laplace family: $25K–$100K annually
- Substrate-as-research-tool (per-query API): $5K/month flat with research SLA
- Custom research engagements: case-by-case

## Pricing power justification

Substrate-derived models are unique in the market:
- No competitor produces refined safetensors at I/O cost.
- No competitor offers per-hop-filtered inference.
- No competitor produces architecture-flexible models from one substrate.
- No competitor offers provenance-traceable AI outputs at substrate's depth.

Pricing reflects unique value, not commodity model-API rates.

## Cross-references

- Customer segments: `00-business/03-customer-segments.md`
- Product line: `00-business/01-product-line.md`
- Competitive moats: `00-business/06-competitive-moats.md`
