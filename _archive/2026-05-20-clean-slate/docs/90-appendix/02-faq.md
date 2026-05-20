# FAQ

**Status:** Canonical (initial); living document
**Last verified:** 2026-04-29

---

## "Is this just a knowledge graph with an LLM on top?"

No. The substrate replaces the LLM. There is no transformer doing inference; A\* over typed edges with Glicko-2-rated significance IS the inference. Knowledge graphs store triples; the substrate stores typed edges with arena ratings, geometric trajectories, and content-addressed identity. Inference is built into the substrate, not bolted on.

See `90-appendix/01-related-work.md` for full comparisons.

## "Is this just RAG with extra steps?"

No. RAG retrieves chunks and feeds them to an LLM. The substrate has no LLM. Inference is direct traversal of the substrate's edge graph; no retrieval-then-generation pipeline. Output IS the path traversed, not a generated response over retrieved context.

## "Why not just use vector embeddings and ANN?"

The substrate is content-addressed via BLAKE3, not embedding-similarity. Identity is exact, not approximate. For genuine similarity queries, the substrate uses exact 4D Fréchet/Hausdorff over stored geometric trajectories. No HNSW, no LSH, no approximate methods. Sparsity comes from significance threshold (honest absence of attestation), not approximation.

## "How do you handle hallucination?"

Structurally. Inference traverses edges that exist; if no edge above significance threshold exists for a query path, the substrate says nothing rather than inventing. There is no probability distribution to sample from. Hallucination requires generating tokens from a probability distribution; the substrate has no such mechanism.

## "What about novel content the substrate hasn't seen?"

For genuinely novel content, the substrate produces honest abstention plus a frayed-edge flag (the geometry implies an edge type might apply, but no attestation exists). Inference returns `{paths: [], frayed_edges: [...], elapsed_ms: T}` with the structural reason for abstention.

For content that's compositionally novel but built from known atoms (a new sentence built from known words and grammatical structures), the substrate handles it normally — decomposes per text decomposer, traverses, returns coherent output.

## "How is this different from model merging?"

Model merging requires architectural compatibility (same layer structure across teachers) and uses interpolation/averaging heuristics. The substrate combines edges across architectures via the typed-edge representation; arena Glicko mechanics replace ad-hoc averaging; substrate accumulates monotonically vs static merge output.

## "Won't the substrate be biased by which models are ingested?"

Yes — and that's tunable via trust priors and arena dynamics. Customers can specify provenance filters per request (e.g., "use only academic-curated sources"), which addresses bias from specific model providers. The substrate's audit trail makes biases visible and queryable rather than hidden in opaque weights.

## "Why PostgreSQL, not a custom database?"

Postgres provides: MVCC concurrency, B-tree/GiST indexing, partitioning, custom extensions, COPY bulk-load, mature operations. The substrate's compute primitives (BLAKE3, 4D operators, A\*) are added as a native extension. Building a custom database for this would mean reinventing 30 years of database engineering. Postgres is the right boundary.

## "How does this scale to billions of edges?"

Partitioning per major key (entity_type_id, edge_type_id, context_type_id, physicality_type_id) keeps individual partitions tractable. Lazy materialization of significance rows avoids the (arena × edge) cardinality explosion. Content-addressed dedup via BLAKE3 prevents duplicate-driven growth. Horizontal scaling is planned via decentralized mode (substrate sharded across hosts) post-M14.

## "What about concurrent writers?"

PostgreSQL MVCC handles concurrent reads/writes natively. The pipeline batches inserts via COPY; ON CONFLICT DO NOTHING handles duplicate hashes. Significance updates use row-level locking with serializable isolation for outcome processing.

## "How does the substrate handle a new model architecture (e.g., Mamba state-space models)?"

The SafetensorsDecomposer handles known architecture families directly. New architectures require:
1. New `tensor_role` reference rows for the architecture's tensor structure (state-space matrices, recurrent kernels, etc.).
2. Decomposer extension to emit architecture-specific edges.
3. Recomposer extension to project substrate state onto the new architecture's tensor shapes.

This is engineering work, not architectural change. The substrate's three pillars (identity, geometry, significance) are architecture-neutral.

## "What if a customer ingests our substrate's exported model and re-trains it?"

That's outside the substrate's mechanism. The customer becomes the source of new model attestations; if they want their re-trained model in the substrate, they ingest it. Substrate-to-customer-back-to-substrate is just two ingestion events.

## "How is determinism preserved across decomposer versions?"

Each decomposer has a version. Substrate state is associated with the (decomposer, version) tuple. Re-ingesting with a new version produces (potentially) different state. Substrate snapshots reference (decomposer, version) tuples; reproducibility is preserved by replaying against the snapshot's specific versions.

## "What's the licensing model for substrate-derived models?"

Per ingested source license. If a customer's corpus is proprietary, refined model is the customer's. If a public-domain or permissive-license source contributes attestations, refined model inherits compatibility. Substrate maintains license metadata per provenance and can refuse to export models if mixed-license incompatibilities arise (configurable per deployment).

## "Why does the brand 'Laplace' matter?"

It signals the substrate's philosophical position — the substrate for digital content. Determinism, completeness, derivability, audit. Customers who care about those qualities recognize them in the brand. Customers who don't care still get a product; the brand is the proxy for the technical position.

See `90-appendix/00-laplace-demon-context.md`.

## Cross-references

- Vision: `00-business/00-vision.md`
- Architecture: `10-architecture/00-overview.md`
- Related work: `90-appendix/01-related-work.md`
- Anti-patterns (where these questions usually come from): `40-process/01-anti-patterns.md`
