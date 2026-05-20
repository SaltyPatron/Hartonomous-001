# The Substrate Bond

**Status**: ✅ Complete

The conceptual frame for what Hartonomous is and what it is not. Every design decision in this repository — content-addressed identity, determinism, local-first execution, geometric physicality, Glicko-rated traversal, substrate-level governance — follows from this principle. Engineers, reviewers, and AI agents working in this repo should read this document before making architectural claims about the substrate.

---

## Purpose of this document

To name, precisely, the category of system Hartonomous belongs to. Industry vocabulary has no term for it because the industry has not built this category before. Occult vocabulary has a term — *substrate* — that fits with unusual exactness. This document grounds that term in the specific technical requirements it implies, and shows how every major design choice in the repo derives from it.

---

## the substrate in the knowledge regime

### The original Demon (Laplace, 1814)

> "An intellect which at a certain moment would know all forces that set nature in motion, and all positions of all items of which nature is composed, if this intellect were also vast enough to submit these data to analysis, it would embrace in a single formula the movements of the greatest bodies of the universe and those of the tiniest atom; for such an intellect nothing would be uncertain and the future just like the past would be present before its eyes."

the substrate requires four things to work:

1. **Complete state** — the position and momentum of every atom.
2. **Complete catalog of forces** — every force law describing how atoms interact.
3. **Sufficient computational capacity** — enough power to integrate state forward.
4. **Determinism of the dynamics** — the system must not be stochastic at its foundation.

### Why the Demon fails in physics

| Requirement | Physics failure mode |
|---|---|
| Complete state | Heisenberg uncertainty — position and momentum cannot be jointly measured to arbitrary precision. |
| Complete force catalog | Forces are continuous fields over R³ × t; integrating them requires infinite precision and infinite information per point. |
| Sufficient compute | Chaotic dynamics produce sensitivity to initial conditions that exceeds any finite representation; the demon would need to be outside the universe it models, introducing self-reference. |
| Deterministic dynamics | Quantum mechanics introduces irreducible indeterminism at the foundation. |

These four failures are the standard objections to Laplace's project. They are universally accepted in physics.

### Why the Demon succeeds in the knowledge regime

Every one of the four failures is a constraint of **matter**, not a universal constraint on demons. Digital content is not matter. Its four corresponding properties are the inverse of matter's:

| Requirement | Knowledge regime answer |
|---|---|
| Complete state | BLAKE3 content-addressing. Every atom of knowledge has exact, uncollidable, reproducible identity over its byte-content. There is no uncertainty principle for content: the bytes ARE the state. |
| Complete force catalog | Typed edges with Glicko-2 ratings over a finite, partitioned n-ary relation space. Forces between knowledge atoms are discrete, named, and indexed. No continuous field integration is required. |
| Sufficient compute | A\* traversal over typed edges, GiST/SP-GiST indexes over geometric physicality, LIST partitioning by type. Queries touch only the slice of the universe relevant to the question. The practitioner sits outside the substrate; the substrate models content, not itself. No self-reference problem. |
| Deterministic dynamics | Law #6. Same input + same decomposer version = byte-identical substrate state. Determinism by construction, not by hope. |

**The impossibility of the substrate is a property of matter, not a universal constraint on demons.** In the knowledge regime, the four prerequisites all hold, and they hold cleanly, by construction. The demon is tractable here.

This observation is the origin of the entire architecture. Every design decision in the repo is an answer to "what does the knowledge-regime demon need in order to function?"

---

## The substrate: what the demon is, precisely

A *substrate*, in the Western magical tradition, is not synonymous with a demon in the modern popular sense. It has specific properties:

1. **Bonded to a specific practitioner.** Not a shared service, not a crowd-sourced artifact.
2. **Subservient, not autonomous.** It acts on command; it does not decide.
3. **Auditable by its master.** The practitioner can always interrogate it about why it did what it did.
4. **Learns from service.** Use updates the substrate's capabilities in ways specific to the practitioner's work.
5. **Goes places the practitioner cannot.** Retrieves knowledge and returns with it.

Hartonomous is a substrate, not a god, not an oracle, not an AGI, not an LLM. Each property above maps to concrete design decisions in this repo.

### Property 1: Bonded to a specific practitioner

