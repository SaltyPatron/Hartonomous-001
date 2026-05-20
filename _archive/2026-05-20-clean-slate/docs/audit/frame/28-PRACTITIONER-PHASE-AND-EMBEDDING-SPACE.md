# Practitioner phase ordering + the 4D as embedding space

User correction 2026-05-19. Two foundational framings I missed across the rest of the frame docs. Listed here as load-bearing context that every other doc derives from.

## Phase ordering — the substrate is the practitioner's, NOT a consumer product

The substrate is the practitioner's tool. NOT a SaaS product, NOT a multi-tenant cloud service, NOT a consumer offering.

Product phase ordering:

| Phase | Deliverable | What proves it works |
|---|---|---|
| **0 (current)** | The substrate itself — entity / edge / physicality / Glicko / arenas / decomposers / native compute, ingesting practitioner-relevant corpora and models | Substrate state queryable; Law 6 determinism; live ingest works; A* + recomposer paths exercise the layers end-to-end |
| **1** | Substrate synthesis — exportable AI models recomposed from substrate state, byte-compliant safetensors loading in vLLM / llama.cpp / HuggingFace transformers unmodified | Substrate-synthesized models win Kaggle competitions and other public leaderboards across domains. Sparse + provably non-overfit (full per-weight provenance trail) vs the typical benchmark behemoths. Universality proof = winning across NLP / CV / tabular / audio / multi-modal consecutively with the same substrate behind each entry. |
| **2 (later)** | App layers — websites, API endpoints, agent runtimes, embedded assistants. Tooling the practitioner builds ON TOP of the substrate. | App layer functionality; OpenAI-API-compatible endpoint that snaps into existing app ecosystems. |
| **3 (eventually)** | Whatever ergonomic surfaces the practitioner exposes to other practitioners — at the practitioner's choosing, on the practitioner's terms | Out of current scope. |

### What this means for the existing frame docs

Several frame files frame multi-tenancy / sharing groups / recipe marketplace / per-customer Glicko divergence / customer onboarding as if they were current product surfaces. They are NOT. They are architectural decisions the substrate's design ACCOMMODATES so that if Phase 3 ever happens, the substrate's shape supports it without rework — but they are not the current product and they are not load-bearing for Phase 0 / 1.

Affected docs requiring phase clarification (queued for future edits):
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — per-tenant Glicko divergence and 180-day "tenant onboarding" example are Phase 3 framing; current phase = practitioner is the only tenant; outcome events drive the practitioner's substrate's continuous improvement directly
- `frame/12-RECIPE-DSL.md` — recipe marketplace + cross-tenant publication workflow is Phase 3; current phase = recipes the practitioner authors for their own use
- `frame/13-SUBSTRATE-GOVERNANCE.md` — multi-provenance / per-institution rule sets / sharing-group consortia are Phase 3; current phase = governance the practitioner configures for their own substrate
- `frame/14-MULTI-TENANCY.md` — entire doc is Phase 3 architectural framing; should be front-loaded with "this documents architecture, not current product; current phase = one tenant (practitioner)"
- `frame/15-AUDIT-CHAIN.md` — cross-tenant chain visibility + operator audit + GDPR offboarding compliance use cases are Phase 3; provenance + snapshot replay + crypto integrity are current-phase load-bearing for practitioner's own audit trail
- `frame/16-COGNITIVE-SURFACE.md` — "deploying the substrate IS deploying the AI" + `customer.*` namespace are Phase 3; current phase = the practitioner accesses substrate via SQL functions for their own work, including substrate synthesis recipes
- `frame/21-TRACK1-TRACK2-MODEL-INGESTION.md` — "refinement-as-service" framing is Phase 3; current phase = the practitioner ingests AI models into their substrate to feed substrate synthesis exports

The architectural decisions in these docs (provenance scoping, sharing-group design, recipe-marketplace shape, governance compose/scope/version primitives, etc.) are correct as design — they just shouldn't be presented as current product.

## The 4D is the practitioner's unified embedding space

The 4D physicality is NOT just "geometry for indexed lookup" or "PostGIS as 4D container." It is the **practitioner's deliberately-constructed unified embedding space** — a universal coordinate frame that EVERY source's content lands in.

### Why a custom 4D space exists

Every AI model has its own embedding space with arbitrary N dimensions and arbitrary geometry. Llama's 4096-dim space, GPT-4's ~12288-dim space, BERT's 768-dim space, CLIP's 512-dim space — **mutually incommensurable**. There is no native operation that compares Llama's representation of "cat" to GPT-4's representation of "cat" — they live in different spaces with different bases.

