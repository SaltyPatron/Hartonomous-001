# Recipe 16: Add a Governance Rule

Intent: add a governance rule that triggers an action during decomposition or inference based on substrate state. Rules are SQL predicates over junction tables; actions are deterministic and audited.

Governance is **not** a classifier. See `docs/specs/engine/substrate-governance.md`.

---

## Prerequisites

- A clear policy statement in plain language ("flag any sentence whose lemma is registered as `pejorative_directed` with μ > 85000").
- The required junction table exists (recipe `05-add-junction-table.md`).
- The required reference values exist in the relevant reference table (recipe `06-add-reference-table.md`).
- The action exists in `substrate.ref_governance_action` (or add via recipe `06`).

---

## Steps

### 1. Capture the rule definition in a table

Governance rules live in `substrate.governance_rule`. Schema:

```sql
-- sql/schema/reference/governance_rule.sql
CREATE TABLE substrate.governance_rule (
    id              INT PRIMARY KEY,
    code            TEXT UNIQUE NOT NULL,
    description     TEXT NOT NULL,
    predicate_sql   TEXT NOT NULL,            -- The SQL WHERE clause body.
    action_id       SMALLINT NOT NULL REFERENCES substrate.ref_governance_action(id),
    enabled         BOOLEAN NOT NULL DEFAULT true,
    severity        SMALLINT NOT NULL DEFAULT 50,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      TEXT NOT NULL
);
```

Add a rule via a seed file:

`sql/seeds/governance/{code}.sql`:

```sql
INSERT INTO substrate.governance_rule
    (id, code, description, predicate_sql, action_id, enabled, severity, created_by)
VALUES
    ({id}, '{code}',
     '{description}',
     $RULE$
         entity_id IN (
             SELECT epr.entity_id
             FROM substrate.entity_pragmatic_register epr
             JOIN substrate.ref_pragmatic_register r ON r.id = epr.register_id
             WHERE r.code = 'pejorative_directed'
               AND epr.mu > 85000
         )
     $RULE$,
     (SELECT id FROM substrate.ref_governance_action WHERE code = 'flag'),
     true,
     50,
     'system')
ON CONFLICT (id) DO NOTHING;
```

The `predicate_sql` is a fragment that completes a `WHERE entity_id IN (...)` or similar shape, depending on what kind of substrate object the rule applies to.

### 2. Add the migration

`sql/migrations/{NNNN}_add_{code}_governance_rule.up.sql`:

```sql
\i ../seeds/governance/{code}.sql
```

Down:

```sql
DELETE FROM substrate.governance_rule WHERE code = '{code}';
```

### 3. Add a C# representation

`src/Hartonomous.Core/Governance/GovernanceRule.cs`:

```csharp
public sealed record GovernanceRule(
    int Id,
    string Code,
    string Description,
    string PredicateSql,
    GovernanceActionCode Action,
    bool Enabled,
    short Severity);
```

`src/Hartonomous.Engine/Governance/GovernanceRuleEngine.cs`:

```csharp
public sealed class GovernanceRuleEngine : IGovernanceRuleEngine
{
    public async Task<IReadOnlyList<GovernanceViolation>> EvaluateAsync(
        long entityId, CancellationToken ct)
    {
        // Loads enabled rules, runs each predicate against entityId.
        // Returns violations as structured records (rule_id, action, severity, evidence).
    }
}
```

The rule engine is invoked:
- During decomposition by the ingestion pipeline at the per-entity checkpoint.
- During inference by the traversal engine at each node visited.

### 4. Wire the action

For each `GovernanceActionCode`, the engine has a handler. New actions require a new handler:

```csharp
internal interface IGovernanceActionHandler
{
    GovernanceActionCode HandlesAction { get; }
    Task ApplyAsync(GovernanceViolation violation, CancellationToken ct);
}
```

Existing handlers cover `Flag`, `Annotate`, `Quarantine`, `HaltDecomposition`, `RefuseRecomposition`, `RefuseTraversal`, `RouteToReview`, `RecordAndPass`. If you need a new action, add it via recipe `06-add-reference-table.md` and implement a new handler.

### 5. Test the rule

`tests/Hartonomous.Engine.Tests/Governance/{Pascal}RuleTests.cs`:

