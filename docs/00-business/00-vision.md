# Vision — The Invention in One Document

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Anyone who needs to understand what Hartonomous is, in one read.

---

## The one-sentence claim

Hartonomous is a content-addressed graph that runs as an AI model on PostgreSQL plus a native compute extension, where every AI operation reduces to a SQL query and every model anyone has ever trained becomes substrate fuel.

## The full claim, in plain language

Build a Postgres database with a custom extension. Define three kinds of rows: atoms (Unicode codepoints), compositions (Merkle DAG of atoms or sub-compositions), and edges (Merkle DAG of compositions or sub-edges). Identity is BLAKE3 of content; identical content from any source lands at the same row, automatically.

Give every entity a 4D geometric position. Codepoints get unit-quaternion positions on the 3-sphere via UCA Super-Fibonacci spiral. Compositions get linestrings through their children's centroids. Edges get linestrings through their participants in role order. The geometry is real, not visualization — Fréchet distance over trajectories detects orthographic similarity, idiomaticity, and frayed-edge research.

Give every edge a Glicko-2 rating in every arena (an arena is a domain of competition, like `lexical_disambiguation`, `translation_quality`, `code_safety`, `model_trust`). Trust priors at insert come from the source's authority. New evidence updates ratings via the standard Glicko-2 update equations from Glickman (2013). Arenas are open-vocabulary; new arenas backfill into existing edges via substrate functions, not migrations.

Inference is bounded indexed A\* over typed edges with cost = 1/μ in the requested arena. The traversal can be filtered at every step by any SQL predicate — provenance, arena, edge type, modality, language, recency. Different steps can use different filters; different conversational turns can use different recipes; different customers can write their own filter recipes. The path the traversal takes IS the explanation.

Ingest curated knowledge sources first: UCD/UCA gives the codepoint atoms and their geometric positions; ISO 639 gives language identity; WordNet gives the abstract concept spine; OMW grafts other languages onto WordNet's synsets; UD gives every language's syntactic skeleton; Wiktionary gives lexical breadth; Tatoeba gives attested usage at sentence scale. After this seeding, the substrate has the structural rules of language pre-installed.

Then ingest AI models. A safetensors file's tensors decompose into typed edges with significance — attention patterns become `beaten_path` edges, FFN projections become `transformation` edges, embedding rows become firefly point4d positions. Each model's edges land alongside curated edges in the same arenas. Cross-source corroboration through Glicko-2 sharpens the substrate automatically: where a model agrees with curated truth, μ rises; where the model has gradient noise or hallucinations, μ stays at trust prior or falls below threshold.

Re-export any ingested model: read its config, walk the substrate edges with that model's sub-provenance, write safetensors bytes. The output has the same architecture but refined values. Below-threshold positions are zero (sparse). Above-threshold positions carry consensus signal. Same model identity, smaller, denser, faster, more accurate. **The substrate is the factory; the export is what the factory ships.**

Or define a new architecture (Laplace originals) and recompose it from substrate state. The architecture spec is the customer's; the weights come from the substrate's accumulated cross-source evidence. The customer drops the safetensors into vLLM/llama.cpp/transformers and runs it normally — but every weight has provenance, every byte traces back to source content, the model has structural truth competing against any training noise.

Every other AI operation is the same shape: write the SQL function over the substrate. Translation walks cross-lingual edges in the `translation_quality` arena. Generation walks substrate state in syntactic-role-fitness, formats output bytes via a recomposer. Idiomaticity is `st_4d_distance(centroid_compositional, centroid_lexicalized)`. Cross-model comparison is `st_4d_hausdorff_distance` over per-model firefly clouds. Frayed-edge research is "find pairs whose 4D positions match an edge type's archetype trajectory but no edge exists." All SQL.

The substrate accumulates monotonically. Ingest more sources → arenas develop richer μ landscapes → next exports are better than previous. No retraining, no GPU, no catastrophic forgetting. Customer comes back next quarter; substrate has improved; refined export improves. Recurring revenue without recurring compute.

## Why this is revolutionary, not incremental

