# Related Work — How Hartonomous Differs

**Status:** Canonical
**Audience:** Engineers and customers who pattern-match the substrate to similar-looking systems. Read this when you're tempted to call something a "knowledge graph with AI" or "RAG system."

---

## Knowledge graphs (Neo4j, Amazon Neptune, TigerGraph, Stardog, Ontotext, RDF/OWL)

**What they do:** Store typed triples (subject-predicate-object) and answer queries via graph traversal. Some support reasoning over OWL ontologies (subsumption, transitivity).

**How Hartonomous is different:**

1. **Identity is content-addressed via BLAKE3, not labeled.** Two sources attesting `(Whale, hypernym, Mammal)` produce one row, not two — deduplication is automatic via hash. Knowledge graphs use string identifiers; deduplication requires manual reconciliation.

2. **Inference replaces queries, not augments them.** A knowledge graph answers "show me all hypernym chains from Whale." Hartonomous answers "given this prompt, traverse to produce a coherent response with explanation." The traversal IS the inference; there's no LLM bolted on top.

3. **Significance per arena replaces edge weights.** Edges in KGs are typed but unweighted (or weighted by ad-hoc scores). Hartonomous edges have Glicko-2 ratings per arena, with cross-source corroboration dynamics.

4. **Geometry is first-class.** KGs have no spatial primitive. Hartonomous has 4D physicality with edge trajectories, Fréchet/Hausdorff distance, Voronoi consensus.

5. **The substrate replaces the model, not stores about the model.** Customers don't store model attestations in a KG and query them; they ingest models into Hartonomous as evidence sources.

## Vector databases (Pinecone, Weaviate, Qdrant, Chroma, Milvus, pgvector)

**What they do:** Store dense embedding vectors and perform approximate nearest-neighbor search via HNSW/LSH/IVF.

**How Hartonomous is different:**

1. **No ANN.** Distance is Glicko-2 significance on typed edges plus exact 4D Fréchet/Hausdorff on stored trajectories. No HNSW. No approximate search.

2. **No embedding-similarity-as-meaning.** Vector DBs assume similar embeddings = similar meaning. Hartonomous has no such assumption — relationships are explicit edges with provenance.

3. **Identity precludes the use case.** A vector DB's value is approximate retrieval over a corpus. Content-addressed identity in Hartonomous means convergent observations land at the same row; there's no need for similarity search to find duplicates.

4. **Geometry is structural, not similarity.** 4D positions encode UCA collation (codepoints) or Laplacian topology (fireflies), not embedding distance. Suffix similarity falls out from S³ adjacency, not cosine over training-derived vectors.

5. **No retrieval-then-LLM pipeline.** RAG (retrieval-augmented generation) is the canonical vector-DB consumer pattern. Hartonomous has no LLM to augment — inference IS traversal.

## Retrieval-augmented generation (RAG; LangChain, LlamaIndex, etc.)

**What they do:** Retrieve relevant chunks via embedding similarity from a vector DB; concatenate retrieved text into an LLM's context window; LLM produces output.

**How Hartonomous is different:**

1. **No transformer forward pass.** RAG depends on an LLM doing a forward pass on retrieved context. Hartonomous has no forward pass — inference is graph traversal.

2. **No retrieval boundary.** RAG separates retrieval (vector DB) from generation (LLM). Hartonomous has no separation — traversal IS retrieval IS inference.

3. **No context window.** RAG is bounded by the LLM's context window; chunked retrieval is a workaround. Hartonomous has no token window; prompts are substrate state, history is graph traversable.

4. **Per-hop filtering vs single-stage filtering.** RAG can filter at retrieval (which documents to include); after that, the LLM is monolithic. Hartonomous filters at every hop independently — different hops can use different filter recipes.

5. **Provenance traces, not citations.** RAG produces output that may reference retrieved chunks. Hartonomous produces output where every byte traces through substrate edges to provenance, machine-verifiable.

## Foundation models / LLMs (GPT-4, Claude, Llama, Qwen, Mistral, DeepSeek, etc.)

**What they do:** Pretrained transformer models producing token sequences via forward pass. Some are open-weight; some are API-only.

**How Hartonomous is different:**

1. **Hartonomous is upstream, not parallel.** Foundation models become evidence sources for the substrate via SafetensorsDecomposer. Their attestations land alongside curated knowledge. Substrate-mediated outputs are derived from their consensus.

2. **No training-cost-per-output.** Foundation models cost millions per pretraining run. Hartonomous pays substrate-build cost once; subsequent model exports are I/O.

3. **Refinement vs distillation.** Conventional distillation produces a different model (student). Hartonomous refinement produces the SAME model (same architecture, same identity) with improved weights via cross-source corroboration.