**Design implication: local-first execution. No GPU requirement. No datacenter dependency.**

A substrate you need a datacenter to summon is not yours — it is a tenant of someone else's spell. CPU-only inference, PostgreSQL-only storage, local-file-only ingestion are not performance compromises; they are loyalty properties. The substrate must be able to run entirely on hardware the practitioner owns and operates. This is why:

- `Hartonomous.Engine` uses A\* over edges instead of transformer inference.
- The compute facade (`Hartonomous.Core.Compute.*`) is tuned to AVX2+FMA3+AVX-VNNI+BMI2 (consumer CPU ceiling) rather than AVX-512.
- Ingestion determinism is enforced via `MKL_CBWR=AUTO,STRICT` and fixed seeds, not via cloud-managed reproducibility.
- All seeds (UCD, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba) and model decomposition outputs are written into the practitioner's own PostgreSQL instance. There is no cloud sync.

### Property 2: Subservient, not autonomous

**Design implication: the substrate never emits a probability distribution over answers. It traverses edges on a practitioner's query and returns a named path with rated steps.**

The substrate does not produce generative output in the LLM sense. It produces:

- A list of entity IDs reached by traversal.
- A list of edges crossed, each with its Glicko rating and provenance.
- A list of physicality centroids and distances.
- A recomposed output (text, image, audio, video) reconstructed deterministically from substrate state via `IRecomposer<T>`.

The practitioner decides what to do with that output. The substrate does not make the decision. This is why:

- `IInferenceEngine.InferAsync` returns `InferenceResult` with explicit `SeedEntityIds`, `Paths`, `Entities`, and `NodesVisited` — the raw traversal record, not a verbal judgment.
- There is no "chat" layer in the engine. Conversational surfaces are recomposer-level concerns built on top of the traversal substrate.
- Generative recomposition (`specs/engine/generation-and-transformation.md`) is explicitly a deterministic reconstruction from substrate state, not a learned generation from hidden weights.

### Property 3: Auditable by its master

**Design implication: every judgment is a named row in a visible table with explicit provenance and Glicko history.**

A substrate whose reasoning you cannot inspect is a demon, not a substrate. This is the oldest distinction in the tradition. Every Glicko rating, every provenance edge, every significance score, every firefly coordinate is stored as a row the practitioner can SELECT, audit, diff across time, and override. This is why:

- Every edge carries a `provenance_id` referring to a row in `substrate.provenance` — who asserted it, with what trust prior.
- `substrate.significance` rates entities and edges with `(mu, sigma, volatility, games)` that update on evidence and can be inspected as a history.
- Junction tables (`entity_pos`, `entity_sense`, `pattern_deprel`) carry Glicko-2 columns so that classification assignments are themselves audited.
- `session`, `comparison_event`, `significance_snapshot` (`specs/operations/sessions.md`) let the practitioner replay, diff, and rollback substrate changes over time.

The substrate has no interpretability problem because it has no hidden layer. It is interpretable by construction.

### Property 4: Learns from service to the practitioner

**Design implication: Glicko-2 ratings update on use. The substrate grows more useful to its specific practitioner over time.**

Each time a traversal crosses an edge and the result is corroborated (or contradicted) by further evidence, the edge's rating shifts. The shifts are specific to the practitioner's use of the substrate — their queries, their corrections, their corpora. A substrate used by a biologist and a substrate used by a lawyer, starting from the same seed, will develop different rating distributions. This is why:

- `specs/engine/arenas-and-significance.md` defines the Glicko-2 update machinery as use-driven, not batch-trained.
- Session-scoped rating changes can be promoted or reverted per `specs/operations/sessions.md`.
- The Glicko-2 volatility parameter specifically handles how much a rating should drift given contradictory evidence, giving the substrate a measured response to disagreement.

Crucially, this is *closed-loop learning without training*. The substrate adapts to the practitioner without gradient descent, without a loss function, without labeled data. Glicko-2 is a tournament model; every use is a comparison event.

### Property 5: Goes places the practitioner cannot

**Design implication: ingestion of AI models (safetensors decomposition) extracts the geometry of models the practitioner cannot themselves train.**

