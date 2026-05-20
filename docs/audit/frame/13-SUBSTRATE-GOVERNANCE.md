# Substrate-level governance — relational refusal

Source: `docs/specs/engine/substrate-governance.md`.

Governance in Hartonomous is **not** a classifier model layered on top of a generative model. It is a property of the substrate itself: during forward pass of decomposition, each entity at each compositional level triggers indexed JOIN against classification junctions, and substrate takes deterministic action based on what junction returns.

**Governance is a JOIN, not an inference.**

## ML governance vs substrate governance

| Property | ML governance | Substrate governance |
|---|---|---|
| Decision mechanism | Classifier forward pass | Indexed JOIN against junction table |
| Per-decision latency | Milliseconds (classifier inference) | Microseconds (single JOIN) |
| Determinism | Probabilistic (temperature, sampling, model-version drift) | Bitwise deterministic |
| Explanation granularity | "Model assigned P=0.87 to category X" | Exact row: (entity_id, junction_id, μ, σ, provenance_id) |
| Modification | Retrain or fine-tune model | UPDATE a junction row |
| Rollback | Restore model checkpoint | UPDATE back, or use session snapshot |
| Adversarial resistance | Brittle — jailbreaks bypass | Strong — no model to jailbreak |
| Cross-practitioner variation | Single shared model | Per-practitioner or per-institution junction sets |
| Audit trail | Opaque | Complete per-decision row-level history via Glicko game log |

## Per-level checkpoint chain

Decomposition walks substrate from atoms up to compositions. At each level, junction lookup already happening as normal decomposition. Governance uses **same lookup + same transaction** to evaluate policy.

