# Hartonomous Glossary

Centralized definitions for every domain-specific term used across the project documentation. Terms are listed alphabetically. If a term isn't here, it either uses its standard industry meaning or needs to be added.

---

### 4D Embedding Physicality
The geometric representation of an embedding-matrix row as a `POINTZM` in 4-dimensional concept space. Every ingested model's embeddings are projected into the same 4D frame via Laplacian eigenmap + Gram-Schmidt orthonormalization. Each row becomes a **firefly** stored in the `physicality` table with `physicality_type='embedding_firefly'`. Four dimensions is the minimum ambient dimension in which cross-model Voronoi consensus cells are guaranteed to have well-defined interiors (see **Borsuk-Ulam**). See `specs/engine/embedding-physicality.md`.

### Analysis Pass
A C# class that performs a specific analytical operation on decomposed content. Each pass takes entities as input and produces additional entities, edges, physicalities, or junction table entries as output. Examples: `FFTPass`, `EdgeDetectionPass`, `NERPass`. All passes implement a shared `IAnalysisPass` interface. Passes run after structural decomposition and may depend on other passes' output.

### Assimilation
The ingestion policy: every decomposer's content enters the substrate unconditionally. There is no gating, refusal, or competition at ingest. Trust prior from provenance seeds the initial Glicko-2 `mu`; content-addressing deduplicates; tension (disagreement) is recorded, not silenced. Arena competition happens at **inference**, not ingest. Contrast with reject-on-contradiction policies which would pre-filter inputs — the substrate does not do that.

