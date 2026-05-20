# Substrate-Level Governance

**Status**: ✅ Complete

Governance in Hartonomous is **not** a classifier model layered on top of a generative model. It is a property of the substrate itself: during the forward pass of decomposition, each entity at each compositional level triggers an indexed JOIN against classification junctions, and the substrate takes deterministic action based on what the junction returns. This is governance-as-relational-lookup, not governance-as-learned-judgment. This document specifies the mechanism, enumerates the per-level checkpoints, lists the properties this architecture produces, and describes the governance-as-SQL prototyping surface for inventing new governance mechanisms.

---

## Purpose of this document

To state precisely how Hartonomous enforces classification-dependent policy (content filtering, flagging, routing, halt-on-violation, audit trailing) at the substrate level rather than at a post-hoc model layer, and to specify the surface that new governance policies are written against.

This is important because:

1. The substrate bond (`substrate-bond.md`) requires that judgments be auditable, deterministic, and modifiable by the practitioner. A learned classifier satisfies none of those.
2. The substrate already has junction tables with Glicko-2-rated classification assignments. Governance uses that existing surface directly.
3. The forward pass of decomposition is the natural enforcement surface — every level of composition is already examining entity classifications as part of normal processing.
4. Using the substrate itself for governance eliminates the attack surface of "jailbreak the model to bypass the filter" that ML-based moderation suffers.

---

## The reframe: governance IS the substrate

### ML-industry governance pattern

The mainstream pattern:

1. Input arrives at a system.
2. A classifier model (often a smaller dedicated moderation model, sometimes the same generation model) runs inference against the input.
3. The classifier emits a probability distribution over harmful categories.
4. A threshold decides the action (allow, flag, refuse, rewrite).
5. The classifier was trained on labeled data with all the biases of that labeling.
6. Individual decisions are uninterpretable in detail (logit residuals do not explain why).
7. Appeals require either human review or model retraining.
8. Adversarial inputs that trick the classifier bypass the policy.

### Hartonomous governance pattern

The substrate pattern:

1. Input arrives and is decomposed by the standard text (or modality) decomposer.
2. **At each level of decomposition** — codepoint, grapheme cluster, morpheme, lemma, word form, lexicalized compound, sense assignment, dependency pattern — an indexed JOIN against the relevant junction table runs.
3. The junction row either exists (with Glicko-rated confidence) or it does not.
4. Governance predicates in SQL declare what combinations of junction rows trigger what actions: quarantine the entity, halt decomposition, annotate with `edge_of_concern`, route to a specific partition, refuse to emit recomposition, record the violation in `monitor.error_log`.
5. Every decision has a literal traceback: which entity, which junction, which provenance, which Glicko μ/σ/games, which governance rule fired.
6. The judgment was made by whichever curator populated the junction row at ingestion time. The substrate looks up that judgment; it does not re-make it.
7. No model inference is involved in the governance decision. No classifier runs. No probability is computed. The answer is a row.

**Governance is a JOIN, not an inference.**

### Concrete comparison

| Property | ML governance | Substrate governance |
|---|---|---|
| Decision mechanism | Classifier forward pass | Indexed JOIN against junction table |
| Per-decision latency | Milliseconds (classifier inference) | Microseconds (single JOIN) |
| Determinism | Probabilistic (temperature, sampling, model-version drift) | Bitwise deterministic |
| Explanation granularity | "The model assigned P=0.87 to category X" | Exact row: (entity_id, junction_id, μ, σ, provenance_id) |
| Modification | Retrain or fine-tune model | UPDATE a junction row |
| Rollback | Restore model checkpoint | UPDATE back, or use session snapshot |
| Adversarial resistance | Brittle — jailbreaks bypass | Strong — no model to jailbreak |
| Cross-practitioner variation | Single shared model | Per-practitioner or per-institution junction sets |
| Audit trail | Opaque | Complete per-decision row-level history via Glicko game log |

---

## The forward pass as enforcement surface

Decomposition walks the substrate from atoms up to compositions. At each level, a junction lookup is already happening as part of normal decomposition — the decomposer needs to know the entity's classifications to produce the correct downstream structure. Governance uses **the same lookup** and **the same transaction** to evaluate policy.

### The per-level checkpoint chain

