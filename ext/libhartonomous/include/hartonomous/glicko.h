/* libhartonomous — glicko.h
 *
 * Glicko-2 bulk update — closed-loop substrate learning surface.
 * Sign-aware: positive correlation → score=1, negative → score=0,
 * draw → score=0.5. Same inputs → bit-identical output, Law #6.
 */

#ifndef HARTONOMOUS_GLICKO_H
#define HARTONOMOUS_GLICKO_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

HARTONOMOUS_API int hartonomous_glicko2_bulk_update(
    int64_t n,
    const double* mu,
    const double* sigma,
    const double* volatility,
    const double* opp_mu,
    const double* opp_sigma,
    const double* score,
    double* new_mu,
    double* new_sigma,
    double* new_volatility);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_GLICKO_H */
