# Audit chain — provenance traversal, snapshot replay, cryptographic integrity

Source: `docs/10-architecture/17-audit-chain.md`.

Substrate's intrinsic, end-to-end traceability mechanism. For any substrate state (atom, composition, edge, inference output), substrate can answer:
1. **Who put this here?** — walk provenance edges back to originating ingestion sources
2. **What did this look like at time T?** — replay substrate state at any past timestamp via append-only logs
3. **Has anything been tampered with?** — verify cryptographic chains over substrate state to detect modification

Audit chain is NOT a separate logging system bolted on. Emergent property of three substrate-level invariants:
1. **Content addressing** — every atom/composition/edge identity is BLAKE3 of canonicalized contents. Any modification produces new identity; original preserved as unmodified entity rather than overwritten in place.
2. **Append-only state** — substrate state grows; atoms not deleted in normal operation (tenant offboarding is rare exception, itself audited).
3. **Provenance edges** — every state element has at least one provenance attribution; provenance is itself substrate state and itself auditable.

Chain is therefore a graph traversal — substrate-internal artifact, not separate logging tier.

## Provenance traversal — "who put this here"

```sql
SELECT * FROM provenance.audit_chain($entity_id, $arena);
```

Function walks edges of provenance-relevant types (`provenance_of`, `derived_from`, `consensus_member`, `recipe_for`, `ingested_via`, etc.) from target entity back through:
1. **Direct provenance** — entity's `provenance` JSONB list, parsed for source class, ingestor, timestamp
2. **Derivation chain** — for compositions/edges produced by recipe or transform: recipe ID and source compositions
3. **Consensus contributors** — for `firefly_consensus` compositions: contributing fireflies and their source models
4. **Ingestion event** — ingestion run that wrote entity, including ingestor version, source corpus URL/hash, ingestion timestamp
5. **Source-level corroboration** — for entities with multiple provenance entries: all corroborating sources

Output is tree (or DAG, for entities with multiple provenance entries) rooted at entity with leaves at originating sources. Tree returned as substrate state — every traversal step is queryable row, so chain can be inspected, summarized, exported, or rendered.

### Partial traversal

For deep chains (derived composition whose source compositions were themselves derived through 12 transformation steps):
```sql
SELECT * FROM provenance.audit_chain($entity_id, $arena, max_depth => 5);
```
Truncated chains explicitly marked with `truncated_at` flag — Substrate Law 13 (fail loud) requires callers know they received partial answer rather than silently given a tree that looks complete.

### Cross-tenant chain visibility

If entity has provenance from multiple tenants (via sharing groups), audit chain's visibility filtered by calling tenant's permissions. Tenant sees own provenance branches AND branches they have rights to see (public seeds, shared groups). Branches outside their visibility are NOT silently dropped — reported as `restricted` placeholders so caller knows there are additional sources they cannot inspect. Deliberate transparency choice; "we have proof but we won't let you see it" is more honest than "this is complete chain (but actually it isn't)."

## Snapshot replay — "what did this look like at T"

Substrate state is append-only at persistence layer. Every atom, composition, edge, rating change recorded with `created_at` timestamp; for entities that supersede prior versions, `supersedes` edges link to predecessor.

```sql
SELECT * FROM substrate.snapshot_at(timestamp '2026-03-15T00:00:00Z');
```

Function returns temporary view (or materializes snapshot composition entity, depending on caller request) showing only:
- Entities whose `created_at` ≤ T and that were not yet superseded by T
- Edges whose `created_at` ≤ T and that were not yet superseded
- Glicko-2 ratings as of most recent rating-update event ≤ T

Computationally non-trivial: full-substrate snapshot at old timestamp may require reading large amounts of historical state. For practical use, typically scoped:
```sql
SELECT * FROM substrate.snapshot_at(timestamp '2026-03-15', arena => 'medical', max_entities => 10000);
```

### What snapshot replay enables

