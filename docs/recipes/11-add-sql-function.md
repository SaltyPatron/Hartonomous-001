# Recipe 11: Add a SQL Function

Intent: add a new pure SQL function (e.g., `compute_centroid_4d`, `entity_label`, `find_lemmas_by_register`).

For C-implemented functions in the PG extension, see recipe `14-add-native-operator.md`. This recipe covers PL/pgSQL and SQL-language functions only.

---

## Prerequisites

- Function name, snake_case verb (see naming).
- Defined argument types and return type.
- Side-effect classification: `IMMUTABLE` (deterministic, no DB read), `STABLE` (deterministic per-statement, may read), `VOLATILE` (default; may write or have side effects).

---

## Steps

### 1. Create the schema file

`sql/schema/functions/{name}.sql`:

```sql
CREATE OR REPLACE FUNCTION substrate.{name}(
    p_arg1  {type1},
    p_arg2  {type2},
    p_arg3  {type3} DEFAULT {default}
)
RETURNS {return_type}
LANGUAGE {plpgsql|sql}
{IMMUTABLE|STABLE|VOLATILE}
{PARALLEL SAFE|PARALLEL RESTRICTED|PARALLEL UNSAFE}
AS $$
DECLARE
    v_local  {type};
BEGIN
    -- function body
    RETURN ...;
END;
$$;

COMMENT ON FUNCTION substrate.{name}({type1}, {type2}, {type3}) IS
    'One-line description of what this function does. Specify any non-obvious behavior.';
```

Rules:
- Always include the COMMENT.
- Always declare argument names with `p_` prefix (parameter) and locals with `v_` prefix (variable).
- Always specify side-effect class. Default `VOLATILE` is wrong for read-only functions; mark `STABLE` or `IMMUTABLE`.
- Always specify parallel safety. Default `PARALLEL UNSAFE` blocks parallel query plans; use the most permissive accurate setting.

### 2. Add the migration

`sql/migrations/{NNNN}_add_{name}_function.up.sql`:

```sql
\i ../schema/functions/{name}.sql
```

Down:

```sql
DROP FUNCTION IF EXISTS substrate.{name}({type1}, {type2}, {type3});
```

### 3. Add C# call surface (if used from C#)

If the function is invoked from C#, expose it through a typed wrapper. Don't construct raw SQL strings in business code.

`src/Hartonomous.Engine/Data/{Pascal}Reader.cs` (extend an existing reader, or create a new one):

```csharp
public async Task<{ResultType}> {Pascal}Async({TArg1} arg1, {TArg2} arg2, CancellationToken ct)
{
    const string Sql = "SELECT * FROM substrate.{name}($1, $2)";
    await using var cmd = _connection.CreateCommand();
    cmd.CommandText = Sql;
    cmd.Parameters.Add(new NpgsqlParameter { Value = arg1 });
    cmd.Parameters.Add(new NpgsqlParameter { Value = arg2 });
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    // ... read result
}
```

The SQL string is a `const` inside the data-access class. No inline SQL is built from user input.

### 4. Add tests

#### SQL test (pg_regress style)

`ext/hartonomous_pg/test/sql/{name}.sql` and `ext/hartonomous_pg/test/expected/{name}.out` — only if the function is part of the extension. For substrate.* functions, use `tests/Hartonomous.Integration.Tests/Sql/{Pascal}FunctionTests.cs`.

#### C# integration test

```csharp
[Fact]
public async Task {Pascal}_KnownInput_ReturnsExpectedResult()
{
    await using var conn = await OpenAsync();
    var actual = await conn.QuerySingleAsync<{ResultType}>(
        "SELECT * FROM substrate.{name}($1)", knownInput);
    actual.Should().Be(expected);
}
```

### 5. Document

`docs/specs/sql/functions.md` — add the row to the function inventory with signature, side-effect class, purpose, and example.

### 6. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Integration.ps1 -Filter {Pascal}FunctionTests
```

---

## Canonical example — `entity_label`

```sql
-- sql/schema/functions/entity_label.sql
CREATE OR REPLACE FUNCTION substrate.entity_label(p_entity_id BIGINT)
RETURNS TEXT
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
AS $$
DECLARE
    v_type_code  TEXT;
    v_label      TEXT;
BEGIN
    SELECT et.code INTO v_type_code
      FROM substrate.entity e
      JOIN substrate.entity_type et ON et.id = e.entity_type_id
     WHERE e.id = p_entity_id;

    IF v_type_code IS NULL THEN
        RETURN NULL;
    END IF;

    -- For text composition, recompose preview.
    IF v_type_code = 'text_composition' THEN
        v_label := substrate.recompose_text(p_entity_id, 64);
    ELSIF v_type_code = 'word_form' THEN
        v_label := substrate.recompose_text(p_entity_id, 32);
    ELSE
        v_label := v_type_code || ':' || p_entity_id::text;
    END IF;

    RETURN v_label;
END;
$$;

COMMENT ON FUNCTION substrate.entity_label(BIGINT) IS
    'Return a short human-readable label for an entity, suitable for diagnostics. Stable per row.';
```

```sql
-- sql/migrations/0039_add_entity_label_function.up.sql
\i ../schema/functions/entity_label.sql
```

---

## Anti-patterns

- **DON'T** put DDL inline in the migration body. Schema goes in `sql/schema/`.
- **DON'T** put compute logic in a function. Hashing, Merkle construction, canonicalization, geometric primitives — all of those are libhartonomous's job, run client-side. SQL functions are for set-based queries the planner can optimize, not for compute.
- **DON'T** loop row-by-row with `FOR rec IN SELECT ... LOOP`. RBAR is banned (AP-SQL-11). If you find yourself reaching for a loop, restructure to a set-based query or move the work to the client.
- **DON'T** use cursors (AP-SQL-12). Same reason as above.
- **DON'T** write recursive PL/pgSQL functions to walk the Merkle DAG (AP-SQL-13). The DAG is built client-side; SQL queries it via `WHERE entity_id IN (...)` or single JOINs.
- **DON'T** mark a function `IMMUTABLE` if it reads from a table — that's `STABLE` at most.
- **DON'T** mark a function `PARALLEL SAFE` if it accesses session state, sequences, or `random()`.
- **DON'T** use `EXECUTE format(...)` for dynamic SQL with user input. Validate via allowlist.
- **DON'T** raise generic errors. Use `RAISE EXCEPTION 'specific message %', value USING ERRCODE = 'XXNNN';` with a meaningful errcode.
- **DON'T** leave argument names unprefixed. `p_*` for parameters is the convention; without the prefix, name collisions with column names produce subtle bugs.
- **DON'T** invoke the function from C# via inline SQL string assembled with `$"..."`. Always use `const string SQL` and parameterized commands.

---

## Verification checklist

- [ ] Function file at `sql/schema/functions/{name}.sql`, one `CREATE OR REPLACE FUNCTION`
- [ ] COMMENT exists and is meaningful
- [ ] Side-effect class declared correctly
- [ ] Parallel safety declared
- [ ] Parameters use `p_` prefix, locals use `v_` prefix
- [ ] Migration up/down pair present
- [ ] C# wrapper added if used from code (no inline SQL strings)
- [ ] Test passes
- [ ] Function inventory updated in `docs/specs/sql/functions.md`

---

## Related recipes

- `12-add-sql-procedure.md` — for procedures (CALL, transaction-managing)
- `14-add-native-operator.md` — for C-implemented PG extension functions
- `13-add-migration.md` — migration mechanics
