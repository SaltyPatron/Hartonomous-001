# Multi-Tenancy — Provenance Scoping and Tenant Isolation

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the tenant-scoping infrastructure, anyone designing customer-onboarding flows, anyone reasoning about how the substrate accommodates multiple customers without cross-contamination.

---

## What multi-tenancy means here

The substrate is a single content-addressed graph database. Multi-tenancy is the discipline by which:

- **Public substrate state** (universal seeds: UCD, ISO 639, Princeton WordNet, OMW, Wiktionary, Tatoeba, ATOMIC 2020, foundational AI models, etc.) is shared across all tenants.
- **Tenant-private substrate state** (a customer's proprietary corpora, custom recipes, internal knowledge) is visible only to that tenant.
- **Cross-tenant queries** are possible BUT controlled — a tenant can opt-in to share specific provenance classes with other tenants under explicit licensing.

Multi-tenancy is implemented through **provenance scoping**, not through schema partitioning. There is one schema, one entity table, one edge table; tenant boundaries are encoded in provenance and enforced at query time via row-level security and provenance-filtered traversal.

This approach is deliberate. The alternative — separate schemas per tenant — would forfeit the substrate's content-addressed deduplication. A tenant ingesting "café" should benefit from substrate already having that atom from public seeds; a separate-schema tenant would re-store the bytes. Provenance scoping preserves deduplication across tenants while enforcing visibility.

## Provenance class hierarchy

Every substrate atom, composition, and edge carries a `provenance` JSONB field. The provenance has an inherent class hierarchy:

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

Provenance class strings are dotted paths: `public.seed.unicode`, `private.tenant:acme-corp.ingest`, `internal.audit`, etc.

A single piece of substrate state can have MULTIPLE provenance entries — when content is ingested by multiple tenants or sources, each provenance is appended (not replaced). This is how content-addressed deduplication coexists with tenant scoping: the atom is shared (same `atom_id`), but its provenance list has multiple entries; query-time scoping reveals which tenant(s) see it.

## Tenant identity

A tenant is uniquely identified by a `tenant_id` UUID. The substrate stores tenant metadata as `tenant` entities:

- `tenant_id` (UUID)
- `display_name`
- `created_at`
- `subscription_tier` (which substrate features they have access to)
- `data_residency_constraint` (if any — for EU customers requiring EU-only storage; affects deployment topology, not substrate logic)
- `default_inference_arenas`
- `provenance_share_groups` (which sharing groups this tenant is a member of)

Tenants are NOT atomically content-addressed because the tenant identity is metadata, not content. A `tenant` entity is created via the substrate operator surface, not via ingestion of bytes.

## Visibility rules

For any substrate row (atom, composition, edge, inference_trace):

- A row is **visible to tenant T** if its provenance list contains AT LEAST ONE entry that T has rights to see.
- T has rights to see provenance class P if any of:
  - P starts with `public.` (universal access)
  - P starts with `private.tenant:<T's tenant_id>.` (own tenant)
  - P starts with `private.shared/<G>.` AND T is a member of sharing group G
  - T has been granted explicit cross-tenant read access (rare; subject to legal/contractual review)

Visibility rules are enforced by:

1. **Postgres row-level security (RLS)** policies on every substrate table, using the session-bound `current_tenant_id` setting.
2. **Tenant-scoped traversal** in the inference engine: `traverse_astar` filters edges and entities by visibility before considering them admissible.
3. **Audit-trail visibility**: a tenant's audit traces are visible to that tenant (and substrate operators); other tenants cannot see them.

## How a tenant ingests

A tenant ingests new corpora through the standard ingestion pipeline (see `30-ingestion/01-pipeline-overview.md`), but with their `tenant_id` injected into the provenance of every emitted atom, composition, and edge:

```
provenance = {
  "class": "private.tenant:acme-corp.ingest",
  "tenant_id": "acme-corp-uuid",
  "source": "acme-corp internal docs / 2026 Q1",
  "ingested_at": "2026-04-30T14:23:11Z",
  "ingestor_version": "v0.7.3"
}
```

If an atom emitted during this ingestion happens to ALREADY EXIST in substrate (because a public seed or another tenant has ingested the same byte sequence), the existing atom is reused. The new provenance entry is APPENDED to the atom's provenance list. The atom now has multi-source provenance:

```jsonc
[
  {"class": "public.seed.lexical", "source": "Princeton WordNet 3.1", ...},
  {"class": "private.tenant:acme-corp.ingest", "source": "acme-corp internal docs / 2026 Q1", ...}
]
```

When the public-seeded WordNet content is queried by tenant A (who has not ingested anything privately), the atom is returned with public provenance only. When tenant ACME queries, the atom is returned with both provenance entries — they see the same atom enriched with their own provenance trail.

## Cross-tenant queries via sharing groups

Tenants can opt into sharing groups. A sharing group is a substrate entity (`sharing_group`) with:

- `sharing_group_id` (UUID)
- `members` (list of `tenant_id`)
- `share_classes` (which provenance classes within each member's tenant scope are shared into the group)
- `access_terms` (legal/contractual reference)

When tenant A and tenant B are in sharing group G with share_classes = [`private.tenant:A.ingest`, `private.tenant:B.ingest`], any atom/composition/edge whose provenance includes one of those classes ALSO has a provenance entry like:

```jsonc
{"class": "private.shared/G.from_tenant:A", "tenant_id": "A", ...}
```

This dual provenance entry is what makes the row visible to B (via the shared/G class) without losing the original tenant:A provenance.

Sharing groups are how the substrate accommodates partner-corpus collaborations — for example, a consortium of medical research institutions sharing their ingested literature without each having to re-ingest each other's corpora. Each institution remains a distinct tenant; the consortium is a sharing group.

## Recipe sharing

Recipes are substrate compositions. By default, a recipe authored by tenant A has provenance `private.tenant:A.recipe`. The recipe is visible only to A.

A tenant can publish a recipe by:
1. Creating a recipe atom with public-share intent.
2. Submitting it to the substrate operator's recipe-marketplace endpoint.
3. After review, the recipe is re-ingested with provenance `public.operator.recipe_marketplace` and a publication metadata entry pointing back to the original tenant.

Recipes in the marketplace are visible to all tenants. The original author is credited; usage of the recipe is logged (per-tenant, not exposing other tenants' usage to each other).

## Outcome events and Glicko-2 per tenant

Outcome events (which drive Glicko-2 updates; see `10-architecture/04-arenas.md`) carry provenance. A tenant's outcome events are scoped to that tenant's view of arena ratings.

This means: tenant A's outcome events drive A's view of edges' ratings; tenant B's outcome events drive B's view. These views CAN diverge. A's edge "metformin treats type-2-diabetes" might have mu=1900 because A's outcome events from oncology research kept upvoting the metformin-cancer connection; B's view of the same edge might have mu=1700 because B's events were neutral.

The substrate stores per-tenant Glicko ratings in a `tenant_arena_rating` table partitioned by tenant. The default arena view (cross-tenant aggregate) is also stored as the canonical `arena_rating`; tenants with light usage typically operate on the canonical view, while large enterprise tenants get diverged per-tenant views.

This is what enables the substrate to be a refinement-as-service offering: every tenant's domain expertise refines THEIR view of the substrate, while the canonical view aggregates the field. Substrate operators choose how to aggregate per-tenant ratings into the canonical view (typically: weighted by tenant authority, see arena documentation).

## Inference traces are tenant-scoped

Every inference call produces an `inference_trace` entity with provenance `private.tenant:<T>.inference_trace`. This trace is visible only to T (and substrate operators for support purposes).

Cross-tenant trace contamination is structurally impossible: a recipe owned by tenant A inferring over substrate state shared with B will produce a trace with provenance `private.tenant:A.inference_trace`, NOT visible to B.

When tenant A invokes a public-marketplace recipe authored by C, the inference trace's provenance is still `private.tenant:A.inference_trace`. The recipe's provenance (public.operator) appears in the trace's `recipe_used` field but does not change the trace's tenant scope.

## Substrate operator role

Substrate operators (the company running the substrate, in production) have visibility across all tenants for support and operational purposes. Operator access is:

- Audited: every operator query is logged to substrate-internal audit with the operator's identity.
- Scoped: operators have separate roles for billing/usage/support, each with minimum necessary privileges.
- Reviewed: cross-tenant queries (rare; only for incident response or migration) require multi-operator approval.

Substrate operators do NOT modify tenant data without explicit tenant authorization. Tenant data ingestion, recipe authorship, outcome events — all originate from tenant actions, not operator actions. The operator surface includes substrate-administrative actions (provisioning new tenants, managing seed updates, configuring macro-OODA schedules) but excludes tenant-content modification.

## Data residency

Some tenants (typically EU or other regulated regions) require data residency constraints — their substrate state must physically reside in specific geographic regions.

The substrate's deployment topology supports per-region partitions: a tenant tagged with `data_residency_constraint = "EU"` has their atoms, compositions, edges, traces, and outcome events stored exclusively in EU-region nodes. Public seed state may or may not be replicated to the EU partition (typically yes; substrate operators run public seeds in every region for availability).

Cross-region inference is supported: a tenant's EU-resident state is queryable from any operator endpoint, but the actual data fetch happens in-region. Cross-region traversals (e.g., through public seeds living in multiple regions) are routed appropriately.

Data residency does not change the substrate's logical model — tenant scoping is provenance-based regardless of physical location. Residency is a deployment-topology concern enforced at the Postgres level (tablespaces, partition placement, replication topology).

## Tenant offboarding

When a tenant offboards:

1. The tenant's `private.tenant:<T>.*` provenance entries are flagged for deletion.
2. A scheduled job sweeps the substrate, removing tenant-scoped provenance entries.
3. Atoms whose provenance list becomes EMPTY after the sweep (no public seed, no other tenant) are scheduled for atom-level deletion.
4. Atoms with remaining provenance (because they're also public-seeded or shared with other tenants) remain in substrate; only the tenant's provenance is removed.
5. Per-tenant arena ratings, sharing-group memberships, and inference traces are deleted.
6. An operator audit trail records the offboarding for compliance.

The substrate's content-addressed nature means that an offboarding tenant cannot "delete shared content" — if an atom is also used by other tenants or by public seeds, it persists. The tenant's CONTRIBUTION is removed (their provenance, their outcomes' effects, their recipes), not the content itself.

This is consistent with how the substrate handles knowledge: the substrate accumulates verified knowledge from many sources; an offboarding source removes its attribution but cannot retract knowledge that other sources have independently corroborated.

## Cross-references

- Provenance pillar (universal foundation of substrate identity): `10-architecture/05-provenance.md`
- Substrate laws (especially Law 13 — fail loud — applied to tenant-scope violations): `10-architecture/01-substrate-laws.md`
- Audit chain (the per-tenant audit visibility model): `10-architecture/17-audit-chain.md` (forthcoming)
- Ingestion pipeline (where tenant provenance is injected): `30-ingestion/01-pipeline-overview.md`
- Arena dynamics (per-tenant Glicko-2 rating divergence): `10-architecture/04-arenas.md`

## External references

- PostgreSQL row-level security: <https://www.postgresql.org/docs/current/ddl-rowsecurity.html>
- Multi-tenancy patterns in databases: <https://learn.microsoft.com/en-us/azure/azure-sql/database/saas-tenancy-app-design-patterns>