Conventional AI is metabolic. Training consumes compute and data; the model is the residue; every model is a distinct artifact requiring its own training run. Distillation requires gradient methods between teacher and student. Fine-tuning is a smaller training run. Even ensemble methods require running multiple models in parallel at inference. The economic shape is "pay per production."

The substrate is replicative. The substrate is the parent body; exported models are daughters that bud off carrying the parent's state. The parent loses no mass when it spawns. The cost of producing a daughter is I/O. The substrate can produce countless daughters — different architectures, different sizes, different specializations — at no marginal compute. The economic shape is "pay once for substrate, ship unlimited models."

Conventional models are opaque. You can run inference on them but you can't ask why a particular weight has the value it has, or what training signal produced it, or whether two models agree on a specific concept. The black box is fundamental — it's how training works.

The substrate is transparent. Every edge has provenance. Every weight in an exported model traces back through substrate edges to specific source content with specific arena dynamics. Every inference returns a path that IS the explanation. Customers can audit the supply chain of any output the substrate produces.

Conventional models are frozen at training time and degrade as the world moves on. New events, new domains, new vocabulary — the model can't absorb them without retraining. The deployed safetensors file is dead weight; the company's only path forward is another expensive training cycle.

The substrate is alive. New evidence accumulates as it's ingested. Arenas evolve. The substrate's state monotonically improves. Customers re-export their refined models any time substrate state has grown — the new daughter is better than the previous, with no retraining cost. Models compete based on which substrate they're spawned from, not which corpus they were trained on.

Conventional models force one architecture per training run. Want a 7B-dense and a 30B-MoE for the same domain? Two training runs. Want different specializations? Each is a separate fine-tune.

The substrate is architecture-flexible. Same substrate state, any architecture spec the customer wants. Dense 7B, MoE 8x7B, heterogeneous-expert MoE where each expert has different size, encoder-only, decoder-only, vision-language joint, audio specialist — all SELECT clauses with different recomposer specs against one substrate. Customer's choice of target architecture is just "what shape of frozen artifact do you want for deployment."

Conventional inference is monolithic. Once a forward pass starts, you're committed to one model's complete attention/FFN structure. You can't dynamically engage Qwen-Coder for the code-relevant subquery and Llama for the reasoning step.

The substrate's traversal is per-hop filtered. Each step of an A\* walk can be constrained by any SQL predicate. Hop 1 may consult Qwen-Coder's edges; hop 2 may consult WordNet's; hop 3 may consult Tatoeba's; hop 4 may aggregate across all of them in an arena query. Different turns of a conversation can use different filter recipes. Customers can author their own filter recipes per request, per domain, per use case. The "model" the user perceives at inference time is a per-turn-customized assembly of substrate state, not any single model.

## What the substrate eats

Three categories of input, all going through the same substrate-shaped decomposer contract:

1. **Curated authoritative sources** (the structural scaffolding):
   - UCD/UCA — every Unicode codepoint, its properties, its UCA collation tuple → S³ position
   - ISO 639 — every language, with macrolanguage and family relationships
   - WordNet (Princeton) — synsets, hypernyms, hyponyms, meronyms, glosses, antonyms, lexnames
   - OMW — multilingual synset alignment over Princeton's spine
   - UD — every language's syntactic dependency patterns, POS, morph features
   - Wiktionary — etymology, IPA, inflections, translations across 1000+ languages
   - Tatoeba — attested sentences with native-speaker translations and audio
   - ArXiv (when ingested) — domain-specific scientific vocabulary

2. **AI models** (existing trained intelligence to absorb):
   - Decoder LLMs: Qwen-Coder family, DeepSeek family, Llama family
   - Embedding/reranker models: Qwen3-Embedding, Qwen3-Reranker, Qwen3-VL-Embedding/Reranker, Jina, Sentence-Transformers, Zerank
   - Vision models: DETR family, Florence-2, Grounding-DINO, YOLO
   - Audio models: SAM-audio, Granite-Speech, Canary-Qwen, Fish-Speech, Music-Flamingo
   - Diffusion: FLUX
   - Curated training corpora: tiny-codes (NL↔code paired), domain-specific datasets