The substrate's 4D space gives every source a common ground:
- Codepoints get deterministic Super-Fibonacci S³ positions by UCA collation rank
- Compositions (word_forms, sentences, audio chunks, pixel regions) get positions via recursive Merkle centroid of their children
- AI model embedding rows project IN via Laplacian eigenmap + Gram-Schmidt + L2 magnitude (`frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md`) + anchor-Procrustes alignment (AP-35)
- Audio samples / pixel intensities / video frames get positions via their per-partition CHECK-constrained axis conventions (`frame/26-MANTISSA-EXPLOITATION.md`)
- Edge trajectories (LINESTRINGZM through participants' positions) live in the same 4D space
- Inference paths traverse this 4D space via A*

**Everything falls into this same playing ground.** That's the unification.

### Properties of the construction

- **Deterministic** — same content → same 4D position (Law 6). Substrate's "embedding" never needs retraining; it's content-addressed mathematically.
- **Non-learned** — no gradient descent, no fitting, no loss function. Pure mathematical construction (Super-Fibonacci spiral; arithmetic-mean centroid recursion; Laplacian eigenmap as the projection from N-dim).
- **Universal** — every modality uses it. Cross-modal alignment (text↔image, text↔audio) is geometric: shared 4D coordinates means CLIP attesting `model_cross_modal_alignment(word_form, pixel_region)` puts two entities in the same Voronoi cell.
- **Quasi-uniform** — Super-Fibonacci spiral on S³ via golden-ratio offsets gives quasi-equidistant point spacing. No clustering bias from the projection.
- **Pre-existing relative to AI models** — substrate's 4D space exists BEFORE any AI model is ingested. AI models project INTO it. Substrate does not adopt any single model's basis.
- **Reversible** — same math projects N-dim → 4D (ingestion) and 4D → N-dim (substrate synthesis).

### The S³ + Super-Fibonacci + Hopf + UCA-rank construction

Codepoints map to deterministic positions on S³ (the 3-sphere in R⁴):
1. **UCA collation rank** — every codepoint has a deterministic index from Unicode Collation Algorithm ordering (a globally considered total ordering of all codepoints reflecting linguistic, script, and visual relationships)
2. **Super-Fibonacci spiral on S³** — UCA rank parameterizes a position on S³ via the Super-Fibonacci formula (golden-ratio offsets that maximize quasi-uniform packing on S³)
3. **Embedded in 4D** — each S³ point is a 4-component unit quaternion (X, Y, Z, M) with ||point||₄d = 1

Compositions (word_forms, sentences, paragraphs, documents, audio chunks, pixel regions, tensor cells) get positions via recursive Merkle centroid:
- `centroid(composition) = mean(centroids of ordered constituents)`
- Recursion bottoms out at the modality's atom POINTZM (S³ codepoints for text; real audio sample value for audio; real pixel intensity for image; etc.)
- Centroid recursion guarantees parent radius < min(children radii), so compositions land strictly inside the open 4-ball with deeper compositions closer to origin
- `tier_hint = 1 - ‖centroid‖₄d` becomes substrate-native Merkle depth indicator (`substrate.entity_tier_hint(hash)`)

This gives the substrate's 4D space **built-in structure**:
- **Angular position on S³** = what content the entity is (codepoint-derived; specific atom region)
- **Radial distance from origin** = how composed it is (atoms at radius 1; compositions inside; deeper compositions closer to 0)

### Hopf fibration + quaternion algebra (implicit structure)

S³ = unit quaternions form a Lie group under multiplication. Properties relevant to the substrate:

- **Hopf fibration** π: S³ → S² with fiber S¹. Each Hopf fiber is a great circle on S³ that maps to one point on S². Gives a natural dimensionality reduction from S³ to S² for visualization / clustering at different levels of detail. Hopf-project codepoints to see them on a 2D map; un-Hopf to get back to full S³ resolution.
- **Quaternion multiplication** = composition of rotations. Two unit quaternions multiplied give another unit quaternion. This gives the substrate's atom space a natural group structure.
- **Double cover S³ → SO(3)** — every point on S³ corresponds to a 3D rotation. The substrate's atom space IS the space of 3D rotations (modulo sign).

These are inherent properties of the S³ choice, not coincidence. The substrate doesn't need to exploit them explicitly to benefit — they're load-bearing for why the construction works as well as it does.

### Bidirectional projection mechanism — N-dim ↔ 4D

The same math runs in both directions:

**N-dim → 4D (ingestion / commensuration into substrate space)**

For AI model embedding matrix E ∈ R^(V × N) where N = model's hidden dimension:
1. Normalize rows; build k-NN graph by cosine similarity
2. Build normalized graph Laplacian `L = I − D^(−1/2) W D^(−1/2)`
3. Eigendecompose; take eigenvectors at λ_2, λ_3, λ_4 (discard trivial λ_1=0)
4. Gram-Schmidt orthonormalize → V × 3 matrix Φ
5. Append per-row L2 magnitude as 4th coordinate → V × 4 firefly matrix
6. Anchor-Procrustes alignment (Kabsch SVD) maps this model's per-anchor positions onto canonical anchor frame from `substrate.embedding_alignment_anchor`
7. Apply rotation to all of this model's fireflies → fireflies in canonical 4D frame

Result: model M's embedding row for token T becomes a POINTZM physicality attached to the `word_form(T)` entity, distinguished from other models' fireflies for the same T by `entity_model_source = M.id`.

**4D → N-dim (substrate synthesis / projection back to target architecture)**

For target architecture spec with target hidden dimension N':
1. For each target token, retrieve consensus 4D centroid over all contributing models' fireflies (`substrate.st_4d_centroid` aggregate over the species' firefly cluster)
2. Inverse Laplacian eigenmap from consensus 4D centroid → V × N' embedding matrix in target architecture's basis
3. Honest abstention: cells with insufficient attestation density stay at exact zero
4. Output as standards-compliant safetensors loading in vLLM / llama.cpp / HuggingFace transformers

