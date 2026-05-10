# Audit Chain — Provenance Traversal, Snapshot Replay, Cryptographic Integrity

> **Authority note (2026-05-09):** Audit-chain mechanism remains canonical. Where this document references `firefly_consensus` compositions as audit subjects, treat as DEPRECATED per the 2026-05-08 architectural correction — consensus is a derived analytics surface (per [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VII, §X), not a stored composition entity. The audit chain instead traces consensus computations back to their inputs: the firefly POINTZMs (with `entity_model_source` per ingested model), the word_form entity (the species), the ingestion provenance for each contributing model.

**Status:** Mechanism canonical; consensus-as-entity audit references deprecated per the authority note above.
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers implementing audit-chain traversal, anyone designing compliance reporting, anyone reasoning about how the substrate provides cryptographic guarantees about its own history.

---

## What the audit chain is

The audit chain is the substrate's intrinsic, end-to-end traceability mechanism. For any substrate state — an atom, a composition, an edge, an inference output — the substrate can answer:

1. **Who put this here?** Walk back through provenance edges to the originating ingestion sources.
2. **What did this look like at time T?** Replay substrate state at any past timestamp via append-only logs.
3. **Has anything been tampered with?** Verify cryptographic chains over substrate state to detect modification.

The audit chain is NOT a separate logging system bolted onto the substrate. It is an emergent property of three substrate-level invariants:

1. **Content addressing.** Every atom, composition, and edge identity is BLAKE3 of its canonicalized contents. Any modification produces a new identity, so the original is preserved as an unmodified entity rather than overwritten in place.
2. **Append-only state.** Substrate state grows; atoms are not deleted in normal operation. (Tenant offboarding is the rare exception, and is itself audited.)
3. **Provenance edges.** Every state element has at least one provenance attribution; provenance is itself substrate state and itself auditable.

The chain is therefore a graph traversal — a substrate-internal artifact, not a separate logging tier.

## Provenance traversal — "who put this here"

For any substrate row identified by `<entity_id>`, the audit chain produces the full provenance ancestry by:

```sql
SELECT * FROM provenance.audit_chain($entity_id, $arena);
```

The function walks edges of provenance-relevant types (`provenance_of`, `derived_from`, `consensus_member`, `recipe_for`, `ingested_via`, etc.) from the target entity back through:

1. **Direct provenance** — the entity's `provenance` JSONB list, parsed for source class, ingestor, timestamp.
2. **Derivation chain** — for compositions/edges produced by a recipe or transform, the recipe ID and the source compositions.
3. **Consensus contributors** — for `firefly_consensus` compositions, the contributing fireflies and their source models.
4. **Ingestion event** — the ingestion run that wrote the entity, including ingestor version, source corpus URL/hash, ingestion timestamp.
5. **Source-level corroboration** — for entities with multiple provenance entries, all corroborating sources.

The output is a tree (or DAG, for entities with multiple provenance entries) rooted at the entity, with leaves at originating sources. The tree is itself returned as substrate state — every traversal step is a queryable row, so the chain can be inspected, summarized, exported, or rendered.

### Partial traversal

For deep chains (e.g., a derived composition whose source compositions were themselves derived through 12 transformation steps), the audit chain function accepts a depth parameter:

```sql
SELECT * FROM provenance.audit_chain($entity_id, $arena, max_depth => 5);
```

Truncated chains explicitly mark the truncation point with a `truncated_at` flag — Substrate Law 13 (fail loud) requires that callers know they received a partial answer rather than silently being given a tree that looks complete.

### Cross-tenant chains

If an entity has provenance from multiple tenants (via sharing groups), the audit chain's visibility is filtered by the calling tenant's permissions. A tenant sees their own provenance branches AND any branches they have rights to see (public seeds, shared groups). Branches outside their visibility are NOT silently dropped — they are reported as `restricted` placeholders so the caller knows there are additional sources they cannot inspect. This is a deliberate transparency choice; "we have proof but we won't let you see it" is more honest than "this is the complete chain (but actually it isn't)."

## Snapshot replay — "what did this look like at time T"

The substrate's state is append-only at the persistence layer. Every atom, composition, edge, and rating change is recorded with a `created_at` timestamp; for entities that supersede prior versions, `supersedes` edges link to the predecessor.

Snapshot replay queries reconstruct the substrate's logical state at a specified historical timestamp:

```sql
SELECT * FROM substrate.snapshot_at(timestamp '2026-03-15T00:00:00Z');
```

The function returns a temporary view (or materializes a snapshot composition entity, depending on caller request) showing only:
- Entities whose `created_at` ≤ T and that were not yet superseded by T.
- Edges whose `created_at` ≤ T and that were not yet superseded.
- Glicko-2 ratings as of the most recent rating-update event ≤ T.

Snapshot replay is computationally non-trivial: a full-substrate snapshot at an old timestamp may require reading large amounts of historical state. For practical use, snapshot replay is typically scoped:

```sql
SELECT * FROM substrate.snapshot_at(timestamp '2026-03-15', arena => 'medical', max_entities => 10000);
```

### What snapshot replay enables

- **Compliance.** A tenant subject to "right to inspect" demands can produce the substrate state as it was when their query was run.
- **Reproducibility.** An inference that ran in 2026-03 can be replayed against the 2026-03 substrate state, producing the SAME output (because A* is deterministic; see Substrate Law 6). This is important for audit trail of customer-facing decisions.
- **Forensic analysis.** When an inference's output is questioned ("how did the substrate produce this answer?"), snapshot replay reconstructs the exact substrate state at the time of the inference.

### Snapshot replay does NOT include

- **Real-time outcome events.** Outcome events are processed in batches (Glicko updates are scheduled). A snapshot at T reflects the rating state AS OF the most recent batch update ≤ T, not the events that arrived between the last batch update and T.
- **Inference traces from after T.** Naturally — the snapshot is at T.
- **Future state.** Trivially.

Snapshot replay is point-in-time, deterministic, and bounded by the substrate's append-only history.

## Cryptographic integrity — "has anything been tampered with"

The substrate's content-addressing provides an intrinsic integrity guarantee: an atom's `atom_id` is BLAKE3 of its bytes; if the bytes change, the hash changes; if the hash changes, the row is logically a NEW atom and the original is structurally untouched. This is structural integrity at the atom level.

But what about edges and compositions whose identity is BLAKE3 of references to other rows? If an edge's referenced atom is replaced silently, the edge would still hash correctly but its references would now point at "the new thing pretending to be the old thing." Detecting this requires the higher-level chain.

The substrate enforces a chain of cryptographic commitments:

### Per-row commitment

Every row carries:
- Its content-addressed `entity_id` (BLAKE3 of canonicalized contents).
- A `provenance_hash` = BLAKE3 of (entity_id || provenance JSONB || created_at).
- A `parent_chain_hash` = BLAKE3 of (provenance_hash || ingestion_run.parent_chain_hash).

The parent chain hash is a Merkle-style accumulator: each ingestion run starts with the previous run's parent_chain_hash as its initial state and produces a new parent_chain_hash that includes every row written in the run.

### Per-ingestion-run commitment

Each ingestion run produces an `ingestion_run` entity with:
- `run_id` (UUID)
- `parent_chain_hash_in` (the hash this run started from)
- `parent_chain_hash_out` (the hash after this run committed)
- `merkle_root` (Merkle root of all rows written in the run)
- `signed_attestation` (cryptographic signature by the ingestor's identity key — substrate operators have keys; tenants optionally have keys)

The per-run signed attestation is the substrate's strongest tampering detection: an attacker who modifies a row would have to compute a valid signature over the modified row's parent chain, which requires the original signing key.

### Integrity verification

```sql
SELECT * FROM provenance.verify_integrity($entity_id, $depth);
```

The function:
1. Walks the provenance chain back depth steps.
2. For each step, recomputes the row's content hash from its canonicalized contents and verifies it matches the stored `entity_id`.
3. Recomputes the `provenance_hash` and `parent_chain_hash` and verifies the chain.
4. For ingestion runs in the chain, verifies the `signed_attestation` against the ingestor's public key.
5. Returns a per-step verification result; any failure is reported with diagnostic detail (which step, what mismatched).

A clean verification result certifies that the entity's content and provenance ancestry have not been modified since their respective ingestion runs.

### Limitations

The substrate's cryptographic chain detects unauthorized modification of substrate state. It does NOT:

- Prove the source corpus was authentic (the substrate cannot independently verify that a corpus claiming to be "Princeton WordNet 3.1" actually is).
- Prove the ingestor logic was correct (a buggy ingestor that produces wrong substrate state still produces a valid chain).
- Defend against an attacker with the operator's signing key (key compromise is out of scope; standard key rotation procedures apply).

These are bounded guarantees, but they are real ones — they detect tampering with substrate state after ingestion, which is the in-scope threat for compliance purposes.

## Audit traces

Every substrate operation that has audit relevance — inference traversals, ingestion runs, recipe executions, macro-OODA decisions, tenant operations — emits an `audit_trace` entity. Audit traces have provenance scoped to the operation's tenant (or to substrate-internal scopes for operator/macro-OODA actions).

An audit trace records:
- The operation type (inference, ingestion, etc.).
- The invoking principal (tenant_id, recipe_id, operator_id, etc.).
- Inputs (recipe, parameters, source entities).
- Outputs (entities produced, paths returned, traces written).
- Timestamps (start, end).
- Resource metrics (substrate reads, traversal depth, time elapsed).
- Outcome (success, partial, failed-with-reason).

Audit traces are themselves substrate state. They participate in:
- Provenance chains (an inference's output has provenance pointing to its audit trace).
- Snapshot replay (audit traces are point-in-time records).
- Cryptographic integrity (audit traces inherit the chain commitment scheme).

A query like "show me everything tenant A did in the medical arena on 2026-04-01" is a substrate query joining audit traces by tenant, arena, and date.

## Self-reference: auditing the auditor

The audit chain itself is implemented through substrate primitives, which means the substrate can audit its own auditing. A query like "verify that the audit infrastructure has not been tampered with" runs `provenance.verify_integrity` on the audit-trace entities themselves, which transitively walks back to the substrate's bootstrap commitment.

The bootstrap commitment is the cryptographic root: a signed manifest of the substrate's initial state at deployment, stored in tamper-evident off-substrate storage (operator's HSM or equivalent). Verifying the bootstrap manifest's signature against the operator's root key is the trust anchor.

This self-reference is what the Gödel Engine's name alludes to: the substrate's structural ability to reason about itself, including its own audit history.

## Compliance use cases

### "Who has accessed this content?"

```sql
SELECT * FROM substrate.audit_trace
WHERE atom_id = $atom OR composition_id = $composition
  AND operation_type = 'inference_read'
  AND created_at > now() - interval '90 days'
ORDER BY created_at DESC;
```

Returns every inference that read the specified content in the last 90 days, with tenant, recipe, and trace details.

### "Reproduce this customer-facing decision"

```sql
SELECT * FROM substrate.audit_trace WHERE trace_id = $original_trace;
-- inspect the recipe, inputs, substrate state at trace.created_at

SELECT * FROM inference.replay($original_trace);
-- runs the same recipe against snapshot_at(trace.created_at)
-- output should byte-equivalently match trace.output
```

Reproducibility is determinism + snapshot replay. The replayed output is verifiable byte-for-byte against the original trace's recorded output.

### "Show me the source attribution for this answer"

```sql
SELECT * FROM provenance.audit_chain($answer_composition_id);
```

Returns the full provenance tree from the answer back to originating sources. For natural-language outputs, the recompose pipeline can render the chain inline as citation markers (see `20-technical/13-recomposers.md` for citation rendering).

### "Detect tampering"

```sql
SELECT * FROM provenance.verify_integrity_full($tenant_id, max_depth => 100);
```

Runs integrity verification over a tenant's recent state. Failed verifications surface entities whose hashes don't match — indicating corruption, replication error, or tampering.

## Performance characteristics

| Operation | Typical performance |
|---|---|
| Direct provenance lookup (depth 1) | < 1 ms |
| Audit chain traversal (depth ~10) | 5–50 ms |
| Audit chain traversal (full depth) | Seconds to minutes |
| Snapshot replay (small region) | Subsecond |
| Snapshot replay (full substrate) | Hours; rarely needed |
| Integrity verification (depth 10) | 50–500 ms |
| Integrity verification (full chain) | Minutes |

Most audit-chain queries operate on small depths (the user wants to know the immediate provenance, not the full chain back to the bootstrap manifest). Performance is dominated by the substrate's underlying graph-traversal cost, which the bulk-fetch SPI optimizes.

## What the audit chain is NOT

- **Not optional.** Every substrate write produces audit-relevant state; there is no "audit off" mode.
- **Not lossless natural language.** The audit chain captures structured provenance; rendering the chain as natural-language attribution is a recompose-pipeline concern.
- **Not a substitute for external compliance.** Substrate audit chains certify substrate-internal integrity; external compliance frameworks (SOC2, HIPAA, GDPR) impose additional requirements (organizational controls, access policies, retention schedules) that the substrate supports but does not replace.
- **Not modifiable by tenants.** Tenants can READ their audit traces; they cannot modify or delete them. Audit-trace deletion (in the rare case it's required for legal compliance, e.g., GDPR right-to-erasure) is an operator action with its own audit trail.

## Cross-references

- Substrate Laws (especially Law 13 — fail loud — applied to integrity failures): `10-architecture/01-substrate-laws.md`
- Provenance pillar: `10-architecture/05-provenance.md`
- Multi-tenancy (audit visibility scoping): `10-architecture/16-multi-tenancy.md`
- Macro-OODA (where scheduled audit-integrity sweeps run): `10-architecture/10-godel-engine.md`
- Continuous learning loop (where outcome-event audit trails matter): `10-architecture/18-continuous-learning-loop.md` (forthcoming)

## External references

- BLAKE3 cryptographic hash: <https://github.com/BLAKE3-team/BLAKE3>
- Merkle trees: <https://en.wikipedia.org/wiki/Merkle_tree>
- Append-only log integrity (CONIKS, Certificate Transparency, etc.): <https://research.google/pubs/certificate-transparency/>
