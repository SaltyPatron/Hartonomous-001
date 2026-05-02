# Go-to-Market

**Status:** Canonical (initial)
**Last verified:** 2026-04-29

---

## Phasing

### Phase 1 — Pre-revenue (M0 – M9)

**Activities:**
- Build the substrate to the first commercial gate (refinement of Qwen2.5-Coder-3B passes P1 quality benchmark)
- Maintain documentation tree as canonical reference
- Begin technical content marketing (blog posts on substrate technical architecture, demonstrating per-hop filtering and refinement mechanics)
- Identify and engage 3-5 design-partner customers for refinement-as-service

**No revenue.** Avoid premature commercial commitments.

**Exit criteria:** P1 gate passes. First refinement output validates the substrate's commercial premise.

### Phase 2 — Design partners (M10 – M11)

**Activities:**
- Onboard 3-5 design partners under Refinement-as-Service Basic tier with discounted/founders pricing
- Build out substrate breadth (Wiktionary, Tatoeba, multi-model)
- Iterate recomposer based on design-partner feedback
- Generate case studies showing refinement quality improvement

**Revenue:** $750K–$2M ARR from design partners.

**Exit criteria:** P2 gate passes (Laplace-Linguistics ships). At least one design partner achieves successful production deployment of refined model.

### Phase 3 — Initial commercial launch (M12 – M13)

**Activities:**
- Launch Laplace-Linguistics-7B publicly (commercial + open-source variants)
- Launch refinement-as-service general availability
- Stand up inference-as-service product (substrate cognitive surface as REST/gRPC)
- Build customer success and support functions
- Begin enterprise sales cycle for Segment 1 and Segment 3 customers

**Revenue target:** $5M–$15M ARR within 12 months of launch.

**Exit criteria:** P3 gate passes. Inference SLA met for production workloads. At least 10 paying customers across segments.

### Phase 4 — Scale (M14+)

**Activities:**
- Custom architecture business (Segment 2) for select engagements
- On-premise substrate offering for Segment 4
- International expansion
- Strategic partnerships with model labs, cloud providers, regulated-industry vendors
- Research lab engagement program

**Revenue target:** $25M–$100M ARR by year 3.

## Strategic partnerships

**Frontier model labs (Llama, Qwen, DeepSeek, Mistral, etc.):**
- Position substrate as accumulating evidence sink — substrate users discover quality issues that benefit lab future training
- Co-marketing: "Llama-4-Maverick refined by Hartonomous"
- Integration: substrate as preferred refinement and analysis layer for their models

**Cloud providers (AWS, Azure, GCP):**
- Substrate-as-a-service offering on cloud marketplaces
- Joint go-to-market for enterprise customers
- Hardware partnerships: substrate runs on standard cloud CPU instances

**Regulated-industry vendors (FIS for finance, Epic for healthcare, LexisNexis for legal):**
- White-label substrate deployments for their customer bases
- Compliance-tuned arena recipes per industry
- Joint sales motions

**Model evaluation and benchmark organizations (HuggingFace, MLPerf, Stanford HELM):**
- Substrate-derived models on public leaderboards demonstrate technical performance
- Substrate as analysis tool for benchmark comparisons

## Content strategy

**Technical content (substrate/engineering audience):**
- Blog posts on substrate architecture (the three pillars, per-hop filtering, mitosis economics)
- Open-source the substrate documentation tree
- Conference talks at PostgreSQL events, ML systems venues, AI infrastructure conferences

**Business content (customer audience):**
- Case studies from design partners
- Whitepapers on AI auditability and provenance for regulated industries
- ROI calculators showing substrate vs conventional fine-tuning costs

**Investor content:**
- Vision narrative emphasizing the inversion of AI's cost structure
- Technical due diligence package showing falsifiable claims and validation gates
- Reference architecture documentation

## Sales motion

**Refinement-as-Service:** Inside sales + technical pre-sales engineering. Customers self-discover via content; pre-sales validates technical fit; quick-time-to-value (refined model in days).

**Custom Architecture:** Field sales + senior architect. Long sales cycles (3-6 months); high contract value justifies; engineering deeply involved in scoping.

**Inference-as-Service:** Self-service tier (online signup, credit card billing) for Basic; sales-led for Pro and Enterprise.

**On-Premise Substrate:** Field sales + executive sponsorship. Long sales cycles (6-12 months). Sovereign-data customers; substantial deal sizes.

## Cross-references

- Customer segments: `00-business/03-customer-segments.md`
- Pricing model: `00-business/04-pricing-model.md`
- Roadmap: `40-process/04-implementation-roadmap.md`