4. **Per-hop filtering is structural.** Customers can't compose Llama with Qwen at hop-granularity in conventional inference. Hartonomous lets per-hop filtering specify which model's attestations to consult at each step.

5. **Provenance is structural.** Foundation models cannot trace which training example produced which weight. Hartonomous can.

## Model merging / SOUP / TIES / DARE methods

**What they do:** Combine multiple fine-tuned models' weights via linear interpolation or sparse averaging to produce a single combined model.

**How Hartonomous is different:**

1. **Architecture-agnostic.** Model merging requires architectural compatibility (same layer structure). Hartonomous combines edges across architectures via the substrate's typed-edge representation.

2. **Glicko-2 mechanics replace ad-hoc averaging.** Merging methods choose interpolation weights heuristically. Hartonomous's arena-Glicko dynamics are principled and update under outcome events.

3. **Substrate is permanent; merging is one-shot.** Merged models are static after the merge. The substrate accumulates monotonically; future exports incorporate later evidence.

4. **No tokenizer mismatch problem.** Merging methods can't combine models with different tokenizers. Hartonomous's content-addressed identity makes tokenizers converge naturally.

## MoE upcycling / training (clone-then-fine-tune approaches)

**What they do:** Clone a dense model's FFN N times, add a router, fine-tune to specialize the experts.

**How Hartonomous is different:**

1. **No training.** SQL clustering of substrate edges by domain produces specialized experts. Recomposer projects to MoE architecture. No GPU.

2. **Heterogeneous experts.** Conventional MoE requires uniform expert shape. Hartonomous supports per-cluster expert sizing based on edge density.

3. **Domain-coherent from the start.** Conventional upcycling starts from N identical experts and hopes specialization emerges. Hartonomous starts from clustered substrate state and produces specialized experts.

## Cyc / Wolfram | Alpha (curated symbolic AI)

**What they do:** Manually encode rules, facts, and inference procedures. Cyc has decades of human effort encoding common-sense knowledge.

**How Hartonomous is different:**

1. **Curation + ingestion + arenas.** Hartonomous accepts curated sources (UCD, WordNet, UD) AND ingests AI models AND uses Glicko-2 arenas to handle disagreement automatically. Cyc relies on manual reconciliation.

2. **Architecture is queryable, not hand-coded inference.** Cyc has dedicated inference engines for each rule type. Hartonomous has one A\* engine over typed edges; "rules" emerge from arena dynamics.

3. **Multimodal natively.** Cyc is text-rule-centric. Hartonomous handles text + code + image + audio + video + model attestations in one substrate.

4. **Coverage scales by ingestion.** Cyc's coverage scales by manual curation rate. Hartonomous's coverage scales by ingestion bandwidth (CPU-bounded, not human-bounded).

## Semantic Web / RDF / OWL / Linked Data

**What they do:** Standardize data interchange via URIs, RDF triples, and OWL ontologies. Goal: a globally distributed, queryable web of structured data.

**How Hartonomous is different:**

1. **Content-addressed instead of URI-addressed.** Identity is content; no global URI registry needed.

2. **Inference is built-in.** Semantic Web reasoning relies on external reasoners (Pellet, FaCT++, HermiT). Hartonomous's A\* + arena Glicko is the inference engine.

3. **No vocabulary war.** Semantic Web requires upfront ontology agreement (FOAF vs schema.org vs vendor-specific). Hartonomous's reference vocabularies are seedable per substrate; arenas handle disagreement.

4. **Production-ready performance.** Semantic Web tooling is decades old and has consistently underdelivered on the performance promise. Hartonomous is built on Postgres with native compute extension — battle-tested infrastructure.

## Graph neural networks (GNNs)

**What they do:** Train neural networks whose layers are graph convolutions, used for tasks like node classification or link prediction.

**How Hartonomous is different:**

1. **No training; analysis at query time.** GNNs require training per task. Hartonomous's queries operate on the substrate's permanent state.

2. **Substrate is the graph; substrate IS the model.** GNNs operate on graphs as input. Hartonomous IS the graph and IS the model.

3. **No feature engineering.** GNN performance depends on node and edge feature design. Hartonomous edges are typed and Glicko-rated; no per-task feature design.

## Conclusion

Hartonomous overlaps with several existing system categories at the descriptive level (it stores graph data; it answers queries; it produces models). At the structural level it's none of them. Pattern-matching to any of the above categories will produce wrong implementation choices and wrong product positioning.

When in doubt, return to the three pillars (`10-architecture/00-overview.md`): identity, geometry, significance. Every Hartonomous design decision flows from those three. None of the systems above have all three; most have at most one.

## Cross-references

- Vision: `00-business/00-vision.md`
- Architecture overview: `10-architecture/00-overview.md`
- Competitive moats: `00-business/06-competitive-moats.md`
