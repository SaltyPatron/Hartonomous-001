# Recipes — How-To Guides

Each recipe answers ONE assembly question with numbered steps, exact file paths, copy-paste code, and verification commands. Open the recipe that matches the task; do the steps; the result conforms to the canonical architecture.

Format per recipe:
- **Intent** — what this recipe accomplishes (one sentence).
- **Prerequisites** — what must already exist.
- **Steps** — numbered, each with file path and exact code/SQL.
- **Verification** — the command that proves it worked.
- **Anti-patterns** — what to never do here.

---

## The recipes

| # | Recipe | Use when... |
|---|---|---|
| 00 | [The vertical slice — input to output, end to end](00-vertical-slice.md) | You want the full operational map. Read first to orient. |
| 01 | [Fresh setup — clone to first inference query](01-fresh-setup.md) | New machine; you need a working substrate from zero. |
| 02 | [Add an entity type](02-add-entity-type.md) | A new kind of substrate atom or composition needs a type code. |
| 03 | [Add an edge type](03-add-edge-type.md) | A new typed relation between entity types is needed. |
| 04 | [Add a physicality type](04-add-physicality-type.md) | A new geometric representation (firefly variant, spectrogram kind, etc.) is needed. |
| 05 | [Add a junction table](05-add-junction-table.md) | A new app-layer classification surface (rated or not) is needed. |
| 06 | [Add a reference table](06-add-reference-table.md) | A new bounded vocabulary (POS, deprel, register, action kind) is needed. |
| 07 | [Add a provenance class](07-add-provenance-class.md) | A new corpus or data source is being ingested. |
| 08 | [Add a decomposer](08-add-decomposer.md) | A new source format needs a parser that submits content to the central pipeline. |
| 09 | [Add an analysis pass](09-add-analysis-pass.md) | A new model-analysis or modality-analysis pass needs to run inside an existing decomposer. |
| 10 | [Add a recomposer](10-add-recomposer.md) | A new output reconstruction surface (modality or format) is needed. |
| 11 | [Add a SQL function](11-add-sql-function.md) | A new pure SQL function needs to be callable from queries. |
| 12 | [Add a SQL procedure](12-add-sql-procedure.md) | A new transactional or batch-managing stored procedure is needed. |
| 13 | [Add a migration](13-add-migration.md) | The schema needs to evolve. |
| 14 | [Add a native operator](14-add-native-operator.md) | A new compute primitive needs C performance, in libhartonomous or the PG extension. |
| 15 | [Add a P/Invoke surface](15-add-pinvoke-surface.md) | An existing native function needs to be callable from C# via the compute facade. |
| 16 | [Add a governance rule](16-add-governance-rule.md) | A new SQL-predicate rule needs to fire during the forward pass. |
| 17 | [Add a test](17-add-test.md) | Unit, integration, contract, native, or PG regression test. |
| 18 | [Add a CLI command](18-add-cli-command.md) | A new command-line operation needs an entrypoint. |
| 19 | [Add a phase](19-add-phase.md) | A new orchestration phase needs to be inserted into the runner. |

---

## Cross-cutting reference

These reference docs are LOOKUP TABLES, not narrative — scan them, find your row, apply.

- [`reference/file-layout.md`](../reference/file-layout.md) — where every kind of artifact goes
- [`reference/naming.md`](../reference/naming.md) — every naming convention
- [`reference/anti-patterns.md`](../reference/anti-patterns.md) — every wrong shape and the right shape
- [`reference/allowed-dependencies.md`](../reference/allowed-dependencies.md) — what can reference what
