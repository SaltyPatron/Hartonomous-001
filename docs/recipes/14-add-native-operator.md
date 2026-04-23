# Recipe 14: Add a Native Operator (PG Extension or libhartonomous)

Intent: add a new C-implemented function or operator to the `hartonomous` PostgreSQL extension or the shared `libhartonomous` library.

This is for compute-heavy primitives (BLAKE3, 4D distance, traversal, geometric anomaly detection, etc.) that need C performance and ABI stability.

---

## Prerequisites

- The operator's purpose, signature, and complexity bound documented.
- Decision: does this belong in:
  - **`libhartonomous`** (shared C library, P/Invoked from C# or linked into the PG extension) — for primitives reusable across consumers.
  - **`ext/hartonomous_pg`** (PostgreSQL extension) — for SQL-callable operators wired into the planner.
  - **Both** (implement in `libhartonomous`, wrap in PG extension) — preferred for primitives also used from C#.

---

## Steps for `libhartonomous`

### 1. Declare the public API in a header

`ext/libhartonomous/include/hartonomous/{module}.h`:

```c
#ifndef HTNS_{MODULE}_H
#define HTNS_{MODULE}_H

#include <stddef.h>
#include <stdint.h>
#include "hartonomous/types.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * One-line description of the function.
 *
 * Detailed description: parameters, semantics, complexity, error conditions.
 *
 * @param arg1 Description.
 * @param arg2 Description.
 * @param out  Caller-allocated output buffer, must be at least N bytes.
 * @return HTNS_OK on success, error code on failure.
 */
htns_error_t htns_{module}_{verb}(
    const {type1}* arg1,
    {type2} arg2,
    {type3}* out
);

#ifdef __cplusplus
}
#endif

#endif /* HTNS_{MODULE}_H */
```

Rules:
- Header guards: `HTNS_{MODULE}_H`.
- All public functions begin with `htns_` and follow `htns_{module}_{verb}` naming.
- All allocations are caller-allocated. No hidden mallocs.
- Doxygen-style comments on every public function.
- `extern "C"` for C++ consumers.

### 2. Implement

`ext/libhartonomous/src/{module}.c`:

```c
#include "hartonomous/{module}.h"
#include "{module}_internal.h"  // if internal header exists
#include <string.h>
// ... other includes

htns_error_t htns_{module}_{verb}(
    const {type1}* arg1,
    {type2} arg2,
    {type3}* out)
{
    if (arg1 == NULL || out == NULL) {
        return HTNS_ERR_NULL_ARG;
    }
    // ... implementation
    return HTNS_OK;
}
```

### 3. SIMD specializations (optional)

If the operator benefits from SIMD, implement specializations and dispatch at runtime:

`ext/libhartonomous/src/{module}_avx2.c`:

```c
#include "{module}_internal.h"
// AVX2 implementation
```

The dispatcher in `{module}.c` calls the right specialization based on detected ISA (set during `htns_init()`).

### 4. Register in CMake

Edit `ext/libhartonomous/CMakeLists.txt`:

```cmake
target_sources(hartonomous PRIVATE
    src/{module}.c
    src/{module}_avx2.c   # if applicable
)
```

### 5. Add tests

`ext/libhartonomous/tests/test_{module}.c`:

```c
#include "hartonomous/{module}.h"
#include <gtest/gtest.h>

TEST({Module}Test, ValidInput_ReturnsOk) {
    {type1} arg1 = ...;
    {type3} out;
    EXPECT_EQ(HTNS_OK, htns_{module}_{verb}(&arg1, 5, &out));
    EXPECT_EQ(expected, out);
}

TEST({Module}Test, NullArg_ReturnsError) {
    {type3} out;
    EXPECT_EQ(HTNS_ERR_NULL_ARG, htns_{module}_{verb}(NULL, 5, &out));
}
```

Register in `ext/libhartonomous/tests/CMakeLists.txt`:

```cmake
add_executable(test_{module} test_{module}.c)
target_link_libraries(test_{module} PRIVATE hartonomous gtest_main)
add_test(NAME test_{module} COMMAND test_{module})
```

### 6. Build and run tests

```pwsh
pwsh scripts/build/Native.ps1
pwsh scripts/test/Native.ps1 -Filter {Module}Test
```

### 7. Expose to C# (recipe `15-add-pinvoke-surface.md`)

If the operator should be callable from C#, add a P/Invoke surface in `Hartonomous.Core/Native/{Module}Native.cs`.

---

## Steps for `ext/hartonomous_pg` (PG extension wrapping)

### 1. Implement the SQL-callable wrapper

`ext/hartonomous_pg/src/{module}.c`:

```c
#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "hartonomous/{module}.h"  // libhartonomous header

PG_FUNCTION_INFO_V1(pg_htns_{module}_{verb});
Datum pg_htns_{module}_{verb}(PG_FUNCTION_ARGS) {
    {type1} arg1 = PG_GETARG_{TYPE1}(0);
    {type2} arg2 = PG_GETARG_{TYPE2}(1);
    {type3} out;

    htns_error_t err = htns_{module}_{verb}(&arg1, arg2, &out);
    if (err != HTNS_OK) {
        ereport(ERROR,
            (errcode(ERRCODE_INTERNAL_ERROR),
             errmsg("hartonomous {module} failed: %s", htns_error_message(err))));
    }

    PG_RETURN_{TYPE3}(out);
}
```

### 2. Register the function in the extension SQL script

`ext/hartonomous_pg/sql/hartonomous--{version}.sql`:

```sql
CREATE FUNCTION hartonomous.{module}_{verb}({type1}, {type2})
RETURNS {type3}
AS '$libdir/hartonomous', 'pg_htns_{module}_{verb}'
LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION hartonomous.{module}_{verb}({type1}, {type2}) IS
    'One-line description.';
```

### 3. Add a regression test

`ext/hartonomous_pg/test/sql/{module}.sql`:

```sql
SELECT hartonomous.{module}_{verb}('input1', 5);
```

`ext/hartonomous_pg/test/expected/{module}.out`:

```
 {module}_{verb}
-----------------
 expected_value
(1 row)
```

### 4. Build and test the extension

```pwsh
pwsh scripts/build/PgExtension.ps1
pwsh scripts/test/PgRegress.ps1 -Filter {module}
```

### 5. Bump the extension version (if API changed)

If the extension's public surface changed (new function, signature change, removal):
- Bump version in `ext/hartonomous_pg/hartonomous.control` (e.g., `1.0` → `1.1`).
- Add an upgrade SQL script: `ext/hartonomous_pg/sql/hartonomous--1.0--1.1.sql` containing the diff.
- Document in `docs/specs/native/pg-extension.md`.

---

## Anti-patterns (specific to native code)

- **DON'T** allocate inside the native function and return a pointer the caller must free without explicitly documenting and providing a paired `htns_free_*` function.
- **DON'T** rely on default struct layout for P/Invoke. Use explicit `[StructLayout]` on the C# side; document the C struct layout in the header comment.
- **DON'T** skip ISA detection. Use `htns_isa_supports(...)` to dispatch; do not hardcode AVX-512 (the consumer ceiling is AVX2+FMA3+AVX-VNNI+BMI2).
- **DON'T** call `palloc` outside a PostgreSQL function context. The PG extension wrapper uses palloc; libhartonomous uses caller-allocated buffers.
- **DON'T** emit log messages from libhartonomous. Errors are returned as `htns_error_t` codes; the caller decides how to log.
- **DON'T** use floating-point reductions without `MKL_CBWR=AUTO,STRICT`. Determinism is required (Law #6).

---

## Verification checklist

- [ ] Header file with Doxygen comments, header guards, `extern "C"`
- [ ] Implementation with NULL-arg checks, no hidden allocations
- [ ] CMake registration
- [ ] Google Test coverage for valid + invalid inputs
- [ ] (If wrapping in PG extension) PG_FUNCTION_INFO_V1 wrapper with proper PG_GETARG / PG_RETURN
- [ ] (If wrapping in PG extension) SQL CREATE FUNCTION in extension script
- [ ] (If wrapping in PG extension) Regression test passes
- [ ] (If API changed) Extension version bumped, upgrade script provided
- [ ] Determinism verified (test runs twice, byte-identical output)

---

## Related recipes

- `15-add-pinvoke-surface.md` — exposing libhartonomous functions to C#
- `11-add-sql-function.md` — for PL/pgSQL functions (no native code)
