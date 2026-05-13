/* libhartonomous — runtime.h
 *
 * MKL/OpenMP introspection and one-shot determinism initialization.
 */

#ifndef HARTONOMOUS_RUNTIME_H
#define HARTONOMOUS_RUNTIME_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hartonomous_runtime_info {
    int  has_mkl;
    char mkl_version[128];
    int  mkl_max_threads;
    int  omp_max_threads;
    int  cbwr_branch;
} hartonomous_runtime_info_t;

HARTONOMOUS_API void hartonomous_runtime_info(hartonomous_runtime_info_t* out);

HARTONOMOUS_API int hartonomous_init_determinism(void);
HARTONOMOUS_API int hartonomous_ensure_mkl_initialized(void);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_RUNTIME_H */
