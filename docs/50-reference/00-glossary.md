# Glossary

**Status:** Canonical
**Last verified:** 2026-04-29

Authoritative definitions of every load-bearing term used in the Hartonomous documentation tree. Where this glossary disagrees with informal usage in code comments or chat, this glossary is correct.

---

**A\* (A-star).** Best-first graph traversal algorithm with a cost function and an optional heuristic. The substrate uses A\* over the edge graph with edge cost = 1/μ in the requested arena. Bounded by `max_cost`, `max_depth`, `max_paths`. Implemented in the native extension's `traverse_astar` C function with bulk-fetch SPI.

**Arena.** A domain of competition for Glicko-2 ratings. Open-vocabulary set in `ref.significance_context`. Initial 10: `lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, `model_trust`, `source_authority`, `semantic_relevance`, `corroboration_strength`, `frequency_significance`, `attention_pattern_confidence`, `morphological_productivity`. Customers and the substrate operator can add new arenas at runtime.

**Atom.** A leaf entity in the substrate. The base case is a Unicode codepoint atom whose canonical content is the codepoint integer. Modality-specific atom types (pixel-value, audio-sample, tensor-element) are admitted by some implementations.

**Audit chain.** The traceable path from any output (refined model, inference response, generated text) back through substrate state to source provenance. Encoded in metadata, queryable via `hartonomous.provenance.audit_chain`.

**BLAKE3.** Cryptographic hash function used for content-addressed identity in the substrate. SIMD-accelerated. 256-bit output, optionally truncated to 128-bit for storage efficiency. Reference: <https://github.com/BLAKE3-team/BLAKE3>.

**Bulk-fetch SPI.** The pattern in `traverse_astar` of issuing one SQL query per popped node to retrieve all candidate successor edges, rather than one query per neighbor. Critical for performance.

**Centroid.** The geometric center of a composition's `linestring4d`, computed via `st_4d_centroid` (Euclidean) or `st_s3_centroid` (direction-only with unit-norm projection). Stored in `substrate.physicality`. Recursive: parents use child centroids as vertices.

**Composition.** A non-leaf entity whose identity is the BLAKE3 Merkle hash of its ordered children's hashes. Geometry: `linestring4d` through children's centroids.

**Composition trajectory.** The `linestring4d` representing a composition's structural fingerprint. Vertices are the centroids of constituents in canonical order. Compositions ARE trajectories — order is vertex order.

**Convergence.** The substrate's central learning event: identical content from different sources lands at the same row via content-addressed identity. Same hash = same row = accumulating evidence. Different sources attesting the same relationship via different edge types contribute to consensus arenas.

**Cognitive surface.** The set of SQL functions exposed to customers and integrators. Categorized: `inference.*`, `transform.*`, `generate.*`, `compare.*`, `analyze.*`, `recompose.*`, `provenance.*`, `lexical.*`, `cross_lingual.*`, `geometric.*`. Documented in `20-technical/08-cognitive-functions.md`.

**Cross-modal edge.** An edge connecting entities of different modalities (e.g., `recording_of` linking an audio chunk to a text composition; `depicts` linking an image region to a word).

**Decomposer.** A pure function from `(input bytes, provenance, format hints)` to typed substrate-shaped records. Emits via the central pipeline; never owns concurrency, transactions, or COPY. See `10-architecture/05-decomposer-contract.md`.

**Determinism.** Substrate Law 6: same input + same decomposer version + same substrate state = byte-identical state. Same recomposer + same recipe + same substrate = byte-identical output.

**Edge.** A typed n-ary relation between entities. Identity: `BLAKE3(edge_type_id || role-ordered participant hashes)`. Edge type IS in identity (Law 1a). Has a `linestring4d` trajectory through participants in role order.

**Edge member.** A row in `substrate.edge_member` linking an edge to its participants with role and ordinal position. Composite FK to both the edge and the entity.

**Edge trajectory.** The `linestring4d` of an edge through its participants' centroids in role order. The edge's structural fingerprint. Used for analogy completion (Fréchet match), relation clustering, frayed-edge detection.

**ELO.** Common shorthand for the Glicko-2 rating system used in the substrate. Strictly speaking, Glicko-2 is the successor to ELO. The substrate uses Glicko-2; "ELO" in code or comments refers to Glicko-2 ratings.

**Entity.** A row in `substrate.entity` representing an atom or composition. Identity: BLAKE3 of canonical content (atom) or Merkle of children (composition).

**Falsification test.** For every claim, a SQL query or test command that would fail if the claim were false. Substrate Laws and validation gates are stated with their falsification tests.

**Firefly.** A 4D point representing an embedding row's projection via Laplacian eigenmap + Gram-Schmidt + L2 norm. Per-model: `(eig2, eig3, eig4, ||row||)`. Multiple models contribute fireflies for the same token, enabling cross-model consensus and divergence queries.

**Fail-loud.** Substrate Law 13: operations succeed completely or fail explicitly with diagnostic context. No silent failures, no graceful degradation, no partial-result reporting.

**Frayed edge.** A pair of entities whose 4D positions match an edge type's archetype trajectory but no edge of that type exists between them. Mendeleev for knowledge — gaps the geometry says should be filled.

**Glicko-2.** Probabilistic rating system by Mark Glickman. Models skill (μ), uncertainty (σ), and meta-uncertainty (volatility). Used per arena per edge in the substrate. Reference: <https://glicko.net/glicko/glicko2.pdf>.

**Hartonomous.** The substrate's name and the project's name. Distinct from "Laplace," which is the brand name of the model family produced by the substrate.

**Hash value.** A BLAKE3 hash truncated to 128 or 256 bits. Used as identity for entities and edges. Stored as `bytea` of 16 or 32 bytes in the schema, declared via the `ref.hash_value` domain.

**Idiomaticity.** Geometric divergence between a compound's compositional centroid (mean of parts' centroids) and lexicalized centroid (whole-form's stored centroid). Three measurement levels: Euclidean (centroid distance), Fréchet (trajectory distance), Hausdorff (cloud distance).

**Ingestion.** The decomposer + pipeline path from input bytes to substrate state. Deterministic by Law 6.

**Inference.** A\* traversal over the substrate's edge graph, returning paths with explanation traces. The substrate's replacement for the transformer forward pass.

**Junction table.** A table mapping entities to classification reference rows with significance: `entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature`, `codepoint_property`, `tensor_tensor_role`, `pattern_deprel`. Glicko-bearing for some.

**Laplace.** The brand name for the model family produced by the substrate. Includes Laplace-Linguistics, Laplace-Coder, Laplace-Reason, Laplace-VL, Laplace-Vision, Laplace-Audio, Laplace-Multimodal, Laplace-Embed, Laplace-Rerank, Laplace-Diffuse, Laplace-Custom.

**Laplace's Demon.** Pierre-Simon Laplace's thought experiment about an intellect that knows the position and momentum of every particle in the universe. Hartonomous is Laplace's Demon for digital content: knows every codepoint, composition, edge, significance, and provenance, and can derive any past or future composition.

**Lazy materialization.** Strategy for `substrate.edge_significance`: rows are NOT eagerly created for every (arena, edge) pair. Queries `COALESCE(s.mu, p.initial_mu)` to use provenance trust prior as default. Rows are inserted on first outcome event for a (arena, edge) pair.

**Linestring4d.** Substrate-native type for 4D ordered vertices. Implemented in `hartonomous_pg`. Used for composition and edge trajectories.

**Mantissa exploitation.** Pattern of using PostGIS GeometryZM as a 4-float exact-integer payload by treating each axis as bit-exact for integers up to 2^53. CHECK constraints declare per-physicality-type coordinate semantics.

**Merkle DAG.** Directed acyclic graph where each non-leaf node's identity is the hash of its children's identities. The substrate's compositions form a Merkle DAG (not tree — children are shared by reference).

**Mitosis.** The biological metaphor for substrate model production: substrate is the parent body; exported models are daughters that bud off carrying the parent's state without depleting it. Production cost is I/O.

**Modality.** The kind of content: text, code, image, audio, video, model attestation. Entity types declare their modality. Cross-modal edges link entities across modalities.

**NFC, NFD.** Unicode Normalization Forms. NFC = Canonical Composition (precomposed); NFD = Canonical Decomposition. Substrate's text decomposer applies NFC at entry. Different forms are different content — they link via `canonical_decomposition_of` edges from UCD seed.

**Outcome event.** Customer-supplied feedback after inference (accept/reject/partial). Triggers Glicko-2 updates per arena on selected and rejected path edges.

**Per-hop filtering.** The substrate's defining inference capability: each step of A\* can be independently filtered by any SQL predicate over arena, provenance, edge type, modality, language, etc. Different hops can use different filters. Different turns can use different recipes.

**Physicality.** A row in `substrate.physicality` carrying geometric data for an entity. One physicality_type per row; one geometric column populated per type.

**Pipeline.** The central ingestion subsystem. Bounded `Channel<TRecord>` per record kind, COPY-based bulk loaders, staging-flush procedures. Decomposers emit; pipeline consumes.

**Point4d.** Substrate-native type for a single 4D point. Implemented in `hartonomous_pg`. Used for atom positions and centroids.

**Provenance.** A row in `ref.provenance` representing a source of edges and entities. Has `initial_mu` trust prior. Sub-provenance for ingested models: `huggingface_model:<model_id>`.

**Recipe.** A structured object specifying per-hop filtering for inference traversal. JSON or DSL form. Customers compose recipes; substrate operator ships canonical recipes.

**Recomposer.** A function from `(target spec, substrate state)` to output bytes. Each output format has a recomposer. The recomposer's projection function is the load-bearing engineering for refinement-as-service.

**Refinement.** The process of re-exporting an ingested model after substrate accumulation. Output preserves architecture exactly; weights reflect substrate consensus, not the original model's training noise.

**Refinement-as-service.** First commercial product. Customer's model + corpus → ingest → cross-source-corroborate → re-export with refined values.

**Sigma (σ).** In Glicko-2: the rating uncertainty. Decreases as evidence accumulates. Initial value 350.

**Significance.** The Glicko-2 rating in a specific arena for a specific edge or entity. Stored in `substrate.edge_significance` or `substrate.entity_significance`.

**Substrate.** The PostgreSQL database with `hartonomous_pg` extension and PostGIS. The factory that produces Laplace models. Distinct from Laplace, which is the product line.

**Substrate Law.** A non-negotiable invariant of the substrate. Documented in `10-architecture/01-substrate-laws.md`. 13 laws.

**Tau (τ).** The Glicko-2 system constant constraining volatility change. Typical values 0.3–1.2. Substrate uses 0.5 by default.

**Text decomposer.** The universal text path: bytes → UTF-8 decode → NFC → grapheme clusters → words → sentences → paragraphs → text_compositions. Every text-bearing decomposer routes its strings through this.

**Tree-sitter.** Incremental parser generator producing typed ASTs. The substrate uses tree-sitter (and equivalent typed-AST parsers) as the canonical decomposer interface for structured formats. ~305 language grammars in the language pack.

**Trust prior.** A row's `provenance.initial_mu` — the default Glicko-2 mu for edges from this provenance before any other evidence. Authoritative sources have higher priors (UCD = 2000, WordNet = 1800); community sources have lower (Wiktionary = 1400).

**UAX #29.** Unicode Annex defining text segmentation algorithms (grapheme cluster, word, sentence, line break boundaries). The substrate's text decomposer uses these for language-agnostic segmentation.

**UCA.** Unicode Collation Algorithm (UAX #10). DUCET (Default Unicode Collation Element Table) provides per-codepoint collation weights used by the substrate to compute deterministic S³ positions via Super-Fibonacci spiral.

**UCD.** Unicode Character Database. Source of all codepoint properties (general category, script, block, break properties, combining class, decomposition mappings, case mappings, numeric values). Foundational seed.

**Volatility.** In Glicko-2: meta-uncertainty (how much sigma is expected to change over time). Initial value 0.06.

**Voronoi consensus.** 4D Voronoi cells over per-token firefly clouds. Tight cells = high cross-model agreement. Borsuk-Ulam justifies dimension 4 as the minimum where these cells reliably exist.

## Cross-references

- Architecture overview: `10-architecture/00-overview.md`
- Substrate Laws: `10-architecture/01-substrate-laws.md`
- Schema reference: `20-technical/00-schema-reference.md`