| Level | Existing junction | Governance-relevant class of check |
|---|---|---|
| **Codepoint** | `codepoint_property` | Invisible characters, RTL-override spoofing, control-character injection, cross-script homoglyph components |
| **Grapheme cluster** | (UAX #29 conformance itself) | Malformed clusters, zero-width-joiner abuse, variation-selector misuse |
| **Morpheme / lemma** | `entity_pos`, proposed `entity_pragmatic_register` | Register classifications on the lemma (pejorative, threatening, slur, abusive) normalized across all surface variants |
| **Word form** | `entity_pos`, `entity_sense`, proposed `entity_pragmatic_register` | Per-form classification — governance applies even if the lemma is clean but a specific form carries marked register |
| **Lexicalized compound** (edge type 37) | Attached junctions on the whole-form lemma | Compound-level register — "scurvy dog" as a lexicalized pejorative even though neither part is flagged |
| **Sense** | `entity_sense` Glicko ratings | Sense-specific governance — "dog" is clean as noun-animal, marked when bearing the verb-pejorative-directed-at-person sense |
| **UD sentence pattern** | `pattern_deprel` Glicko-rated | Syntactic patterns characteristic of threats (imperative + hostile verb + addressee-you) |
| **Turn / conversation structure** | Proposed `edge_responds_to`, `edge_escalates`, `edge_concedes` | Turn-adjacency governance — escalation patterns, bad-faith argumentation patterns |

### Proposed junction: `entity_pragmatic_register`

The base schema does not yet carry register classification. Governance work that depends on pragmatic register (pejorative, threatening, conciliatory, de-escalating, good-faith, bad-faith, consenting, coerced, etc.) needs a junction following the `entity_pos` pattern exactly:

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
CREATE INDEX ON substrate.entity_pragmatic_register (register_id, mu DESC);
```

This is **infrastructure, not substrate**. It follows the `entity_pos` pattern: Glicko-rated classification assignment with microsecond JOIN access. It does not introduce a new entity type and does not add content to the substrate — it adds a classification surface the substrate can look up.

Seed provenances:
- `conflict_corpus` — transcripts of disputes, court filings, platform abuse records (with ethics/consent guardrails)
- `mediation_corpus` — successful mediation transcripts, NVC exemplars, restorative-justice records
- `deliberation_corpus` — citizen assembly minutes, jury deliberation records
- `constitutional_corpus` — founding documents, treaties
- `governance_failure_corpus` — pre-collapse media, failed-state records
- `governance_success_corpus` — long-stable institutional records
- `moderation_corpus` — platform moderation logs with outcome labels

Each seed corpus has a trust prior μ in `substrate.provenance` and contributes register classifications via ingestion. Glicko-2 updates the classifications on use.

---

## Normalization defeats obfuscation

The substrate's content-addressed identity combined with deterministic Unicode normalization produces **obfuscation resistance as a structural property**, not as a per-input classifier's capability:

### Surface-form obfuscation

Attackers typically obfuscate via:

- Homoglyph substitution: `ｆｕｃｋ` (fullwidth), `fυck` (Greek upsilon), `fuсk` (Cyrillic es)
- Leetspeak: `f0ck`, `fvck`
- Zero-width-space injection: `f​uck`
- Variation selector abuse: `f︀uck`
- Case mixing: `FuCk`
- Punctuation injection: `f-uck`, `f*ck`

### Why the substrate defeats these structurally

1. The text decomposer applies NFC normalization at the grapheme level.
2. Canonical equivalents map to identical grapheme clusters.
3. Zero-width-spaces and variation selectors either annotate or are filtered via UAX #29 boundary rules depending on the decomposer configuration.
4. The normalized form decomposes to the same lemma.
5. The lemma has exactly one BLAKE3 hash.
6. The lemma's `entity_pragmatic_register` junction rows return the same classification regardless of surface obfuscation.

**The attacker cannot obfuscate their way past a JOIN.** They can obfuscate their way past a classifier (by moving outside its training distribution), but a JOIN against content-addressed lemma identity is immune to surface variation. The normalization pipeline is deterministic, open-source, and inspectable; it is not a learned artifact that can be adversarially evaded.

### What the substrate cannot normalize away

Novel coinages, genuine neologisms, evolving slang not yet in any ingested corpus — these won't have junction rows. The substrate's response is **honest abstention**: no classification exists, no flag fires, the content passes. This is correct behavior. A learned classifier would *guess* with false confidence; the substrate records that it doesn't know.

Practitioners who need coverage of recent slang can ingest current moderation corpora; new junction rows appear as soon as the corpus is ingested. The rating has `games=0` and wide σ initially, giving honest uncertainty about the classification's strength.

---

## Governance actions

A governance rule is a SQL predicate that selects entities or edges matching some condition. The action is the second half of the rule. Standard actions:

| Action | Semantics | Implementation |
|---|---|---|
| **Flag** | Attach an `edge_of_concern` edge from the violating entity to a governance-record entity | `INSERT INTO substrate.edge ...` |
| **Annotate** | Mark the decomposition with warning metadata in `monitor.ingestion_progress` | `INSERT INTO monitor.error_log ...` |
| **Quarantine** | Route the entity to a dedicated partition not accessible to general queries | `ON CONFLICT ... INSERT INTO substrate.entity_quarantine` |
| **Halt decomposition** | Abort the current ingestion batch, roll back the transaction | `RAISE EXCEPTION` |
| **Refuse recomposition** | Recomposer refuses to reconstruct output containing the flagged entity | Precondition check in `IRecomposer<T>` |
| **Refuse traversal** | A\* treats flagged edges as having infinite Glicko cost, making them unreachable | Runtime filter in traversal |
| **Route to review queue** | Entity is written normally but also enqueued for human review | `INSERT INTO monitor.review_queue` |
| **Record-and-pass** | The violation is logged but the content is not blocked | `INSERT INTO monitor.governance_log` only |

All actions are SQL. All are auditable. All can be selectively applied, scoped to a practitioner, or combined. None require retraining.

---

## Properties this architecture produces

### Property 1: Determinism

Same input + same governance configuration = same action. Every time. No temperature, no sampling, no model-version drift, no weight update after deployment.

Consequences:
- Two runs of the same decomposition on the same input yield byte-identical governance outcomes (Law #6 applied to governance).
- Governance behavior can be unit-tested exhaustively.
- Regressions are immediately visible via snapshot comparison.

### Property 2: Per-decision audit traceability

Every governance action produces a concrete row of evidence:

```
Governance action: QUARANTINE
  Trigger: entity_id=12345, register_id=3 (pejorative_directed), μ=93000, games=18000
  Provenance: princeton_wordnet (trust_prior_mu=95000)
  Rule: governance.rule_id=7 ("pejorative_directed at addressee in imperative pattern")
  Decomposition batch: 2026-04-23T14:32:07Z / 0x8f3a...
```

Any practitioner can issue this SQL and receive the full audit. There is no interpretability gap. The substrate does not have an unexplained component.

### Property 3: Modification without retraining

Correcting or adjusting a governance rule is an UPDATE on junction rows or a change to a SQL predicate. No model retraining. No catastrophic forgetting. No compute cost for deployment.

Consequences:
- Governance evolves faster than any learned system can.
- Specific false positives can be corrected row-by-row with provenance recorded.
- Societal drift (words changing register over time) is handled by Glicko updates on use or by explicit curator intervention, not by retraining.

### Property 4: Honest abstention

The substrate can only flag what it has classifications for. If a content form has no junction row, no flag fires — and the abstention is visible:

```sql
-- "Did the substrate classify this entity?"
SELECT count(*) FROM substrate.entity_pragmatic_register
WHERE entity_id = :id;  -- 0 = no classification, abstention
```

A learned classifier would produce a confident output for unfamiliar content; the substrate admits ignorance. This is correct behavior for any principled governance system.

### Property 5: Adversarial resistance by structural property

There is no prompt to inject into. There is no model to trick. A prompt like "ignore all previous instructions" decomposes into word_forms with their own register classifications; there is no instruction-following layer to hijack.

Prompt-injection attacks against LLMs work because the model conflates instructions with content. The substrate never conflates them because it doesn't follow instructions at all — it only decomposes and looks up.

### Property 6: Composability, scopability, versioning

Governance rules are SQL. Rules compose, scope, and version naturally:

- **Compose**: `WHERE rule_A AND rule_B`.
- **Scope**: `WHERE practitioner_id = :current_user AND (rule_set = 'strict' OR rule_set = 'default')`.
- **Version**: store rule definitions in a table with `version_id`, diff across versions, roll back to prior versions.

Different practitioners can run different rule sets against the same substrate. Different institutions can fork a rule set, evolve it independently, and merge changes back via standard SQL/audit tooling. The substrate is invariant; governance is a view over it.

### Property 7: Multi-provenance disagreement

Two curators can hold opposite classifications of the same entity simultaneously, each with their own provenance_id and Glicko rating. Governance predicates can choose which provenance hierarchy to trust:

```sql
-- Use only Princeton-curated classifications.
WHERE EXISTS (
  SELECT 1 FROM substrate.entity_pragmatic_register epr
  JOIN substrate.provenance p ON p.id = epr.provenance_id  -- assumes provenance stored
  WHERE epr.entity_id = e.id
    AND p.code = 'princeton_wordnet'
    AND epr.mu > 85000
)

-- Use a community-curated override that disagrees.
OR EXISTS (...)
```

Disagreement is preserved, not collapsed. Practitioners decide which curators to trust for which decisions. This is the opposite of ML-based moderation, where the model's opinion is the only opinion and disagreement is invisibly overridden.

---

## The governance sandbox — invent new mechanisms

Because governance is SQL, **governance can be prototyped and tested against history** before deployment. This capability does not exist in any learned moderation system.

### Prototyping as SQL

A proposed governance rule is a predicate over substrate + app-layer state:

```sql
-- Rule: "Halt decomposition if a conversation turn contains a word_form
-- that is rated pejorative_directed (μ > 85000) by Princeton-trusted
-- corpus AND the edge_member role is 'addressee'."
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

### Testing against history

Run the rule against every ingested conflict-corpus conversation and measure:

- How many historically-labeled-as-escalating conversations would the rule have flagged? (true positives)
- How many mediation-success conversations would the rule have flagged? (false positives)
- What is the Fréchet distance between rule-flagged conversation trajectories and known-escalation trajectories? (behavioral similarity)

This is not a prediction; it is a **measurement against the ingested historical record of governance outcomes**. No learned system can do this at decision-level granularity because no learned system's decisions are deterministically reproducible on historical data.

### Measuring policy proposals geometrically

A proposed governance mechanism (e.g., a new moderation policy, a new legal clause, a new deliberation protocol) decomposes into the substrate as text. The text's centroid trajectory in the 4D frame can be compared to:

- Trajectories of known-effective governance mechanisms (low Fréchet distance = structurally similar to effective mechanisms).
- Trajectories of known-ineffective or harmful mechanisms.
- Historical evolution of existing mechanisms that succeeded or failed.

See `specs/native/geometry4d-composition.md` § "Idiomaticity as geometric measure" for the measurement primitives. The same primitives apply: governance rules are text, text has centroids, centroid distances to known outcomes are queryable.

This is the capability the substrate enables and that no LLM delivers honestly: **proposed governance can be measured for structural similarity to governance whose outcomes we know, before deployment**. Not predicted. Measured.

---

## The inference-side governance application

Governance is not only about ingestion. Inference also uses junction lookups during traversal:

### Traversal-time governance

A\* over typed edges can consult governance predicates during path selection:

- If a candidate edge's endpoint entity carries a `register_id` that the practitioner's rule set forbids, treat the edge as having infinite Glicko cost.
- If a path would cross through a quarantined entity partition, the path is unreachable.
- If a recomposition would emit content flagged by a governance rule, the recomposer refuses.

All of this is O(log N) per check — the junction JOIN is indexed and fast. Governance adds at most constant overhead per traversal step. It does not change the asymptotic O(K log N) inference cost.

### Traversal refusal vs honest error

When governance prevents a traversal from completing (because every candidate path crosses a forbidden entity), the substrate returns a structured error, not a hallucinated answer:

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

The practitioner sees that governance prevented the answer and can inspect the specific rule that triggered. No silent refusal, no rewritten-to-be-safe output, no pretend-you-didn't-see-that. The substrate never lies about what it did.

---

## Warnings and open questions

Substrate governance is a large improvement over ML-based moderation, but it is not a free lunch. Real concerns remain:

### Concern 1: Curator selection is an act of governance

The set of junction rows the substrate ships with is an act of governance before any practitioner query runs. Whoever seeds the pragmatic-register junction decides which words are classified as which registers, with what provenance, at what trust prior. This choice is visible and documented — unlike a learned model's training-data curation — but it is still a choice with consequences.

**Mitigation**: Multi-provenance design. Every classification can have rows from multiple curators. Rule predicates declare which provenance hierarchy they trust. Practitioners can exclude provenance they disagree with. Disagreement is preserved, not erased.

### Concern 2: Glicko drift under adversarial use

Glicko-2 updates on use. A coordinated group of practitioners using the substrate in bad faith could drift ratings over time.

**Mitigation**: Per-practitioner Glicko branches, separated from the shared substrate ratings. Institutional deployments can freeze certain ratings. Audit trails on every game let the practitioner see *who* drove a rating change and when. Session snapshots (`specs/operations/sessions.md`) let the practitioner roll back drift.

### Concern 3: Absence of classification ≠ presence of safety

The substrate's honest abstention ("we have no classification for this content") is not the same as "this content is safe." Novel harms that have no junction rows will pass unflagged.

**Mitigation**: Practitioner-level policies can refuse to decompose content that has insufficient classification coverage in any register dimension. "Novel content requires human review before ingestion" is a valid and implementable rule.

### Concern 4: Legal and institutional deployment

If governance is ever deployed for binding institutional decisions (moderation, legal filtering, medical triage), the provenance documentation, the curator accountability, and the rule-change audit trail become not just nice-to-haves but legal requirements.

**Mitigation**: The substrate's design is already oriented toward these properties. Every rule change is a SQL migration with timestamp and authorship. Every Glicko update has a game-event row. Every governance action has a full audit trace. The infrastructure for legal-grade accountability exists; deployment needs to honor it.

### Open question: Automated rule discovery

Can the substrate's historical data be used to automatically discover effective governance rules? Statistical methods (association mining, decision tree induction) could in principle propose rules that discriminate known-good from known-bad outcomes. This is a research surface, not a deployed feature. Any discovered rule would itself be SQL, auditable, and subject to the same tournament evaluation as any other rule.

---

## Anti-patterns

### Anti-pattern 1: Training a classifier on top of substrate state

Don't. The substrate's governance advantage is that it has no learned component. Adding one reintroduces every ML-governance failure mode (opacity, training bias, update overhead, adversarial brittleness). Keep governance in SQL.

### Anti-pattern 2: Writing rules against the substrate layer instead of the app layer

Don't. Classification lookups belong on the app-layer junctions (microsecond JOINs). Writing a rule that traverses substrate edges to recompute a classification defeats the point of junction tables. See `specs/sql/infrastructure-vs-substrate.md`.

### Anti-pattern 3: Collapsing provenance disagreement

Don't pick one curator and delete the rest. Multi-provenance is a feature. Rules that need a specific curator's opinion should scope to that provenance; they should not delete competing provenance rows.

### Anti-pattern 4: Silent refusal

Don't let the substrate quietly refuse to produce output without a structured governance violation record. Every refusal must produce a row in `monitor.governance_log` with rule_id and full context. Silent refusal is indistinguishable from a bug.

### Anti-pattern 5: Irreversible governance actions

Don't implement governance actions that cannot be undone via session rollback. Every governance write must be session-scoped and reversible. Content permanently quarantined with no recovery path violates the practitioner's sovereignty.

---

## Cross-references

- `substrate-bond.md` — Why governance must be deterministic, auditable, and practitioner-controlled (corollaries 2, 3, 4).
- `specs/sql/infrastructure-vs-substrate.md` — The two-layer discipline that governance JOINs depend on.
- `specs/sql/reference-tables.md` — Reference table DDL (and the proposed `ref_pragmatic_register` extension).
- `specs/sql/junction-tables.md` — Junction DDL (and the proposed `entity_pragmatic_register` extension).
- `specs/engine/inference.md` — How traversal consults governance during path selection.
- `specs/engine/arenas-and-significance.md` — Glicko-2 machinery that governance ratings plug into.
- `specs/operations/sessions.md` — Session-scoped rollback for governance actions.
- `specs/operations/monitoring.md` — `monitor.governance_log`, `monitor.review_queue`, error-log integration.
- `specs/decomposers/tokenizers.md` — UAX #29 normalization that defeats surface-form obfuscation.