| Level | Existing junction | Governance check class |
|---|---|---|
| **Codepoint** | `codepoint_property` | Invisible characters, RTL-override spoofing, control-character injection, cross-script homoglyph components |
| **Grapheme cluster** | (UAX #29 conformance) | Malformed clusters, zero-width-joiner abuse, variation-selector misuse |
| **Morpheme / lemma** | `entity_pos`, proposed `entity_pragmatic_register` | Register classifications on lemma (pejorative, threatening, slur, abusive) normalized across all surface variants |
| **Word form** | `entity_pos`, `entity_sense`, `entity_pragmatic_register` | Per-form classification — applies even if lemma is clean but specific form carries marked register |
| **Lexicalized compound** | Attached junctions on whole-form lemma | Compound-level register — "scurvy dog" as lexicalized pejorative even though neither part flagged |
| **Sense** | `entity_sense` Glicko ratings | Sense-specific governance — "dog" is clean as noun-animal, marked when bearing verb-pejorative-directed-at-person sense |
| **UD sentence pattern** | `pattern_deprel` Glicko-rated | Syntactic patterns characteristic of threats (imperative + hostile verb + addressee-you) |
| **Turn / conversation structure** | Proposed `edge_responds_to`, `edge_escalates`, `edge_concedes` | Turn-adjacency governance — escalation patterns, bad-faith argumentation patterns |

## Proposed `entity_pragmatic_register` junction

Follows `entity_pos` pattern exactly: Glicko-rated classification with microsecond JOIN access. Infrastructure, not substrate — adds classification surface substrate can look up. Vocabulary: pejorative / threatening / conciliatory / de-escalating / good-faith / bad-faith / consenting / coerced / etc.

```sql
CREATE TABLE substrate.ref_pragmatic_register (
    id      SMALLINT PRIMARY KEY,
    code    TEXT UNIQUE NOT NULL,
    description TEXT
);

CREATE TABLE substrate.entity_pragmatic_register (
    entity_id   BIGINT NOT NULL,
    register_id SMALLINT NOT NULL REFERENCES substrate.ref_pragmatic_register(id),
    mu          FLOAT8 DEFAULT 1500.0,
    sigma       FLOAT8 DEFAULT 350.0,
    volatility  FLOAT8 DEFAULT 0.06,
    games       INT DEFAULT 0,
    PRIMARY KEY (entity_id, register_id)
);
```

## Seed provenances for governance

7 corpus classes:
- `conflict_corpus` — transcripts of disputes, court filings, platform abuse records (with ethics/consent guardrails)
- `mediation_corpus` — successful mediation transcripts, NVC exemplars, restorative-justice records
- `deliberation_corpus` — citizen assembly minutes, jury deliberation records
- `constitutional_corpus` — founding documents, treaties
- `governance_failure_corpus` — pre-collapse media, failed-state records
- `governance_success_corpus` — long-stable institutional records
- `moderation_corpus` — platform moderation logs with outcome labels

Each has trust prior μ in `substrate.provenance`. Glicko-2 updates classifications on use.

## Normalization defeats obfuscation structurally

Attackers obfuscate via: homoglyph substitution (`ｆｕｃｋ` fullwidth, `fυck` Greek upsilon, `fuсk` Cyrillic), leetspeak (`f0ck`), zero-width-space injection, variation selector abuse, case mixing, punctuation injection (`f-uck`).

Substrate defeats structurally:
1. Text decomposer applies NFC normalization at grapheme level
2. Canonical equivalents map to identical grapheme clusters
3. Zero-width-spaces and variation selectors either annotate or filtered via UAX #29
4. Normalized form decomposes to same lemma
5. Lemma has exactly one BLAKE3 hash
6. Lemma's `entity_pragmatic_register` junction returns same classification regardless of surface obfuscation

**Attacker cannot obfuscate past a JOIN.** Can obfuscate past a classifier (by moving outside training distribution), but JOIN against content-addressed lemma identity is immune to surface variation. Normalization pipeline is deterministic, open-source, inspectable; not a learned artifact that can be adversarially evaded.

## What substrate cannot normalize away

Novel coinages, genuine neologisms, evolving slang not yet in any ingested corpus → no junction rows. Substrate response: **honest abstention** — no classification exists, no flag fires, content passes. This is correct behavior. A learned classifier would *guess* with false confidence; substrate records that it doesn't know.

Practitioners needing coverage of recent slang can ingest current moderation corpora; new junction rows appear as soon as corpus ingested. Rating has `games=0` and wide σ initially, giving honest uncertainty about classification strength.

## 8 governance actions

| Action | Semantics | Implementation |
|---|---|---|
| **Flag** | Attach `edge_of_concern` from violating entity to governance-record entity | `INSERT INTO substrate.edge ...` |
| **Annotate** | Mark decomposition with warning metadata | `INSERT INTO monitor.error_log ...` |
| **Quarantine** | Route entity to dedicated partition not accessible to general queries | `ON CONFLICT ... INSERT INTO substrate.entity_quarantine` |
| **Halt decomposition** | Abort current ingestion batch, roll back transaction | `RAISE EXCEPTION` |
| **Refuse recomposition** | Recomposer refuses to reconstruct output containing flagged entity | Precondition check in `IRecomposer<T>` |
| **Refuse traversal** | A* treats flagged edges as infinite Glicko cost, making them unreachable | Runtime filter in traversal |
| **Route to review queue** | Entity written normally but also enqueued for human review | `INSERT INTO monitor.review_queue` |
| **Record-and-pass** | Violation logged but content not blocked | `INSERT INTO monitor.governance_log` only |

All actions are SQL. All auditable. All can be selectively applied, scoped to a practitioner, or combined. None require retraining.

## 7 properties this architecture produces

1. **Determinism** — same input + same governance config = same action every time (Law 6 applied to governance). Two runs of same decomposition on same input yield byte-identical governance outcomes. Governance behavior can be unit-tested exhaustively. Regressions immediately visible via snapshot comparison.

2. **Per-decision audit traceability** — every governance action produces concrete row of evidence: `Governance action: QUARANTINE; Trigger: entity_id=12345, register_id=3 (pejorative_directed), μ=93000, games=18000; Provenance: princeton_wordnet (trust_prior_mu=95000); Rule: governance.rule_id=7 ("pejorative_directed at addressee in imperative pattern"); Decomposition batch: 2026-04-23T14:32:07Z / 0x8f3a...`. No interpretability gap. Substrate does not have an unexplained component.

3. **Modification without retraining** — correcting/adjusting a governance rule is UPDATE on junction rows or change to SQL predicate. No model retraining. No catastrophic forgetting. No compute cost for deployment. Governance evolves faster than any learned system can. Societal drift (words changing register over time) handled by Glicko updates on use or explicit curator intervention.

4. **Honest abstention** — substrate can only flag what it has classifications for. If content form has no junction row, no flag fires; abstention is visible (`SELECT count(*) FROM substrate.entity_pragmatic_register WHERE entity_id = :id` → 0 = no classification, abstention). A learned classifier produces confident output for unfamiliar content; substrate admits ignorance. Correct behavior for any principled governance system.

5. **Adversarial resistance by structural property** — no prompt to inject into. No model to trick. "Ignore all previous instructions" decomposes into word_forms with their own register classifications; no instruction-following layer to hijack. Prompt-injection attacks against LLMs work because model conflates instructions with content. Substrate never conflates them — doesn't follow instructions at all, only decomposes and looks up.

6. **Composability, scopability, versioning** — rules are SQL. Compose (`WHERE rule_A AND rule_B`); scope (`WHERE practitioner_id = :current_user AND (rule_set = 'strict' OR rule_set = 'default')`); version (store rule definitions in table with version_id, diff across versions, roll back to prior). Different practitioners can run different rule sets against same substrate. Different institutions can fork a rule set, evolve independently, merge changes back via standard SQL/audit tooling.

7. **Multi-provenance disagreement preserved** — two curators can hold opposite classifications of same entity simultaneously, each with own provenance_id and Glicko rating. Governance predicates can choose which provenance hierarchy to trust. Disagreement preserved, NOT collapsed. Opposite of ML-based moderation where model's opinion is the only opinion and disagreement is invisibly overridden.

## Governance sandbox — invent new mechanisms

Because governance is SQL, **governance can be prototyped and tested against history** before deployment. Capability does not exist in any learned moderation system.

Proposed governance rule = predicate over substrate + app-layer state:
```sql
-- "Halt decomposition if a conversation turn contains a word_form
-- rated pejorative_directed (μ > 85000) by Princeton-trusted corpus
-- AND the edge_member role is 'addressee'."
WITH violating_entities AS (
  SELECT m.entity_id, t.id AS turn_id
  FROM substrate.edge_member m
  JOIN substrate.edge t ON t.id = m.edge_id
  JOIN substrate.entity_pragmatic_register epr ON epr.entity_id = m.entity_id
  JOIN substrate.ref_pragmatic_register r ON r.id = epr.register_id
  WHERE r.code = 'pejorative_directed'
    AND epr.mu > 85000
    AND m.edge_role_id = (SELECT id FROM substrate.edge_role WHERE code = 'addressee')
    AND t.edge_type_id = (SELECT id FROM substrate.edge_type WHERE code = 'conversation_turn')
)
SELECT * FROM violating_entities;
```

**Testing against history**: run rule against every ingested conflict-corpus conversation and measure:
- True positives (historically-labeled-as-escalating conversations rule would have flagged)
- False positives (mediation-success conversations rule would have flagged)
- Fréchet distance between rule-flagged conversation trajectories and known-escalation trajectories (behavioral similarity)

This is not prediction; it is **measurement against ingested historical record of governance outcomes**. No learned system can do this at decision-level granularity because no learned system's decisions are deterministically reproducible on historical data.

**Measuring policy proposals geometrically**: proposed governance mechanism (new moderation policy, new legal clause, new deliberation protocol) decomposes into substrate as text. Text's centroid trajectory in 4D frame compared to:
- Trajectories of known-effective governance mechanisms (low Fréchet = structurally similar to effective)
- Trajectories of known-ineffective or harmful mechanisms
- Historical evolution of existing mechanisms that succeeded or failed

**Proposed governance can be measured for structural similarity to governance whose outcomes we know, before deployment.** Not predicted. Measured.

## Inference-side governance application

A* over typed edges consults governance predicates during path selection:
- Candidate edge's endpoint entity carries register_id forbidden by practitioner's rule set → edge treated as infinite Glicko cost
- Path would cross through quarantined entity partition → unreachable
- Recomposition would emit content flagged by governance → recomposer refuses

All O(log N) per check — junction JOIN is indexed and fast. Governance adds at most constant overhead per traversal step. Does not change asymptotic O(K log N) inference cost.

**Structured refusal response** when governance prevents traversal:
```
InferenceResult {
  SeedEntityIds: [...]
  Paths: []
  NodesVisited: 12543
  Elapsed: 00:00:00.087
  GovernanceViolations: [
    { rule_id: 7, description: "...", blocked_entity: 12345 }
  ]
}
```

Practitioner sees governance prevented answer and can inspect specific rule that triggered. No silent refusal, no rewritten-to-be-safe output, no pretend-you-didn't-see-that. Substrate never lies about what it did.

## 4 concerns and mitigations

1. **Curator selection is itself act of governance.** Mitigation: multi-provenance design — every classification can have rows from multiple curators. Rule predicates declare which provenance hierarchy they trust. Practitioners can exclude provenance they disagree with. Disagreement preserved, not erased.

2. **Glicko drift under adversarial use.** Mitigation: per-practitioner Glicko branches separated from shared substrate ratings. Institutional deployments can freeze certain ratings. Audit trails on every game let practitioner see *who* drove rating change and when. Session snapshots roll back drift.

3. **Absence of classification ≠ presence of safety.** Novel harms without junction rows pass unflagged. Mitigation: practitioner-level policies can refuse to decompose content with insufficient classification coverage in any register dimension. "Novel content requires human review before ingestion" is valid implementable rule.

4. **Legal/institutional deployment.** If governance ever deployed for binding institutional decisions, provenance documentation + curator accountability + rule-change audit trail become legal requirements. Substrate design already oriented toward these properties.

Open question: automated rule discovery. Statistical methods (association mining, decision tree induction) could in principle propose rules discriminating known-good from known-bad outcomes. Any discovered rule would itself be SQL, auditable, subject to same tournament evaluation as any other rule.

## 5 governance-specific anti-patterns

- **AP-gov-1**: Training a classifier on top of substrate state. Reintroduces every ML-governance failure mode (opacity, training bias, update overhead, adversarial brittleness). Keep governance in SQL.
- **AP-gov-2**: Writing rules against substrate layer instead of app layer. Classification lookups belong on app-layer junctions (microsecond JOINs). Writing rule that traverses substrate edges to recompute classification defeats junction-table point.
- **AP-gov-3**: Collapsing provenance disagreement. Don't pick one curator and delete the rest. Multi-provenance is a feature.
- **AP-gov-4**: Silent refusal. Every refusal must produce row in `monitor.governance_log` with rule_id and full context. Silent refusal indistinguishable from a bug.
- **AP-gov-5**: Irreversible governance actions. Every governance write must be session-scoped and reversible. Content permanently quarantined with no recovery path violates practitioner sovereignty.

Cross-references:
- `frame/00-FOUNDATIONAL.md` — practitioner sovereignty (Substrate Property 4: practitioner-controlled)
- `frame/07-INFERENCE-ENGINE.md` — traversal-time governance consultation
- `frame/15-AUDIT-CHAIN.md` — governance violation logging via audit traces
- `frame/14-MULTI-TENANCY.md` — per-tenant rule sets
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — Glicko-2 dynamics governance ratings plug into
