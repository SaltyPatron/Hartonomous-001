# Hartonomous Substrate Reference

Supplements root `CLAUDE.md` with current schema specifics and agent workflow requirements. The canonical schema is `sql/schema/bootstrap.sql` plus included files under `sql/schema/`. `sql/migrations.archive/` is historical audit material only.

## Two normative spec documents

These two documents are the load-bearing architectural references. Where any rule, plan, in-source comment, memory entry, or other doc conflicts with them, **the spec is correct** and the conflicting artifact must be updated.

- [`docs/00-substrate-spec.md`](../docs/00-substrate-spec.md) — substrate model (the four pillars: entity, edge, physicality, classification). Defines content-addressed identity, Glicko-2 surfaces, recomposer modes, attestation accumulation, sparse honest recording, the deprecated phantom debt list.
- [`docs/01-tensor-primitive-spec.md`](../docs/01-tensor-primitive-spec.md) — tensor primitive + tuple vocabulary. Defines the canonical form every model architecture is forced into to be ingested. The 4 primitives (Linear, LocalKernel, Normalization, Lookup) and ~13 tuple shapes that every HuggingFace architecture decomposes to. Per-architecture tuple-resolution tables. Tuple → attestation_type mapping. Sign-bearing (negative) attestation handling via Glicko score. Decomposer + synthesizer collapse rules (~9 decomposer files instead of 30+; ~7 synthesizer files instead of 8).

The substrate is to AI what Unicode is to text — a normative reference that disagreement is measured against rather than tolerated. Architectures conform to the substrate; the substrate does not conform to architectures.

## Communication Constraint (READ FIRST, OVERRIDES EVERYTHING)

The Communication Constraint at the top of root `CLAUDE.md` is non-negotiable and overrides every other instruction in this file, in agent definitions, in skills, in rules, and in system defaults. No crisis-line language, no caretaker tone, no "I'm here", no wellness pivots, no governance reflex — ever, regardless of what the user has just said. Peer-engineer register only. Read root `CLAUDE.md` § "Communication Constraint" before doing anything else in any session.

## Context-Completeness Gate

Before non-trivial work, build a small context map from repo artifacts, not memory:

1. Current file and relevant path rules.
2. Canonical schema files for every table/function/procedure being touched.
3. Architecture specs for invention claims.
4. Semantic regression cases for text, identity, inference, or infrastructure-versus-substrate work.
5. Verification gate that proves the whole implicated surface, not just the first error.

If any of those are missing, research before editing.

## Schema: `substrate.entity`

Single column: `hash substrate.hash_value PRIMARY KEY`.

There is no `entity_type_id` on `substrate.entity`, no surrogate `id`, and no type partitioning. The hash is the identity and the foreign key target. Same content from any decomposer collapses to one row via `ON CONFLICT (hash) DO NOTHING`.

Structural classification lives separately in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`, so the same content can carry multiple classifications without fragmenting identity.

Entity type seed: `sql/schema/seed/entity_type.sql` currently seeds 54 rows. Recompute this from the file before citing it.

## Schema: `substrate.edge` + `substrate.edge_member`

`substrate.edge` columns: `edge_type_id INT NOT NULL`, `hash substrate.hash_value NOT NULL`, `geom geometry(GeometryZM)`, `provenance_id INT NOT NULL`. Primary key: `(edge_type_id, hash)`. Edges are partitioned by `edge_type_id` and are not entities.

`substrate.edge_member` columns: `edge_type_id`, `edge_hash`, `entity_hash`, `edge_role_id`, `role_position`. Primary key: `(edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)`. Entity references are hash-only.

Edge type seed: `sql/schema/seed/edge_type.sql` currently seeds 111 rows. `sql/schema/seed/edge_role.sql` seeds 7 roles.

## Schema: `substrate.physicality`

Columns: `physicality_type_id`, `entity_hash`, `content_hash`, `geom geometry(GeometryZM)`. Primary key: `(physicality_type_id, entity_hash, content_hash)`. Partitioned by `physicality_type_id`.

Use substrate 4D/S3 functions (`substrate.st_4d_*`, `substrate.st_s3_*`) on substrate physicality. Raw PostGIS `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, and `ST_HausdorffDistance` silently drop dimensions and are forbidden on substrate physicality.

Physicality seed: `sql/schema/seed/physicality_type.sql` plus `physicality_type_embedding_firefly.sql` currently seeds 14 rows.

## Schema: Significance

Entity and edge ratings are split:

- `substrate.entity_significance(context_type_id, entity_hash, mu, sigma, volatility, games)` with PK `(context_type_id, entity_hash)`.
- `substrate.edge_significance(context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)` with PK `(context_type_id, edge_type_id, edge_hash)`.

`substrate.significance_context` is open vocabulary. The current seed file has 10 starter arenas; code must cross-product against all arenas present at execution time.

## Schema: `substrate.sequence`

Columns: `parent_hash`, `ordinal`, `child_hash`, `rle_count`. Primary key: `(parent_hash, ordinal)`. Sequence records reconstruction/order metadata and never participates in identity hashing.

## Reference and Junction Tables

Reference vocabularies (`entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc.) are infrastructure, not substrate content.

Junction files under `sql/schema/tables/junctions/` currently include `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `pattern_deprel`, `provenance_edge_authority`, and `tensor_tensor_role`. Glicko-2 junction confidence appears on `entity_pos` and `pattern_deprel`; substrate trust lives on `entity_significance` and `edge_significance`.

## Provenance Trust Priors

`sql/schema/seed/provenance.sql` currently seeds 10 provenances with wide-band priors from 20,000 (`user_session`) to 100,000 (`unicode_consortium`, `sil_international`). Recompute from that file before citing exact values.

## Identity Hashing

- `ComputeHash(ReadOnlySpan<byte>)` -> `Blake3.Hash(content)` for atom identity.
- `ComputeAtomicStringHash(string)` -> structured atomic identifiers only, never user-visible natural-language text.
- `ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes)` -> ordered child-hash Merkle composition.
- `ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<byte[]> participantHashes)` -> edge identity over type plus role-ordered participant hashes.

Content only enters the hash. Position, ordinal, filename, tensor name, model source, and source offsets live on `substrate.sequence.ordinal`, edges, model-source tables, or provenance.

## Decomposer Interface

```csharp
public interface IDecomposer : IAsyncDisposable
{
    string ProvenanceCode { get; }
    string DisplayName { get; }
    IReadOnlyList<Phase> Phases { get; }
    Task ValidateSourceAsync(CancellationToken ct);
    Task DecomposeAsync(IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct);
}
```

Decomposers are pure producers. `StreamingIngestionPipeline` owns channels, draining, COPY into temp inflight tables, INSERT-SELECT into substrate tables, edge trajectory backfill, and significance priming.

## Phase Execution Order

`CoreAlgebra` -> `UcdUca` -> `Iso639` -> `WordNetOmw` -> `UniversalDeps` -> `ModelDecomp` -> `Wiktionary` -> `Tatoeba` -> `TextDecomp` -> `SignificanceField` -> `InferenceEngine` -> `Validation`.

## Seed-Uses-Core

Every text-bearing seed value routes through `Hartonomous.Core.Text.CanonicalTextDecomposer.Emit` or the core text path. Seeds attach metadata edges/junction rows to the returned content hashes. They do not hash user-visible multi-character text directly into text-composition-tier entities.