- **Compliance** — tenant subject to "right to inspect" demands can produce substrate state as it was when their query ran
- **Reproducibility** — inference that ran in 2026-03 can be replayed against 2026-03 substrate state, producing SAME output (A* is deterministic; Law 6). Important for audit trail of customer-facing decisions.
- **Forensic analysis** — when inference's output is questioned, snapshot replay reconstructs exact substrate state at time of inference

### What snapshot replay does NOT include

- **Real-time outcome events** — outcome events processed in batches (Glicko updates scheduled). Snapshot at T reflects rating state AS OF most recent batch update ≤ T, NOT events arriving between last batch update and T.
- **Inference traces from after T** — naturally
- **Future state** — trivially

Snapshot replay is point-in-time, deterministic, bounded by substrate's append-only history.

## Cryptographic integrity — "has anything been tampered with"

Content-addressing provides intrinsic integrity guarantee at atom level: atom's `atom_id` is BLAKE3 of its bytes; if bytes change, hash changes; if hash changes, row is logically NEW atom and original is structurally untouched.

But what about edges and compositions whose identity is BLAKE3 of references to other rows? If edge's referenced atom replaced silently, edge would still hash correctly but its references would now point at "the new thing pretending to be the old thing." Detecting requires higher-level chain.

Substrate enforces chain of cryptographic commitments:

### Per-row commitment

Every row carries:
- Its content-addressed `entity_id` (BLAKE3 of canonicalized contents)
- A `provenance_hash` = BLAKE3 of (entity_id || provenance JSONB || created_at)
- A `parent_chain_hash` = BLAKE3 of (provenance_hash || ingestion_run.parent_chain_hash)

Parent chain hash is Merkle-style accumulator: each ingestion run starts with previous run's parent_chain_hash as initial state and produces new parent_chain_hash including every row written in run.

### Per-ingestion-run commitment

