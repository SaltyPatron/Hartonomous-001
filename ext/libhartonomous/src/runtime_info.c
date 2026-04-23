#include <stddef.h>
#include <string.h>

#include <omp.h>
#include <mkl.h>
#include <mkl_service.h>

#include "hartonomous.h"

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
 * that need byte-identical math across runs (PG extension load, CLI ingest
 * entrypoint, test harnesses) must invoke this before doing any compute.
 *
 * Idempotent: subsequent calls with CBWR already set are no-ops at the MKL
 * level. Returns the active branch on success, or -1 if mkl_cbwr_set rejects
 * the request (which would indicate MKL was used before this call — a
 * deterministic-init violation we want to surface immediately).
 */
int hartonomous_init_determinism(void) {
    int rc = mkl_cbwr_set(MKL_CBWR_AUTO | MKL_CBWR_STRICT);
    if (rc != MKL_CBWR_SUCCESS) {
        return -1;
    }
    return mkl_cbwr_get(MKL_CBWR_BRANCH);
}