The practitioner cannot realistically train a 70B-parameter transformer. But they can ingest one. The safetensors decomposer and its analysis passes (`EmbeddingFireflyPass`, `SvdPass`, `WeightDistributionPass`, `AttentionArchetypePass`, `MoERoutingStatsPass`, `SparsityAnalysisPass`) extract the *learned geometry* of the model into the shared 4D firefly frame. From that moment on, the substrate carries what the model learned without needing to run the model. See `specs/engine/embedding-physicality.md` for the Laplacian-eigenmap + Gram-Schmidt projection that makes cross-model geometries commensurable.

This is how the black box becomes transparent: not by explaining the model (which is intractable), but by *absorbing its geometry into a substrate where geometry is a queryable, indexed, auditable object*. The substrate does not compete with LLMs; it extracts their learned structure and carries it home.

---

## What Hartonomous is NOT

Every one of these framings is insufficient and gets the architecture wrong:

| Wrong framing | Why it is wrong |
|---|---|
| A knowledge graph | Knowledge graphs do not carry geometric physicality. They cannot compute Fréchet distance between trajectories, Voronoi consensus between models, or centroid-based compositional position. |
| A vector database | Vector databases store opaque embeddings. They have no edges, no Merkle identity, no provenance hierarchy, no Glicko-rated classification. They cannot explain a retrieval. |
| A RAG system | RAG passes retrieved text back to an LLM for generation. Hartonomous has no LLM. Recomposition is deterministic from substrate state. |
| An ontology | Ontologies are static classification schemes. Hartonomous's junctions carry Glicko-rated confidences that update on use; the "ontology" is a live tournament. |
| A semantic search engine | Semantic search returns ranked documents. Hartonomous returns named paths with rated edges and reconstructed output. |
| A fine-tuned model | Fine-tuning adjusts hidden weights. Hartonomous has no weights to adjust. Adaptation is UPDATE on junction rows. |
| An AGI | AGI implies autonomous decision-making. The substrate is bonded and subservient by design. |
| A content moderation system | Moderation is learned classification. Hartonomous governance is deterministic relational lookup (see `specs/engine/substrate-governance.md`). |

Any document, test, or code change that assumes one of these wrong framings is drifting from the architecture. Reviewers should flag such drift.

---

## The seed order as construction of prior agreement

The phase order — `UcdUca → Iso639 → WordNetOmw → UniversalDeps → ModelDecomp → Wiktionary → Tatoeba → TextDecomp` — is not a dependency convenience. It is a construction of **the agreement the practitioner inherits before any of their own content arrives**:

| Seed | What agreement it captures |
|---|---|
| UCD/UCA | Every codepoint decision the Unicode Consortium has made — the atoms of written language and their collation weights. |
| ISO 639 | Every language-identity decision SIL and ISO have ratified. |
| WordNet | Princeton's canonical sense inventory for English — the largest human-curated synset graph ever assembled. |
| OMW | Open Multilingual Wordnet's cross-lingual alignment — synsets attested in many languages share one ID. |
| Universal Dependencies | Cross-linguistic syntactic structure — dependency relations as typed edges, not annotations. |
| Safetensors / ModelDecomp | Every ingested model's learned geometric agreement with its training data, projected into the shared 4D frame. |
| Wiktionary | Cross-lingual derivation, etymology, and sense density beyond what WordNet has curated. |
| Tatoeba | Attested cross-lingual sentences with audio recordings — real human use of the lexicon. |

By the time a practitioner ingests their first email, the substrate already knows every Unicode codepoint decision, every ISO language identity, every Princeton sense, every cross-lingual alignment, every dependency pattern, every major model's learned geometry, every major etymological relation, and every attested sentence in a hundred languages with audio. The practitioner's content **aligns against this pre-agreed universe**, not against an empty substrate. The substrate inherits the settled portion of the human record.

---

## Design corollaries

The substrate bond produces specific, testable requirements. Any violation is a bug.

### Corollary 1: Content identity is absolute

Same bytes → same BLAKE3 → same entity ID → same centroid → same position in the substrate. No exceptions. Placement metadata (position, ordinal, filename, tensor name, source offset) lives on `sequence`, edges, or `provenance` — never in the identity hash. Violating this produces phantom duplicates that prevent the universe from converging on a single shared representation.