### Arena
A significance context in which Glicko-2 ratings are computed. Each arena isolates one kind of evaluation: `lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, etc. The same entity or edge can have different ratings in different arenas. Arenas are rows in the `significance_context` reference table.

### Atom
A leaf-level entity with no children. The lowest structural unit in a given modality. For text, a Unicode codepoint is an atom (tier 0). Atoms are positioned on the S3 surface as POINTZMs — their S3 coordinate IS their centroid. Contrast with **composition**.

### Borsuk-Ulam
Borsuk (1933). Every continuous function from the n-sphere `S^n` into `R^n` sends some pair of antipodal points to the same value. Equivalently, `S^n` cannot be embedded into `R^(n-1)` without collapsing some antipodal pair. Hartonomous consequence: 4D is the smallest ambient dimension in which cross-model Voronoi consensus cells are guaranteed to have well-defined interiors for every shared token, which is the geometric foundation of **4D Embedding Physicality**.

### BLAKE3
The canonical hash function used throughout the system. 256-bit output, SIMD-accelerated. Used for entity identity (content hash), composition identity (Merkle hash of ordered children), and edge identity (hash of type + participant hashes). Implemented once in the shared C++ library (`libhartonomous`) and exposed to both the PostgreSQL extension and C# via P/Invoke.

### Centroid
The spatial position of a composition entity, derived from the physicality of its children. For a LINESTRINGZM trajectory, the centroid is the geometric center of that linestring (via `ST_Centroid`). This centroid becomes a POINTZM in the parent composition's trajectory. Recursive — each tier's centroid feeds the next tier up.

### Comparison Event
A recorded evaluation in an arena where two or more alternatives are compared. Drives Glicko-2 rating updates. Example: two candidate senses for an ambiguous word are compared in the `lexical_disambiguation` arena based on traversal evidence. The winner's mu rises, the loser's mu drops, both sigmas decrease.

### Composition
An entity whose content is an ordered sequence of child entities. A word is a composition of codepoint atoms. A sentence is a composition of word compositions. The hash of a composition is the BLAKE3 hash of its ordered child hashes (Merkle tree). Same content = same children in same order = same hash = same entity. Contrast with **atom**.

### Decomposer
A C# class that ingests a specific data source and produces substrate state (entities, edges, physicalities, junction table entries, reference table rows). Two categories: **seed decomposers** (8 total, one per seed source, run during seed ingestion phases) and **runtime decomposers** (4 total, one per modality, run during user content ingestion). All implement `IDecomposer` and extend `BaseDecomposer`.

### Evidence Accumulation
The mechanism by which repeated ingestion of the same content tightens a significance record's uncertainty. Because content-addressing deduplicates to one entity, re-ingesting identical content does not create duplicates — it produces a comparison event that updates Glicko-2 `sigma` (uncertainty decreases) and may shift `mu` based on corroboration or contradiction. Twenty models asserting the same pattern does not create twenty rows; it creates one row with a very small sigma.

### Edge
A record in the `edge` table connecting two or more entities with typed semantics. Edges are the AI model — they carry significance and have their own trajectory geometry. Edges are NOT entities. They are n-ary (can have more than 2 participants), typed via `edge_type`, and carry provenance. Edge identity is BLAKE3 hash of (edge_type_id, participant hashes in role order).

### Edge Member
A participant in an edge. Stored in the `edge_member` table. Each member has a role (source, target, context, mediator, evidence, head, dependent) and an ordinal position among members of the same role. `edge_member.entity_id FK → entity(id)` — only entities can be edge members, never reference table rows.

### Entity
A row in the `entity` table. Either an atom or a composition. Has a BLAKE3 content hash as its identity, a structural type (`entity_type_id`), and potentially physicality, significance, junction table entries, and edge memberships. Same content = same hash = one entity. No duplicates.

### Evidence Junction Table
A table mapping entities to their classification values for fast application-layer lookups. Example: `entity_pos` maps entities to POS values with significance. "Is 'rake' a noun?" = one indexed JOIN, not graph traversal. Junction tables are the fast path; the edge table is the deep path. Both can coexist for the same relationship.

### Fail Loud
Substrate Law #13. No error swallowing, no silent failures, no fallback continuations, no partial results. Every operation succeeds completely or fails explicitly with full diagnostic context. The only acceptable response to failure is: stop, report what broke and why, fix it, re-run.

### Firefly
A 4D `POINTZM` physicality of an entity, produced by Track 1 embedding ingestion. Its coordinates are `(eig2, eig3, eig4, ||row||)` — the three non-trivial Laplacian eigenvector components (Gram-Schmidt orthonormalized) plus the embedding row's L2 norm as `m`. One entity can have many firefly physicalities (one per ingested model) sharing the same 4D frame. Used for cross-model **Voronoi Consensus**. See `specs/engine/embedding-physicality.md`.

### Frayed Edge
A query that cannot be resolved by traversal because it lands outside any existing Voronoi consensus cell, or traverses toward a destination with no significant path. The geometric or topological evidence of a gap in the substrate. Frayed edges are the substrate's primary trigger for Gödel-engine exploration — they tell the system "you do not know this yet, consider acquiring content that would fill the gap."

### Functional Sparsity
The Track 2 filter applied to transformation weights during safetensors ingestion. Not magnitude thresholding — activation-based, in the spirit of the Lottery Ticket Hypothesis. A weight (or weight cluster) is significant if it participates in an activation pathway that produces a non-zero downstream response on representative inputs. Weights that never fire are pruned. Contrast with wholesale ingestion (Track 1) which takes all embedding rows regardless of magnitude.

### Fréchet Distance
The spatial similarity metric used universally across modalities. `ST_FrechetDistance(a, b)` compares the SHAPE of any two trajectories: word similarity (suffix patterns), syntactic tree similarity, audio waveform similarity, attention pattern similarity. One PostGIS operator, every modality.

### Gram-Schmidt Orthonormalization
Linear-algebra procedure that turns any linearly-independent vector set into an orthonormal basis. Applied to the top-3 non-trivial Laplacian eigenvectors during Track 1 ingestion to guarantee that firefly `(x, y, z)` coordinates form a right-handed Cartesian frame. PostGIS 3D geometric functions (`ST_3DDistance`, `ST_3DDWithin`, `ST_Centroid`) require orthogonal axes to produce meaningful results; GSO makes the guarantee explicit rather than relying on numerical tolerance of the eigendecomposition solver.

### Glicko-2
The rating system used for significance scores. Each significance record has mu (rating mean), sigma (uncertainty), volatility (meta-uncertainty), and games (number of update events). Mu represents estimated strength. Sigma decreases as evidence accumulates. Ratings are updated from comparison events where alternatives compete in an arena.

### GiST Index
Generalized Search Tree index in PostGIS. Used on geometry columns (`physicality.geom`, `edge.geom`) for spatial queries: `ST_DWithin`, `ST_FrechetDistance`, `ST_HausdorffDistance`. Enables efficient proximity and similarity queries across all modalities.

### Junction Table
See **Evidence Junction Table**.

### Laplacian Eigenmaps
Spectral dimensionality-reduction technique. Given a weighted graph over points, compute the normalized graph Laplacian `L = I - D^(-1/2) W D^(-1/2)` and take the eigenvectors corresponding to the smallest non-trivial eigenvalues as the points' coordinates in reduced dimension. Preserves local neighborhood structure — points that were k-NN in the original space remain close in the reduced space. Track 1 ingestion uses Laplacian eigenmaps over an embedding matrix's row-wise k-NN graph; the 2nd, 3rd, and 4th eigenvectors become the `x, y, z` of each firefly (the 1st is discarded as a trivial constant).

### Merkle DAG
The entity structure forms a Merkle Directed Acyclic Graph. Each composition's hash is derived from its children's hashes. Each level's geometry is derived from the level below. Same content = same hash = same geometry = same entity. The `sequence` table records parent-child relationships with ordered positions and RLE counts.

### Modality
A category of content: `text`, `image`, `audio`, `video`, `model_weights`, `tensor_metadata`, `configuration`, `vocabulary`. Each entity type belongs to a modality. Each modality has a dedicated runtime decomposer and recomposer.

### Phase
A stage in the seed ingestion pipeline. Phases are ordered and have dependencies. Phase 0: repo/governance. Phase 1: core algebra (schema, reference table bootstrap). Phase 2a-2f: seed decomposers. Phase 3: model decomposition. Phase 4: significance field. Phase 5: inference engine. Phase 6: validation. See the Phase Map in architecture.md.

### Physicality
A geometric representation of an entity, stored in the `physicality` table. One entity can have multiple physicalities (different `physicality_type_id`). Uses PostGIS GEOMETRYZM types (POINTZM, LINESTRINGZM, MULTILINESTRINGZM). GiST-indexed for spatial queries. Text, audio, image, video, model weights — all share one physicality table.

### Provenance
The origin of an entity or edge. Stored as a reference table row with source code, curator class, and initial trust prior (mu). Every entity and edge has a `provenance_id`. Enables filtering by source authority and temporal replay.

### Recomposer
A C# class that reconstructs an output format from substrate state. Text recomposer walks sequences → collects codepoints → encodes UTF-8. Audio recomposer walks waveform geometries → generates PCM → encodes WAV. Safetensors recomposer synthesizes weight matrices from significance scores. All implement `IRecomposer<T>`. Export is not reconstruction of the original — it is distillation of the substrate's accumulated knowledge.

### Reference Table
A properly normalized lookup table holding classification vocabulary. POS types, dependency relation types, languages, Unicode properties, etc. Small, indexed, rarely written after seed ingestion, read on almost every operation. Reference table rows are NOT entities in the entity table. They are infrastructure that enables the substrate to process.

### RLE (Run-Length Encoding)
Compression mechanism in the `sequence` table. 100 identical elements (e.g., blue pixels) = one row with `count=100` instead of 100 rows. Intrinsic to the Merkle DAG — compression is structural, not a separate pass.

### S3 (3-Sphere)
The mathematical surface on which tier-0 atoms are positioned. UCA collation ordering → Super-Fibonacci spiral algorithm → 4D coordinates (x, y, z, m) stored as PostGIS POINTZM. Linguistically related codepoints are geometrically adjacent on this surface. Higher-tier composition centroids drift toward the interior as constituent positions are averaged.

### Seed Data
Data sources ingested during seed phases to build the substrate's initial knowledge graph. Two roles: **infrastructure** (UCD, ISO 639, WordNet/UD type vocabularies — creates the system's ability to process) and **content** (WordNet synsets, OMW alignments, UD syntactic patterns, AI model edges, Wiktionary, Tatoeba — seeds the knowledge graph with attested facts).

### Significance
A Glicko-2 rating on an entity or edge within a specific arena (significance context). Stored in the `significance` table. Drives inference — traversal follows high-significance paths. Updated from comparison events. Near-zero significance → candidate for pruning (Law #11).

### Sparsity
Substrate Law #11. Near-zero-significance edges are not stored. Boolean properties that are false are not recorded. XPOS values that are `_` are not stored. Sparsity is policy-governed and auditable — pruning decisions are logged.

### Substrate
The PostgreSQL database that IS the AI model. Not a database backing an AI — the database itself. Training = INSERT/UPDATE. Pruning = DELETE. Distillation = WHERE clause. Inference = recursive traversal. Context = graph-addressable state.

### Tension
A recorded disagreement between multiple contributors about the same entity or edge. Content-addressing deduplicates to a single row, but the arena preserves the disagreement as a first-class quantity: different provenances, different mu values, higher sigma. Tension is evidence that the substrate has not yet converged on this claim — it is not a bug, it is a signal for the Gödel engine to attend.

### Two-Track Ingestion
The safetensors decomposer's split ingestion model. **Track 1 (wholesale):** embedding-matrix rows are projected into 4D fireflies with no sparsity filter. **Track 2 (functional sparsity):** transformation weights are filtered by activation-based participation criteria and kept as explicit typed edges. The two tracks exist because embeddings are atomic reference frames (every row is a lookup key, can't prune) and transformations are ensembles of learned rules (most rows are gradient-descent noise, should prune). See `specs/decomposers/safetensors.md`.

### Super-Fibonacci Spiral
The algorithm used to project UCA collation ordering onto the S3 surface. Produces a uniformly distributed set of points on the 3-sphere. Each codepoint gets a unique POINTZM position determined by its collation rank. The algorithm ensures linguistically adjacent codepoints (by collation weight) are geometrically adjacent on the sphere.

### Tier
The structural depth of an entity in the Merkle DAG. Tier 0 = atoms (codepoints, individual pixel values). Tier N = compositions over tier-(N-1) entities. Emergent from reference depth, not hardcoded. Higher tiers have physicality centroids closer to the S3 interior.

### Trajectory
The geometric shape of a composition or edge. A composition's trajectory is a LINESTRINGZM through its children's centroids. An edge's trajectory is a LINESTRINGZM through its participants' positions. Trajectory shape encodes structural similarity — `ST_FrechetDistance` between two trajectories quantifies how similarly shaped they are.

### Trust Prior
The initial Glicko-2 mu assigned to entities and edges from a given provenance source. Authoritative sources (Unicode Consortium, ISO) get higher priors. Community sources (Wiktionary, Tatoeba) get lower priors. Trust priors are the starting point — arena dynamics adjust them from evidence.

### Voronoi Consensus
The geometric adjudication mechanism for cross-model agreement about a token's position in 4D concept space. Given all firefly physicalities for an entity (one per ingested model contributing an embedding for that entity), compute the 4D centroid and its Voronoi cell against the centroids of all other entities. That cell is the **consensus cell** — the region of concept space where every contributing model agrees "this is where this token sits." Tight cells mean agreement; fragmented or large cells mean ambiguity; empty cells mean disagreement → frayed edge → Gödel engine fires.

### UCA (Unicode Collation Algorithm)
Unicode Technical Standard #10. Defines the default ordering of all Unicode codepoints via multi-level collation weights (primary, secondary, tertiary). Hartonomous uses the UCA collation weight table (`allkeys.txt`) as input to the Super-Fibonacci Spiral algorithm, which projects collation rank into spatial position on the S3 surface. Seeded by the UCD/UCA decomposer in Phase 2a.
