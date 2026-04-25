#include <stddef.h>
#include <string.h>

#include <omp.h>
#include <mkl.h>
#include <mkl_service.h>

#include "hartonomous.h"

/*
 * One-shot guard so MKL CBWR is set exactly once per process. Postgres uses
 * a process-per-connection model where each forked backend has its own MKL
 * thread-pool state (the postmaster's MKL load is shared via copy-on-write
 * but per-backend pools must be rebuilt). Eager MKL init in _PG_init forces
 * every fresh backend to pay the ~7-second pool-rebuild cost on connection
 * open. Lazy init means only backends that actually invoke a compute path
 * pay it — graph-traversal-only backends (the inference engine's hot path)
 * stay fast.
 */
#if defined(_WIN32)
#  include <windows.h>
   static volatile LONG hartonomous_mkl_init_state = 0; /* 0=unset, 1=in-progress, 2=done */
#else
#  include <stdatomic.h>
   static atomic_int hartonomous_mkl_init_state = 0;
#endif
static int hartonomous_mkl_init_branch = -1;

static int hartonomous_run_mkl_init(void) {
    int rc = mkl_cbwr_set(MKL_CBWR_AUTO | MKL_CBWR_STRICT);
    if (rc != MKL_CBWR_SUCCESS) {
        return -1;
    }
    return mkl_cbwr_get(MKL_CBWR_BRANCH);
}

int hartonomous_ensure_mkl_initialized(void) {
#if defined(_WIN32)
    if (InterlockedCompareExchange(&hartonomous_mkl_init_state, 1, 0) == 0) {
        hartonomous_mkl_init_branch = hartonomous_run_mkl_init();
        InterlockedExchange(&hartonomous_mkl_init_state, 2);
    } else {
        while (hartonomous_mkl_init_state != 2) { /* spin until peer finishes */ }
    }
#else
    int expected = 0;
    if (atomic_compare_exchange_strong(&hartonomous_mkl_init_state, &expected, 1)) {
        hartonomous_mkl_init_branch = hartonomous_run_mkl_init();
        atomic_store(&hartonomous_mkl_init_state, 2);
    } else {
        while (atomic_load(&hartonomous_mkl_init_state) != 2) { /* spin */ }
    }
#endif
    return hartonomous_mkl_init_branch;
}

/*
 * Populate a hartonomous_runtime_info_t with everything the managed layer
 * needs to assert that the intended acceleration is actually in use. This
 * is the introspection hook: without it, a test can verify correctness but
 * not that correctness came from the expected code path (AVX-512 MKL kernel
 * vs scalar fallback, OpenMP pool vs single-threaded, CBWR set vs unset).
 */
void hartonomous_runtime_info(hartonomous_runtime_info_t* out) {
    if (out == NULL) {
        return;
    }
    memset(out, 0, sizeof(*out));

    out->has_mkl = 1;
    mkl_get_version_string(out->mkl_version, (int)sizeof(out->mkl_version));
    out->mkl_max_threads = mkl_get_max_threads();
    out->omp_max_threads = omp_get_max_threads();
    out->cbwr_branch     = mkl_cbwr_get(MKL_CBWR_BRANCH);
}

/*
 * Force MKL conditional-bitwise-reproducibility to AUTO|STRICT and return the
 * resolved branch (>= 0). This is the deterministic-execution gate: callers
 * that need byte-identical math across runs (CLI ingest entrypoint, test
 * harnesses, the first MKL-using SQL function in a postgres backend) call
 * this before doing any compute.
 *
 * Now delegates to hartonomous_ensure_mkl_initialized() so it's safe to call
 * from _PG_init AND from per-function entry points without paying the cost
 * twice. The PG extension's _PG_init no longer calls this; instead, every
 * MKL-using SQL function calls it on entry as a one-shot init.
 *
 * Returns the active branch on success, or -1 if mkl_cbwr_set rejects.
 */
int hartonomous_init_determinism(void) {
    return hartonomous_ensure_mkl_initialized();
}
