# Recipe 12: Add a SQL Procedure

Intent: add a stored procedure (e.g., `ingest_entity_batch`, `update_significance_glicko`, `apply_governance_action`). Procedures may manage transactions; functions cannot.

For pure read functions, see recipe `11-add-sql-function.md`.

---

## Prerequisites

- Procedure name, snake_case verb.
- Argument types and (optionally) `INOUT` outputs.
- Decision: does the procedure manage its own COMMIT/ROLLBACK, or run inside the caller's transaction?

---

## Steps

### 1. Create the schema file

`sql/schema/procedures/{name}.sql`:

```sql
CREATE OR REPLACE PROCEDURE substrate.{name}(
    p_arg1   {type1},
    p_arg2   {type2},
    INOUT    p_result   {type3} DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_local  {type};
BEGIN
    -- Body. May execute COMMIT / ROLLBACK if appropriate.
    -- DO NOT issue COMMIT inside a function or trigger context.

    -- Standard pattern: validate inputs, do work, set INOUT, RAISE on error.
    IF p_arg1 IS NULL THEN
        RAISE EXCEPTION 'p_arg1 must not be null' USING ERRCODE = '22004';
    END IF;

    -- ... do work ...

    p_result := computed_value;
END;
$$;

COMMENT ON PROCEDURE substrate.{name}({type1}, {type2}, {type3}) IS
    'One-line description. State whether the procedure commits internally or expects external transaction control.';
```

### 2. Add the migration

`sql/migrations/{NNNN}_add_{name}_procedure.up.sql`:

```sql
\i ../schema/procedures/{name}.sql
```

Down:

```sql
DROP PROCEDURE IF EXISTS substrate.{name}({type1}, {type2}, {type3});
```

### 3. Add C# call surface

`src/Hartonomous.Engine/Data/{Pascal}Writer.cs` (extend or create):

```csharp
public async Task {Pascal}Async({TArg1} arg1, {TArg2} arg2, CancellationToken ct)
{
    const string Sql = "CALL substrate.{name}($1, $2, NULL)";
    await using var cmd = _connection.CreateCommand();
    cmd.CommandText = Sql;
    cmd.Parameters.Add(new NpgsqlParameter { Value = arg1 });
    cmd.Parameters.Add(new NpgsqlParameter { Value = arg2 });
    await cmd.ExecuteNonQueryAsync(ct);
}
```

If the procedure has `INOUT` outputs, retrieve them:

```csharp
public async Task<{TResult}> {Pascal}Async({TArg1} arg1, CancellationToken ct)
{
    const string Sql = "CALL substrate.{name}($1, NULL)";
    await using var cmd = _connection.CreateCommand();
    cmd.CommandText = Sql;
    cmd.Parameters.Add(new NpgsqlParameter { Value = arg1 });
    var resultParam = new NpgsqlParameter
    {
        Direction = ParameterDirection.InputOutput,
        Value = DBNull.Value
    };
    cmd.Parameters.Add(resultParam);
    await cmd.ExecuteNonQueryAsync(ct);
    return ({TResult})resultParam.Value!;
}
```

### 4. Document transaction semantics

In the C# call surface XML doc:

```csharp
/// <summary>
/// Calls substrate.{name}.
/// </summary>
/// <remarks>
/// Transaction semantics: this procedure {commits internally | runs in the caller's transaction}.
/// If the procedure commits internally, the C# wrapper MUST be invoked outside an open transaction.
/// </remarks>
```

### 5. Add tests

```csharp
[Fact]
public async Task {Pascal}_Pre_Post()
{
    // arrange: known DB state
    // act: invoke the procedure
    // assert: post-condition observed via separate read query
}
```

### 6. Document

`docs/specs/sql/stored-procedures.md` — add the row to the procedure inventory with signature, transaction semantics, error behavior.

### 7. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Integration.ps1 -Filter {Pascal}WriterTests
```

---

## Procedure transaction semantics — the two patterns

### Pattern A: Caller-controlled transaction (most common)

```sql
CREATE OR REPLACE PROCEDURE substrate.update_significance_glicko(
    p_entity_id  BIGINT,
    p_outcome    INT
)
LANGUAGE plpgsql
AS $$
BEGIN
    -- No COMMIT / ROLLBACK. Caller wraps in a transaction.
    UPDATE substrate.significance
       SET mu = ..., sigma = ..., games = games + 1
     WHERE entity_id = p_entity_id;
END;
$$;
```

C# usage: invoke inside a transaction.

```csharp
await using var tx = await conn.BeginTransactionAsync(ct);
await writer.UpdateSignificanceGlickoAsync(entityId, outcome, ct);
await tx.CommitAsync(ct);
```

### Pattern B: Procedure-managed transactions (for long-running batch work)

```sql
CREATE OR REPLACE PROCEDURE substrate.ingest_entity_batch(
    p_hashes      BYTEA[],
    p_type_ids    INT[],
    INOUT         p_inserted  INT DEFAULT 0
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_batch_size INT := 1000;
    v_total      INT;
BEGIN
    v_total := array_length(p_hashes, 1);
    FOR i IN 1 .. v_total BY v_batch_size LOOP
        INSERT INTO substrate.entity (hash, entity_type_id)
        SELECT h, t
          FROM unnest(p_hashes[i:LEAST(i+v_batch_size-1, v_total)],
                      p_type_ids[i:LEAST(i+v_batch_size-1, v_total)]) AS x(h, t)
        ON CONFLICT (hash, entity_type_id) DO NOTHING;
        p_inserted := p_inserted + (SELECT count(*) FROM ...);
        COMMIT;  -- Periodic commit to avoid huge transaction.
    END LOOP;
END;
$$;
```

C# usage: invoke without a wrapping transaction.

```csharp
await writer.IngestEntityBatchAsync(hashes, typeIds, ct); // procedure commits internally
```

DOCUMENT the pattern in the COMMENT and in the C# wrapper's XML doc. Mixing patterns silently corrupts transaction semantics.

---

## Anti-patterns

- **DON'T** issue COMMIT inside a procedure called from a function or trigger. PostgreSQL rejects this with "cannot begin/end transactions in PL/pgSQL".
- **DON'T** mix Pattern A and Pattern B without documentation. The caller MUST know which pattern to use.
- **DON'T** put compute in a procedure. Hashing, Merkle, canonicalization, geometry — all client-side via libhartonomous. Procedures coordinate batched DML; they do not compute.
- **DON'T** put per-row INSERTs in a loop (RBAR — AP-SQL-11). Use set-based operations (`unnest`, `INSERT ... SELECT`, `COPY`). The only reason to loop in a procedure is for periodic COMMIT during a long batch — and even then, each loop iteration's body is set-based.
- **DON'T** use cursors (AP-SQL-12).
- **DON'T** swallow errors. Re-RAISE with context if you need to add information.
- **DON'T** invoke the procedure via inline string concatenation in C#. `const string Sql = "CALL ..."` and parameterized commands only.

---

## Verification checklist

- [ ] Procedure file at `sql/schema/procedures/{name}.sql`, one `CREATE OR REPLACE PROCEDURE`
- [ ] COMMENT documents transaction semantics
- [ ] Migration up/down pair present
- [ ] C# wrapper documents transaction pattern (A or B)
- [ ] Tests cover pre/post conditions
- [ ] Procedure inventory updated in `docs/specs/sql/stored-procedures.md`

---

## Related recipes

- `11-add-sql-function.md` — for read-only functions
- `13-add-migration.md` — migration mechanics
