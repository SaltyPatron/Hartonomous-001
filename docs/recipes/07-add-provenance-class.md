# Recipe 07: Add a Provenance Class

Intent: register a new corpus or data source (e.g., `conflict_corpus`, `meta_llama`, `user_corpus_{name}`) so entities ingested from that source carry accurate provenance and trust priors.

---

## Prerequisites

- Provenance `code` — snake_case; format `{organization_or_curator}[_{corpus_kind}]` (see naming reference).
- Curator class — one of: `authoritative_standard`, `academic_curated`, `academic_consortium`, `community_curated`, `community_contributed`, `model_derived`, `user_input`, `system_computed`.
- Initial trust prior `mu` — the starting Glicko rating for entities and edges from this source. See the table in Phase 1 seed (migration `0005`) for reference values.

---

## Steps

### 1. Determine the initial trust prior μ

Consult the trust prior hierarchy:

| Curator class | Typical initial μ range |
|---|---|
| Authoritative standard (Unicode, ISO, SIL) | 1900 – 2000 |
| Academic curated (Princeton WordNet) | 1800 – 1900 |
| Academic consortium (OMW, UD) | 1500 – 1700 |
| Community curated (Wiktextract, CLDR) | 1300 – 1500 |
| Community contributed (Tatoeba, Common Crawl) | 1100 – 1300 |
| Model-derived (HuggingFace models, OpenAI) | 1400 – 1600 |
| User input | 900 – 1100 |
| System computed | 1200 – 1400 |

Pick a value that reflects the corpus's authoritativeness relative to existing corpora.

### 2. Add the seed file

`sql/seeds/provenance/{code}.sql`:

```sql
INSERT INTO substrate.provenance (code, curator_class, trust_prior_mu) VALUES
    ('{code}', '{curator_class}', {mu})
ON CONFLICT (code) DO NOTHING;
```

### 3. Add the migration

`sql/migrations/{NNNN}_add_{code}_provenance.up.sql`:

```sql
\i ../seeds/provenance/{code}.sql
```

Down:

```sql
DELETE FROM substrate.provenance WHERE code = '{code}';
```

### 4. (If this corpus will drive a decomposer) Declare the decomposer's provenance code

In the decomposer class:

```csharp
public sealed class {Pascal}Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "{code}";
    // ...
}
```

### 5. Document

Add a row to `docs/specs/decomposers/{decomposer}.md` § Provenance, and to any relevant spec that enumerates provenance codes.

### 6. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
psql -c "SELECT * FROM substrate.provenance WHERE code = '{code}';"
```

(The `psql` here is for one-shot verification only. Normal ops go through scripts.)

---

## Canonical example — adding `conflict_corpus`

Goal: ingest recorded dispute transcripts for governance-corpus work.

```sql
-- sql/seeds/provenance/conflict_corpus.sql
INSERT INTO substrate.provenance (code, curator_class, trust_prior_mu) VALUES
    ('conflict_corpus', 'community_contributed', 1200)
ON CONFLICT (code) DO NOTHING;
```

```sql
-- sql/migrations/0039_add_conflict_corpus_provenance.up.sql
\i ../seeds/provenance/conflict_corpus.sql
```

---

## When to add vs. when to scope an existing provenance

Add a new provenance class when:
- The corpus is from a distinct source that a practitioner might want to trust or distrust as a unit.
- The trust prior differs materially from any existing provenance.
- The corpus has ethics or licensing constraints that need to be tracked separately.

Do NOT add a new provenance class when:
- The corpus is a subset of an already-represented source (scope via an edge attribute instead).
- The content is anonymous / aggregated and can fairly be marked `community_contributed`.
- You just want a different trust prior for the same data (adjust the existing provenance instead of forking).

---

## Anti-patterns

- **DON'T** set trust prior μ outside the documented ranges without justification written into the migration comment.
- **DON'T** embed corpus-specific trust logic inside decomposer code. The provenance row IS the trust anchor.
- **DON'T** add a provenance class for "content of uncertain origin." That's `unknown` (if it exists) or `user_input`; not a new class.
- **DON'T** delete a provenance class that substrate content references. Deprecate by updating `curator_class` to `deprecated_*` instead.

---

## Verification checklist

- [ ] Seed file exists, one INSERT, idempotent
- [ ] Migration up/down pair present
- [ ] Decomposer's `ProvenanceCode` matches (if applicable)
- [ ] Documentation updated
- [ ] Migrate runs clean

---

## Related recipes

- `08-add-decomposer.md` — add a decomposer that uses this provenance
- `05-add-junction-table.md` — for junctions that carry `provenance_id`
- `16-add-governance-rule.md` — governance rules can scope by provenance
