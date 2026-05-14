/*
 * libhartonomous — umbrella header.
 *
 * Includes every per-domain public header. Callers can include this
 * single file or any individual hartonomous/<domain>.h directly.
 *
 * Header dependency order: version → hash → (everything else).
 */

#ifndef HARTONOMOUS_H
#define HARTONOMOUS_H

#include "hartonomous/version.h"
#include "hartonomous/hash.h"
#include "hartonomous/merkle.h"
#include "hartonomous/runtime.h"

#include "hartonomous/geometry.h"
#include "hartonomous/tensor.h"
#include "hartonomous/linalg.h"
#include "hartonomous/synthesis.h"
#include "hartonomous/glicko.h"

#include "hartonomous/text_decompose.h"
#include "hartonomous/trajectory.h"

#endif /* HARTONOMOUS_H */