Result: target architecture gets a NEW embedding matrix in its N'-dim space, derived from substrate's consensus across every model that contributed fireflies.

**Symmetry**: the projection from N-dim to 4D throws away dimensions (lossy compression into the universal frame). The projection from 4D to N-dim adds dimensions (uplift from universal frame to target). Same eigenmap + Procrustes math; just direction reversed.

This is what makes substrate synthesis structurally non-distillation. Conventional distillation uses gradient descent to make a student model's outputs match a teacher's. Substrate synthesis does NOT need a teacher's gradient — it projects from the universal 4D embedding (which the teacher already contributed to) back into the student's target dimension. No gradient. No teacher-forcing. Pure mathematical inverse projection.

### Entity-tier vs content-tier physicality role split

Two distinct uses of the same 4D physicality table, with different semantics:

**Entity-tier physicality (real coordinates)** — the brick's own internal structure
- An atom POINTZM has **real content-derived coordinates** per the partition's CHECK constraint
  - Codepoint atom: (X, Y, Z, M) = Super-Fibonacci S³ position by UCA rank (real unit quaternion components)
  - Audio sample atom: M = sample value, X/Y/Z = time-since-trigger / channel / frequency (real)
  - Pixel intensity atom: (X, Y, Z, M) = pixel position + intensity values (real)
  - Tensor cell atom: real weight value with axis convention per partition
- An entity-tier composition LINESTRINGZM (e.g. `word_form("cat")` = 3-vertex LINESTRING through codepoint atoms) has vertices that are **real centroid POINTZMs** of its tier-below entity-tier children
- `centroid(entity-tier composition) = arithmetic mean of children's centroids` — the centroid IS the deterministic 4D embedding position for the entity
- These positions are what every cross-modal alignment / Voronoi / Fréchet / Hausdorff query operates against

**Content-tier physicality (mantissa-packed indexed child manifest)** — a trajectory THROUGH entity bricks
- A content-tier composition LINESTRINGZM (e.g. `text_composition("The cat sat on the mat...")`) is an OBSERVATION using entity bricks
- Vertices are NOT real coordinates. Each vertex packs:
  - X = `bb_pack_hash_lo(child.hash_bits_0_51)` — first half of child entity's BLAKE3 hash
  - Y = `bb_pack_ordinal_rle(ordinal, rle_count)` — position in trajectory with run-length encoding
  - Z = `bb_pack_hash_hi(child.hash_bits_52_103)` — second half of child entity's BLAKE3 hash
  - M = `bb_pack_metadata(...)` — reserved
- Reverse-resolve from any vertex to its child entity hash via composite btree on `substrate.entity_by_hash_prefix` `(hash_bits_0_51, hash_bits_52_103)`
- This is the **AI-functionality layer** — fast microsecond reverse-resolution from geometry back to child entity
- The "geometry" of a content-tier composition is not its embedding position; it's its indexed child manifest

