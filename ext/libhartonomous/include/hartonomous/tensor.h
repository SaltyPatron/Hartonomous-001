/* libhartonomous — tensor.h
 *
 * Lossless dtype widening to f64. Never normalizes, never clamps,
 * never quantizes.
 *
 * Source dtype encoding:
 *   0=f64 1=f32 2=f16 3=bf16 4=i8 5=u8 6=i16 7=i32 8=i64
 *   9=u16 10=u32 11=u64 12=bool
 */

#ifndef HARTONOMOUS_TENSOR_H
#define HARTONOMOUS_TENSOR_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

HARTONOMOUS_API int hartonomous_tensor_decode_f64(
    const void* src, size_t src_bytes,
    int src_dtype,
    double* dst, int64_t dst_count);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_TENSOR_H */