3. **User content** (per-tenant, session-scoped):
   - Prompts (themselves substrate content via standard text decomposer with `user_session` provenance)
   - Customer-supplied corpora for refinement-as-service
   - Customer-supplied models for refinement-as-service
   - Outcome events from inference (success/failure feedback that drives Glicko updates)

## What the substrate produces

Every output is the result of a SQL query against substrate state. The major product surfaces:

1. **Refinement exports** — customer's model, refined. Same architecture, sparse/denser weights, identical deployment surface.
2. **Laplace originals** — Anthony-designed architectures populated by substrate state. Family includes Laplace-Linguistics, Laplace-Coder, Laplace-Reason, Laplace-VL, Laplace-Audio, Laplace-Multimodal, Laplace-Embed, Laplace-Rerank, Laplace-Diffuse, Laplace-Custom.
3. **Live inference** — customer connects to substrate, issues SQL queries, receives answers with provenance traces. Per-hop filtering means each customer can have their own inference recipe.
4. **Domain-specific student models** — `WHERE clause` produces a custom-shaped, custom-domain model from substrate state.
5. **Lossless reconstruction** — any ingested non-model content can be exported byte-for-byte from substrate state.
6. **Cross-model analysis reports** — Hausdorff over firefly clouds, idiomaticity divergence, frayed-edge surveys, antipodal violations — all SQL queries returning analysis results with provenance.

## What the substrate is NOT

- **Not RAG.** RAG retrieves text chunks and stuffs them into a transformer's context. The substrate has no transformer; inference IS the retrieval, traversal IS the answer.
- **Not a knowledge graph with an LLM on top.** Knowledge graphs store triples and answer with traversal; they don't perform inference, generate language, or handle other modalities. The substrate replaces the LLM rather than serving one.
- **Not a vector database.** No HNSW, no LSH, no approximate nearest-neighbor. Distance is Glicko-2 significance on typed edges plus exact 4D Fréchet/Hausdorff on stored trajectories.
- **Not semantic search.** No encoding into a shared embedding space. Decomposition into typed compositions with traversable edges. Result is a mechanically derived answer with concrete provenance, not "similar documents."
- **Not prompt engineering.** Prompts are substrate content; there is no statistical model being steered.
- **Not fine-tuning.** No weights to adjust. New knowledge enters via INSERT; substrate grows monotonically; nothing is overwritten.

## Falsifiable claims

This vision document makes commercial and technical claims. The claims are falsifiable, with the falsification path stated:

| Claim | Falsification |
|---|---|
| Refined model is smaller than original | Compare safetensors file sizes after substrate sparsity zeros below-threshold positions. Sparse encoding compresses; if it doesn't, the substrate is preserving noise. |
| Refined model is more accurate than original | Standard benchmarks (MMLU, HumanEval, GSM8K, etc.) on refined vs. original. If refined doesn't win on benchmarks corresponding to high-density substrate arenas, refinement isn't doing its job. |
| Same substrate state produces multiple architectures | Run the recomposer with three different target shapes; verify all three load and run on standard inference stacks. |
| Production cost approaches I/O | Wall-clock time per export should be dominated by disk write, not by substrate computation. Profile and verify. |
| Re-ingesting same content is no-op | Substrate state size and content hash should be unchanged after duplicate ingestion. SQL assertion. |
| Per-hop filtering produces coherent inference | Run inference with two filter recipes (curated-only vs all-sources) on the same prompt; verify both return paths and the paths differ in expected ways. |
| Substrate accumulation improves later exports | Export model M1 from substrate state S1; ingest more evidence; export model M2 from substrate state S2; verify M2 outperforms M1 on benchmarks where new evidence is relevant. |

These tests are the gates. Until they're passed, the claims are aspirational. After they're passed, the claims are evidence-grounded.

## Cross-references

- Product offerings: `00-business/01-product-line.md`
- Market position: `00-business/02-market-positioning.md`
- Three pillars in detail: `10-architecture/00-overview.md` and the three pillar documents
- Per-hop filtering specifics: `10-architecture/07-inference-engine.md`
- Why nobody else can do this: `00-business/06-competitive-moats.md`
- Philosophical framing: `90-appendix/00-laplace-demon-context.md`