Each ingestion run produces `ingestion_run` entity:
- `run_id` (UUID)
- `parent_chain_hash_in` (hash this run started from)
- `parent_chain_hash_out` (hash after this run committed)
- `merkle_root` (Merkle root of all rows written in run)
- `signed_attestation` (cryptographic signature by ingestor's identity key — substrate operators have keys; tenants optionally have keys)

Per-run signed attestation is substrate's strongest tampering detection: attacker who modifies a row would have to compute valid signature over modified row's parent chain, which requires original signing key.

### Integrity verification

```sql
SELECT * FROM provenance.verify_integrity($entity_id, $depth);
```

Function:
1. Walks provenance chain back `depth` steps
2. For each step, recomputes row's content hash from canonicalized contents and verifies it matches stored `entity_id`
3. Recomputes `provenance_hash` and `parent_chain_hash` and verifies the chain
4. For ingestion runs in the chain, verifies `signed_attestation` against ingestor's public key
5. Returns per-step verification result; any failure reported with diagnostic detail (which step, what mismatched)

Clean verification result certifies entity's content and provenance ancestry have not been modified since their respective ingestion runs.

### Limitations

Cryptographic chain detects unauthorized modification of substrate state. Does NOT:
- Prove source corpus was authentic (substrate cannot independently verify that a corpus claiming to be "Princeton WordNet 3.1" actually is)
- Prove ingestor logic was correct (buggy ingestor producing wrong substrate state still produces valid chain)
- Defend against attacker with operator's signing key (key compromise is out of scope; standard key rotation procedures apply)

Bounded but real guarantees — detects tampering with substrate state after ingestion, which is the in-scope threat for compliance.

## Audit traces

Every substrate operation that has audit relevance (inference traversals, ingestion runs, recipe executions, macro-OODA decisions, tenant operations) emits `audit_trace` entity. Audit traces have provenance scoped to operation's tenant (or substrate-internal scopes for operator/macro-OODA actions).

An audit trace records:
- Operation type (inference, ingestion, etc.)
- Invoking principal (tenant_id, recipe_id, operator_id, etc.)
- Inputs (recipe, parameters, source entities)
- Outputs (entities produced, paths returned, traces written)
- Timestamps (start, end)
- Resource metrics (substrate reads, traversal depth, time elapsed)
- Outcome (success, partial, failed-with-reason)

Audit traces are themselves substrate state. Participate in provenance chains (inference's output has provenance pointing to its audit trace), snapshot replay (audit traces are point-in-time records), cryptographic integrity (audit traces inherit chain commitment scheme).

Query like "show me everything tenant A did in the medical arena on 2026-04-01" is a substrate query joining audit traces by tenant, arena, and date.

## Self-reference — auditing the auditor

Audit chain implemented through substrate primitives → substrate can audit its own auditing. Query like "verify that audit infrastructure has not been tampered with" runs `provenance.verify_integrity` on audit-trace entities themselves, which transitively walks back to substrate's bootstrap commitment.

Bootstrap commitment is cryptographic root: signed manifest of substrate's initial state at deployment, stored in tamper-evident off-substrate storage (operator's HSM or equivalent). Verifying bootstrap manifest's signature against operator's root key is the trust anchor.

This self-reference is what Gödel Engine's name alludes to: substrate's structural ability to reason about itself, including its own audit history.

## Compliance use cases

### "Who has accessed this content?"
```sql
SELECT * FROM substrate.audit_trace
WHERE atom_id = $atom OR composition_id = $composition
  AND operation_type = 'inference_read'
  AND created_at > now() - interval '90 days'
ORDER BY created_at DESC;
```

### "Reproduce this customer-facing decision"
```sql
SELECT * FROM substrate.audit_trace WHERE trace_id = $original_trace;
-- inspect the recipe, inputs, substrate state at trace.created_at

SELECT * FROM inference.replay($original_trace);
-- runs same recipe against snapshot_at(trace.created_at)
-- output should byte-equivalently match trace.output
```
Reproducibility = determinism + snapshot replay. Replayed output verifiable byte-for-byte against original trace's recorded output.

### "Show me the source attribution for this answer"
```sql
SELECT * FROM provenance.audit_chain($answer_composition_id);
```
Returns full provenance tree from answer back to originating sources. For natural-language outputs, recompose pipeline can render chain inline as citation markers.

### "Detect tampering"
```sql
SELECT * FROM provenance.verify_integrity_full($tenant_id, max_depth => 100);
```
Runs integrity verification over tenant's recent state. Failed verifications surface entities whose hashes don't match — indicating corruption, replication error, or tampering.

## Performance characteristics

| Operation | Typical performance |
|---|---|
| Direct provenance lookup (depth 1) | <1 ms |
| Audit chain traversal (depth ~10) | 5-50 ms |
| Audit chain traversal (full depth) | Seconds to minutes |
| Snapshot replay (small region) | Subsecond |
| Snapshot replay (full substrate) | Hours; rarely needed |
| Integrity verification (depth 10) | 50-500 ms |
| Integrity verification (full chain) | Minutes |

Most audit-chain queries operate on small depths (user wants immediate provenance, not full chain back to bootstrap manifest). Performance dominated by substrate's underlying graph-traversal cost, which bulk-fetch SPI optimizes.

## What audit chain is NOT

- **NOT optional** — every substrate write produces audit-relevant state; no "audit off" mode
- **NOT lossless natural language** — audit chain captures structured provenance; rendering as natural-language attribution is recompose-pipeline concern
- **NOT substitute for external compliance** — substrate audit chains certify substrate-internal integrity; external compliance frameworks (SOC2, HIPAA, GDPR) impose additional requirements (organizational controls, access policies, retention schedules) that substrate supports but does not replace
- **NOT modifiable by tenants** — tenants can READ audit traces; cannot modify or delete. Audit-trace deletion (rare, only for legal compliance like GDPR right-to-erasure) is operator action with its own audit trail

Cross-references:
- `frame/01-SUBSTRATE-LAWS.md` — Law 13 (fail loud) applied to integrity failures; Law 6 (determinism) enables snapshot replay reproducibility
- `frame/14-MULTI-TENANCY.md` — audit visibility scoping
- `frame/08-GODEL-ENGINE.md` — macro-OODA where scheduled audit-integrity sweeps run
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — outcome events as audit-trail entities
- `frame/02-SUBSTRATE-MODEL.md` — content-addressing invariant the chain rests on
