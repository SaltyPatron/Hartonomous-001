# Multi-tenancy — provenance scoping and tenant isolation

Source: `docs/10-architecture/16-multi-tenancy.md`.

## What multi-tenancy means here

Substrate is a single content-addressed graph database. Multi-tenancy is the discipline by which:
- **Public substrate state** (universal seeds: UCD, ISO 639, Princeton WordNet, OMW, Wiktionary, Tatoeba, ATOMIC 2020, foundational AI models) is shared across all tenants.
- **Tenant-private substrate state** (customer's proprietary corpora, custom recipes, internal knowledge) is visible only to that tenant.
- **Cross-tenant queries** are possible but controlled — tenant can opt-in to share specific provenance classes with other tenants under explicit licensing.

**Multi-tenancy is implemented through provenance scoping, NOT through schema partitioning.** One schema, one entity table, one edge table; tenant boundaries encoded in provenance and enforced at query time via row-level security and provenance-filtered traversal.

This is deliberate. Schema-per-tenant would forfeit substrate's content-addressed deduplication. Tenant ingesting "café" should benefit from substrate already having that atom from public seeds; separate-schema tenant would re-store the bytes. Provenance scoping preserves deduplication across tenants while enforcing visibility.

## Provenance class hierarchy

Every substrate atom, composition, and edge carries `provenance` JSONB field with inherent class hierarchy:

```
public/
├── seed/
│   ├── unicode/                        ← UCD, UCA
│   ├── language/                       ← ISO 639, OMW
│   ├── lexical/                        ← Princeton WordNet, Wiktionary
│   ├── corpora/                        ← Tatoeba, ATOMIC 2020
│   └── models/                         ← foundational AI models
├── operator/                           ← substrate-operator-curated additions
└── consensus/                          ← cross-source converged content

private/
├── tenant:<tenant_id>/
│   ├── ingest/                         ← tenant's ingested corpora
│   ├── recipe/                         ← tenant's custom recipes
│   ├── outcome/                        ← tenant's outcome events
│   └── inference_trace/                ← tenant's session traces
└── shared/<sharing_group_id>/          ← cross-tenant shared spaces (opt-in)

internal/
├── audit/                              ← substrate-internal audit traces
├── macro_ooda/                         ← macro-OODA's own state
└── system/                             ← substrate operations metadata
```

Class strings are dotted paths: `public.seed.unicode`, `private.tenant:acme-corp.ingest`, `internal.audit`, etc.

**Single piece of substrate state can have MULTIPLE provenance entries** — when content ingested by multiple tenants or sources, each provenance is appended (not replaced). This is how content-addressed deduplication coexists with tenant scoping: atom is shared (same `atom_id`), but its provenance list has multiple entries; query-time scoping reveals which tenant(s) see it.

## Tenant identity

Unique by `tenant_id` UUID. Substrate stores tenant metadata as `tenant` entities:
- `tenant_id` (UUID)
- `display_name`
- `created_at`
- `subscription_tier` (which substrate features they have access to)
- `data_residency_constraint` (if any — for EU customers requiring EU-only storage)
- `default_inference_arenas`
- `provenance_share_groups`

Tenants are NOT atomically content-addressed because tenant identity is metadata, not content. `tenant` entity created via substrate operator surface, not via ingestion of bytes.

## Visibility rules (4 classes)

For any substrate row (atom, composition, edge, inference_trace):
- Row visible to tenant T if its provenance list contains at least one entry T has rights to see.
- T has rights to see provenance class P if any of:
  - P starts with `public.` (universal access)
  - P starts with `private.tenant:<T's tenant_id>.` (own tenant)
  - P starts with `private.shared/<G>.` AND T is member of sharing group G
  - T has been granted explicit cross-tenant read access (rare; legal/contractual review)

Enforcement:
1. **Postgres row-level security (RLS)** policies on every substrate table, using session-bound `current_tenant_id` setting
2. **Tenant-scoped traversal** in inference engine: `traverse_astar` filters edges and entities by visibility before considering them admissible
3. **Audit-trail visibility**: tenant's audit traces visible to that tenant (and substrate operators); other tenants cannot see them

## How a tenant ingests

Standard ingestion pipeline, with `tenant_id` injected into provenance of every emitted atom, composition, edge:
```json
provenance = {
  "class": "private.tenant:acme-corp.ingest",
  "tenant_id": "acme-corp-uuid",
  "source": "acme-corp internal docs / 2026 Q1",
  "ingested_at": "2026-04-30T14:23:11Z",
  "ingestor_version": "v0.7.3"
}
```

If atom emitted during ingestion ALREADY EXISTS in substrate (public seed or other tenant has ingested same byte sequence), existing atom is reused. New provenance entry APPENDED to atom's provenance list. Atom now has multi-source provenance.

When public-seeded WordNet content queried by tenant A (who has not ingested anything privately), atom returned with public provenance only. When tenant ACME queries, atom returned with both provenance entries — they see same atom enriched with their own provenance trail.

## Cross-tenant queries via sharing groups

Tenants opt into sharing groups. `sharing_group` substrate entity:
- `sharing_group_id` (UUID)
- `members` (list of tenant_id)
- `share_classes` (which provenance classes within each member's tenant scope are shared into group)
- `access_terms` (legal/contractual reference)

When tenant A and tenant B are in sharing group G with share_classes = [`private.tenant:A.ingest`, `private.tenant:B.ingest`], any atom/composition/edge whose provenance includes one of those classes ALSO has provenance entry like:
```json
{"class": "private.shared/G.from_tenant:A", "tenant_id": "A", ...}
```

This dual provenance entry makes row visible to B (via shared/G class) without losing original tenant:A provenance.

Sharing groups accommodate partner-corpus collaborations — consortium of medical research institutions sharing their ingested literature without each having to re-ingest each other's corpora. Each institution remains distinct tenant; consortium is sharing group.

## Recipe marketplace

Recipes are substrate compositions. By default, recipe authored by tenant A has provenance `private.tenant:A.recipe`. Visible only to A.

Tenant publishes recipe by:
1. Creating recipe atom with public-share intent
2. Submitting to substrate operator's recipe-marketplace endpoint
3. After review, recipe re-ingested with provenance `public.operator.recipe_marketplace` and publication metadata entry pointing back to original tenant

Recipes in marketplace visible to all tenants. Original author credited; usage logged (per-tenant, not exposing other tenants' usage to each other).

## Per-tenant Glicko-2 ratings

Outcome events carry provenance. Tenant's outcome events scoped to that tenant's view of arena ratings.

Tenant A's outcome events drive A's view of edges' ratings; tenant B's outcome events drive B's view. Views CAN diverge. A's edge "metformin treats type-2-diabetes" might have mu=1900 because A's outcome events from oncology research kept upvoting metformin-cancer connection; B's view of same edge might have mu=1700 because B's events were neutral.

Substrate stores per-tenant ratings in `tenant_arena_rating` partitioned by tenant. Default arena view (cross-tenant aggregate) also stored as canonical `arena_rating`; tenants with light usage typically operate on canonical view; large enterprise tenants get diverged per-tenant views.

This enables substrate to be refinement-as-service: every tenant's domain expertise refines THEIR view, while canonical view aggregates the field. Substrate operators choose how to aggregate per-tenant ratings into canonical view (typically: weighted by tenant authority).

## Inference traces are tenant-scoped

Every inference call produces `inference_trace` entity with provenance `private.tenant:<T>.inference_trace`. Visible only to T (and substrate operators for support).

Cross-tenant trace contamination structurally impossible: recipe owned by tenant A inferring over substrate state shared with B produces trace with provenance `private.tenant:A.inference_trace`, NOT visible to B.

When tenant A invokes public-marketplace recipe authored by C, trace's provenance is still `private.tenant:A.inference_trace`. Recipe's provenance (public.operator) appears in trace's `recipe_used` field but does not change trace's tenant scope.

## Substrate operator role

Substrate operators (company running substrate in production) have visibility across all tenants for support and operational purposes:
- **Audited**: every operator query logged to substrate-internal audit with operator's identity
- **Scoped**: separate roles for billing/usage/support, each with minimum necessary privileges
- **Reviewed**: cross-tenant queries (rare; only for incident response or migration) require multi-operator approval

Substrate operators do NOT modify tenant data without explicit tenant authorization. Tenant data ingestion, recipe authorship, outcome events — all originate from tenant actions, not operator actions. Operator surface includes substrate-administrative actions (provisioning new tenants, managing seed updates, configuring macro-OODA schedules) but excludes tenant-content modification.

## Data residency

Tenants (typically EU or regulated regions) require data residency constraints — their substrate state must physically reside in specific geographic regions.

Deployment topology supports per-region partitions: tenant tagged with `data_residency_constraint = "EU"` has atoms/compositions/edges/traces/outcomes stored exclusively in EU-region nodes. Public seed state may or may not be replicated to EU partition (typically yes; substrate operators run public seeds in every region for availability).

Cross-region inference supported: tenant's EU-resident state queryable from any operator endpoint, but actual data fetch happens in-region. Cross-region traversals (e.g., through public seeds living in multiple regions) routed appropriately.

Data residency does NOT change substrate's logical model — tenant scoping is provenance-based regardless of physical location. Residency is deployment-topology concern enforced at Postgres level (tablespaces, partition placement, replication topology).

## Tenant offboarding

When tenant offboards:
1. Tenant's `private.tenant:<T>.*` provenance entries flagged for deletion
2. Scheduled job sweeps substrate, removing tenant-scoped provenance entries
3. Atoms whose provenance list becomes EMPTY after sweep (no public seed, no other tenant) scheduled for atom-level deletion
4. Atoms with remaining provenance (because also public-seeded or shared with other tenants) remain in substrate; only tenant's provenance removed
5. Per-tenant arena ratings, sharing-group memberships, inference traces deleted
6. Operator audit trail records offboarding for compliance

Substrate's content-addressed nature means offboarding tenant cannot "delete shared content" — if atom is also used by other tenants or by public seeds, it persists. Tenant's CONTRIBUTION removed (their provenance, their outcomes' effects, their recipes), not the content itself.

Consistent with how substrate handles knowledge: substrate accumulates verified knowledge from many sources; offboarding source removes its attribution but cannot retract knowledge that other sources have independently corroborated.

Cross-references:
- `frame/15-AUDIT-CHAIN.md` — per-tenant audit visibility model
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — per-tenant Glicko-2 divergence over time
- `frame/13-SUBSTRATE-GOVERNANCE.md` — per-tenant rule sets composability
- `frame/01-SUBSTRATE-LAWS.md` — Law 13 (fail loud) applied to tenant-scope violations
- `frame/12-RECIPE-DSL.md` — recipe sharing / publication workflow
