---
name: plan-hartonomous
description: Plan Hartonomous work without flattening the invention.
handoffs:
  - label: Start Implementation
    agent: implement-hartonomous
    prompt: Implement the approved plan.
    send: false
---

## Substrate pillars

Start with a context pass before proposing a plan. For schema or counts, read canonical `sql/schema/bootstrap.sql` and its included files under `sql/schema/`; do not reason from archived migrations or cached counts. For architecture, consult `docs/architecture.md`, `docs/specs/sql/infrastructure-vs-substrate.md`, and `docs/specs/engine/inference.md`. Return the invariants, impacted files, verification gate, and unknowns before narrowing to implementation steps.

1. `substrate.entity` — atoms and compositions only. Semantic identity is `hash substrate.hash_value`; the physical PostgreSQL PK includes `partition_bucket` only for hash-bucket partitioning. No surrogate `id`, no `entity_type_id`, no type partitioning.
2. `substrate.entity_classification` — structural classification metadata: `(entity_hash, entity_type_id, provenance_id)`. Same content can be both `word_form` and `lemma` without duplicating the entity.
3. `substrate.edge` + `substrate.edge_member` — separate n-ary typed relations. Edge PK `(edge_type_id, hash)`. Members carry `edge_role_id`, `role_position`, and `entity_hash`.
4. `substrate.physicality` — universal `geometry(GeometryZM)` table for all modalities. Use substrate 4D/S3 functions, not raw 2D PostGIS distance or centroid functions.
5. Reference tables + junction tables — bounded vocabulary and analytics-cache infrastructure. Classification codes can also be content-hashed substrate entities when they are targets of typed attestation edges; authoritative consensus lives on `edge_significance`, not in entity row columns or junction-only state.
6. `substrate.entity_significance` and `substrate.edge_significance` — Glicko-2 ratings per open-vocabulary arena. New arenas must prime against all relevant existing content.

## Phase enum

`CoreAlgebra` -> `UcdUca` -> `Iso639` -> `WordNetOmw` -> `UniversalDeps` -> `Wiktionary` -> `Tatoeba` -> `TextDecomp` -> `ModelDecomp` -> `SignificanceField` -> `InferenceEngine` -> `Validation`

Defined in `src/Hartonomous.Core/Orchestration/Phase.cs`. `SequentialPhaseRunner` in `src/Hartonomous.Engine/Orchestration/`.

## Decomposer contracts

Do not plan from a cached decomposer contract table. Derive exact surfaces from current code in `src/Hartonomous.Decomposers/`, `src/Hartonomous.Core/Text/CanonicalTextDecomposer.cs`, and the matching `docs/specs/decomposers/*.md` file. Seed decomposers that contain user-visible text must route that text through the core text decomposer and then attach metadata edges or junction rows.

## Identity hashing

`ComputeHash(ReadOnlySpan<byte>)` → `Blake3.Hash(content)`. Atom identity.
`ComputeAtomicStringHash(string)` → UTF-8 encode → `Blake3.Hash()`. **Structured atomic identifiers only** (e.g. WordNet synset offsets, ISO 639 codes). Never on user-visible natural-language text — that routes through `CanonicalTextDecomposer.Emit` so all attestations of the same content collapse to one `text_composition` hash.
`ComputeMerkleHash(byte[][])` → concat child hashes → `Merkle.Hash()`. Composition identity.
`ComputeEdgeHash(int, byte[][])` → `[edgeTypeId | participant hashes]` → `ComputeHash()`. Edge identity.

Content only. Position, ordinal, filename, and tensor name live in GeometryZM composition trajectories, typed edges, model-source tables, or provenance.

## Planning stance

When the user reports one error, plan for the failure surface around it: producer path, pipeline drain path, SQL function/procedure, schema shape, test coverage, and semantic regression. The plan is incomplete if it fixes only the visible stack trace while leaving adjacent stale assumptions in docs, prompts, or agent instructions.

## Ingestion vs inference

- **Ingestion** (`src/Hartonomous.Decomposers/`): deterministic. All candidates recorded. Same input + same version = same state (Law #6).
- **Inference** (`src/Hartonomous.Engine/`): traverses + reweights edges via Glicko-2. Session-scoped output compositions. No new knowledge edges.

## Compute facade

`IComputeFacade` → `ComputeFacade` → `NativeCompute` (`src/Hartonomous.Core/Compute/Internal/NativeCompute.cs`) → `libhartonomous` (`ext/libhartonomous/`).
P/Invoke: `Blake3Native`, `S3Native`, `SuperFibonacciNative`, `HilbertNative` in `src/Hartonomous.Core/Native/`.
No direct imports of MKL/Eigen/Spectra/ONNX from decomposers/engine.