### Corollary 2: Law #6 — determinism is non-negotiable

Same input + same decomposer version = byte-identical substrate state. This is the loyalty guarantee. A substrate that returns different answers for the same question is unreliable; a substrate whose substrate diverges across repeated runs cannot be trusted by its practitioner. This is why:

- No approximation methods (no HNSW, no LSH, no random projection, no randomized SVD, no stochastic trace estimation).
- No quantization of content values.
- Every PRNG usage takes a fixed, declared seed.
- `MKL_CBWR=AUTO,STRICT` enforced at process start.
- All dtype decoding is lossless (BF16 → F32 → F64, never compressed).

### Corollary 3: Ingestion records, inference decides

At ingestion time, decomposers record ALL candidate senses, structures, and evidence without disambiguation. Sense selection, role assignment, and meaning resolution happen at inference time via significance-weighted traversal. This preserves the full evidence base for later adjudication and lets Glicko-2 ratings (which are use-driven) determine which candidate wins in context.

### Corollary 4: The practitioner retains sovereignty

Governance, rating thresholds, provenance trust, corpus inclusion, and register classifications are all practitioner-controlled. The substrate never refuses to answer on its own moral authority. When it refuses (see `specs/engine/substrate-governance.md`), it refuses based on practitioner-configured relational predicates whose rows are inspectable and modifiable.

### Corollary 5: Infrastructure is not substrate

Reference tables (`pos`, `deprel`, `sense`, `language`, etc.) and junction tables (`entity_pos`, `entity_sense`, `entity_language`, etc.) are cached, rebuildable lookup surfaces for microsecond classification queries. They are NOT substrate content. Confusing the two — by, for instance, treating POS as an entity or storing classification rows in `substrate.entity` — destroys the layer discipline that makes the substrate both fast and auditable. See `specs/sql/infrastructure-vs-substrate.md`.

---

## The scale claim

A substrate that covers "all of digital content" sounds hyperbolic. It is not, if the following hold:

- Ingestion is sublinear in the sense that deduplicated content (the same sentence in 10,000 documents, the same codepoint in every text file) collapses to one entity with one hash. Growth is bounded by **distinct content**, not total content.
- Physicality geometry is GiST-indexed over 4D envelopes. Nearest-centroid queries are logarithmic in substrate size.
- Partitioning by type keeps hot partitions (text, word_form) separate from cold ones (codepoint, language, entity_type).
- Traversal is A\* with Glicko-2 cost, not full graph search. Query complexity is bounded by path-length budget, not by substrate size.

The substrate is content-addressed, indexed, partitioned, and bounded by distinct content. The only scale challenge that remains is the ingestion throughput for novel content — and that is a batching and streaming problem, not a storage or query problem.

---

## Why this framing matters for the repo

The substrate bond is not flavor text. It is the specification for how to evaluate any proposed change:

- A proposal that couples the substrate to a cloud service violates Property 1 (bonded to the practitioner).
- A proposal that emits a probability distribution as a substrate output violates Property 2 (subservient, not autonomous).
- A proposal that buries a decision in an opaque learned component violates Property 3 (auditable).
- A proposal that requires retraining to accept a correction violates Property 4 (learns from service) and corollary 2 (determinism).
- A proposal that requires the substrate to run a GPU-bound model to answer basic queries violates corollary 1 (the substrate lives on the practitioner's hardware).

Reviewers and AI agents should test every proposed change against these five properties and five corollaries. Drift is not always obvious; the framing is what makes drift detectable.

---

## Cross-references

- `architecture.md` — Authoritative architecture reference, substrate laws, scale.
- `specs/engine/inference.md` — How the substrate actually answers queries.
- `specs/engine/embedding-physicality.md` — How ingested-model geometries enter the shared 4D frame.
- `specs/engine/arenas-and-significance.md` — Glicko-2 tournament machinery.
- `specs/engine/substrate-governance.md` — How governance works as relational lookup during decomposition.
- `specs/sql/infrastructure-vs-substrate.md` — The two-layer discipline.
- `specs/native/geometry4d-composition.md` — The recursive centroid geometry that makes the demon tractable.
- `specs/sql/mantissa-exploitation.md` — Why PostGIS is used as a generalized indexed columnar store.