```csharp
[Fact]
public async Task Rule_TriggersOnViolatingEntity()
{
    // arrange: substrate state with a violating entity
    // act: evaluate the rule
    // assert: violation produced with the expected action
}

[Fact]
public async Task Rule_DoesNotTriggerOnNonViolatingEntity()
{
    // arrange: substrate state with a clean entity
    // act
    // assert: no violation
}

[Fact]
public async Task Rule_DeterministicAcrossRepeatedEvaluations()
{
    var first = await engine.EvaluateAsync(entityId, ct);
    var second = await engine.EvaluateAsync(entityId, ct);
    second.Should().BeEquivalentTo(first);
}
```

### 6. Test against historical corpora (the governance sandbox)

```pwsh
pwsh scripts/ops/GovernanceSimulate.ps1 `
    -RuleCode {code} `
    -CorpusProvenance conflict_corpus
```

The simulator runs the rule's predicate against every entity from the named corpus and reports:
- Matches (true positives if the corpus is labeled).
- Non-matches (false negatives).
- Match rate, mean severity, distribution.

Use this to tune the rule before enabling in production.

### 7. Enable / disable

To temporarily disable a rule without deleting:

```pwsh
pwsh scripts/ops/GovernanceRule.ps1 -Disable -Code {code}
```

This sets `enabled = false`. Re-enable with `-Enable`.

### 8. Document

- `docs/specs/engine/substrate-governance.md` — add the rule to the inventory.
- `docs/governance/{code}.md` — write a per-rule rationale doc (when added, why, evidence basis, expected match rate).

### 9. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}RuleTests
pwsh scripts/ops/GovernanceSimulate.ps1 -RuleCode {code} -CorpusProvenance conflict_corpus
```

---

## Canonical example — flag pejorative-directed addressee

```sql
-- sql/seeds/governance/flag_pejorative_addressee.sql
INSERT INTO substrate.governance_rule
    (id, code, description, predicate_sql, action_id, enabled, severity, created_by)
VALUES
    (1, 'flag_pejorative_addressee',
     'Flag entities that resolve to a lemma classified as pejorative_directed with μ > 85000.',
     $RULE$
         entity_id IN (
             SELECT m.entity_id
             FROM substrate.edge_member m
             JOIN substrate.edge t ON t.id = m.edge_id AND t.edge_type_id = (SELECT id FROM substrate.edge_type WHERE code = 'has_lemma')
             JOIN substrate.entity_pragmatic_register epr ON epr.entity_id = m.entity_id
             JOIN substrate.ref_pragmatic_register r ON r.id = epr.register_id
             WHERE r.code = 'pejorative_directed'
               AND epr.mu > 85000
         )
     $RULE$,
     (SELECT id FROM substrate.ref_governance_action WHERE code = 'flag'),
     true,
     60,
     'system')
ON CONFLICT (id) DO NOTHING;
```

---

## Anti-patterns

- **DON'T** hardcode the rule logic in C#. Rules are SQL predicates stored in the database — that's how they remain auditable, modifiable without redeploy, and per-practitioner-scopable.
- **DON'T** delete a rule that has fired in the past. Disable instead. The historical record links to `rule_id` and you'll lose the audit trail.
- **DON'T** combine multiple unrelated checks into one rule. One rule, one purpose. Compose at the rule-engine level if needed.
- **DON'T** skip the simulator step. Test against historical corpora before enabling. Rules tuned only against synthetic test cases will surprise you in production.
- **DON'T** use a non-deterministic predicate (`random()`, time-based). Rules must produce the same result on the same substrate state every time.

---

## Verification checklist

- [ ] Rule schema seeded via migration
- [ ] Predicate is a SQL fragment over substrate state, deterministic
- [ ] Action references a row in `ref_governance_action`
- [ ] Unit tests cover trigger and non-trigger scenarios
- [ ] Determinism test passes
- [ ] Simulator run against relevant historical corpora; results documented
- [ ] Per-rule doc in `docs/governance/{code}.md`
- [ ] Rule inventory updated in `docs/specs/engine/substrate-governance.md`

---

## Related recipes

- `05-add-junction-table.md` — junction the rule predicates over
- `06-add-reference-table.md` — for new action types
- `07-add-provenance-class.md` — for new corpora the rule scopes to