The same 4D physicality TABLE holds both. The CHECK constraint per partition declares which role (and what axis convention). Cross-modal / cross-entity geometric operations (Fréchet on edge trajectories, Hausdorff over firefly clouds, Voronoi consensus, idiomaticity cascade) operate on entity-tier physicality where coordinates are real. Sequence / re-emission / composition-child enumeration operates on content-tier physicality where vertices are mantissa-packed hash references.

### Per-modality embedding-space participation (illustrative)

| Modality | Atom embedding (POINTZM real coords) | Composition embedding (centroid recursion) | Content trajectory (LINESTRINGZM mantissa-packed) |
|---|---|---|---|
| Text | codepoint via Super-Fibonacci S³ by UCA rank | grapheme cluster → word_form → morpheme/lemma → synset centroids via mean recursion | text_composition / paragraph / document = LINESTRINGZM through word_form hash references |
| Audio | audio sample value with time-since-trigger axis | audio_recording / audio_chunk centroids via mean recursion of sample atoms | content trajectory through audio_chunk hash refs |
| Image | pixel intensity with 2D position + class | pixel_region centroids via mean recursion of pixel atoms | image content trajectory through pixel_region hash refs |
| Video | per-frame pixel atom with time axis | video_frame centroids; cross-frame compositions | full video trajectory through video_frame hash refs |
| AI model | tensor cell with axis convention per partition; embedding rows project to firefly POINTZM via Laplacian eigenmap + Procrustes | per-tensor / per-layer centroids of cell atoms | model architecture trajectory through tensor / layer hash refs |
| Application telemetry | event atom with time axis | call chain / request trace centroids | session trajectory through request hash refs |

ALL of these live in the SAME 4D embedding space. Cross-modal alignment between text and image is geometric: `model_cross_modal_alignment(word_form, pixel_region)` puts two entities — both with real 4D positions in the same coordinate frame — into the same arena's edge geometry. Cross-architecture model comparison is geometric: two LLMs' fireflies for the same word_form, both in 4D after Procrustes, get a meaningful Voronoi consensus.

### Why this matters at the audit level

The "4D space" framing throughout the rest of the frame docs is too passive. Most existing docs treat 4D as a property OF the substrate ("substrate uses 4D physicality"). The correct framing: **the 4D space IS the substrate's coordinate system**, deliberately constructed, that every operation happens with respect to. Inference is A* traversal IN this coordinate system. Idiomaticity is centroid/Fréchet/Hausdorff IN this coordinate system. Voronoi consensus is partition OF this coordinate system. Substrate synthesis is inverse projection OUT of this coordinate system. Firefly clouds are AI model embeddings projected INTO this coordinate system. Cross-modal alignment is co-occupancy IN this coordinate system.

The implications I overlooked in the existing frame docs (all queued for cross-reference edits):
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — fireflies are not "the firefly jar"; they are AI model embeddings projected INTO the substrate's pre-existing universal embedding space
- `frame/09-RECOMPOSERS-SYNTHESIS.md` — substrate synthesis is not just "synthesis from consensus over edge attestations"; it is also inverse projection from the universal 4D embedding back into target architecture's N-dim
- `frame/26-MANTISSA-EXPLOITATION.md` — atom POINTZMs carry REAL coordinates (the embedding); composition LINESTRINGZMs carry mantissa-packed indexed child manifest (the AI-functionality layer). Two layers in the same table; conflated in the existing doc.
- `frame/17-THREE-LEVEL-IDIOMATICITY.md` / `frame/18-FRAYED-EDGE-DETECTION.md` / `frame/19-MULTI-MODEL-PERSPECTIVE-QUERY.md` / `frame/20-VORONOI-CONSENSUS.md` — all operate in the substrate's 4D embedding space; should call this out
- `frame/22-NATIVE-COMPUTE-FACADE.md` — Procrustes alignment in `procrustes.c` is the operation that gives an AI model citizenship in the substrate's coordinate frame
- `frame/07-INFERENCE-ENGINE.md` — A* traversal IS traversal through the 4D-embedded substrate

Cross-references:
- `frame/02-SUBSTRATE-MODEL.md` — the substrate model (4D embedding space is the central column)
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — AI model embeddings as projections INTO 4D
- `frame/09-RECOMPOSERS-SYNTHESIS.md` — substrate synthesis as projection OUT of 4D
- `frame/26-MANTISSA-EXPLOITATION.md` — atom POINTZM real coords vs composition LINESTRINGZM mantissa-packed
- `frame/22-NATIVE-COMPUTE-FACADE.md` — Procrustes / Laplacian eigenmap implementations
- `frame/00-FOUNDATIONAL.md` — supplements the the substrate + practitioner-bound properties with phase ordering + embedding-space framing
