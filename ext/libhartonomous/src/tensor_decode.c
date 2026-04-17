#include <stdint.h>
#include <string.h>

#include "hartonomous.h"

/* dtype enum mirrors hartonomous.h documentation. */
enum {
    HTNS_DTYPE_F64 = 0, HTNS_DTYPE_F32 = 1, HTNS_DTYPE_F16 = 2, HTNS_DTYPE_BF16 = 3,
    HTNS_DTYPE_I8 = 4, HTNS_DTYPE_U8 = 5, HTNS_DTYPE_I16 = 6, HTNS_DTYPE_I32 = 7,
    HTNS_DTYPE_I64 = 8, HTNS_DTYPE_U16 = 9, HTNS_DTYPE_U32 = 10, HTNS_DTYPE_U64 = 11,
    HTNS_DTYPE_BOOL = 12
};

/* IEEE 754 binary16 → binary64. Lossless: every f16 value is exactly representable in f64. */
static double f16_to_f64(uint16_t bits) {
    uint32_t sign = (uint32_t)(bits >> 15) & 0x1u;
    uint32_t exp  = (uint32_t)(bits >> 10) & 0x1Fu;
    uint32_t frac = (uint32_t)(bits) & 0x3FFu;
    uint32_t f32_bits;
    if (exp == 0) {
        if (frac == 0) {
            f32_bits = sign << 31;
        } else {
            /* Subnormal: shift mantissa until leading 1, adjust exponent. */
            int e = -14;
            while ((frac & 0x400u) == 0) { frac <<= 1; e -= 1; }
            frac &= 0x3FFu;
            f32_bits = (sign << 31) | (uint32_t)((e + 127) << 23) | (frac << 13);
        }
    } else if (exp == 0x1Fu) {
        f32_bits = (sign << 31) | (0xFFu << 23) | (frac << 13);
    } else {
        f32_bits = (sign << 31) | ((exp + (127 - 15)) << 23) | (frac << 13);
    }
    float f;
    memcpy(&f, &f32_bits, sizeof f);
    return (double)f;
}

/* bfloat16 → binary64. bf16 layout = high 16 bits of f32. */
static double bf16_to_f64(uint16_t bits) {
    uint32_t f32_bits = (uint32_t)bits << 16;
    float f;
    memcpy(&f, &f32_bits, sizeof f);
    return (double)f;
}

int hartonomous_tensor_decode_f64(
    const void* src, size_t src_bytes,
    int src_dtype,
    double* dst, int64_t dst_count
) {
    if (src == NULL || dst == NULL) return -1;
    if (dst_count <= 0) return -2;

    size_t elem_size;
    switch (src_dtype) {
        case HTNS_DTYPE_F64: elem_size = 8; break;
        case HTNS_DTYPE_F32: elem_size = 4; break;
        case HTNS_DTYPE_F16: elem_size = 2; break;
        case HTNS_DTYPE_BF16: elem_size = 2; break;
        case HTNS_DTYPE_I8: case HTNS_DTYPE_U8: case HTNS_DTYPE_BOOL: elem_size = 1; break;
        case HTNS_DTYPE_I16: case HTNS_DTYPE_U16: elem_size = 2; break;
        case HTNS_DTYPE_I32: case HTNS_DTYPE_U32: elem_size = 4; break;
        case HTNS_DTYPE_I64: case HTNS_DTYPE_U64: elem_size = 8; break;
        default: return -8;
    }
    if (src_bytes < (size_t)dst_count * elem_size) return -2;

    const uint8_t* p = (const uint8_t*)src;
    switch (src_dtype) {
        case HTNS_DTYPE_F64: {
            memcpy(dst, p, (size_t)dst_count * 8);
            break;
        }
        case HTNS_DTYPE_F32: {
            for (int64_t i = 0; i < dst_count; ++i) {
                float f;
                memcpy(&f, p + i * 4, 4);
                dst[i] = (double)f;
            }
            break;
        }
        case HTNS_DTYPE_F16: {
            for (int64_t i = 0; i < dst_count; ++i) {
                uint16_t bits;
                memcpy(&bits, p + i * 2, 2);
                dst[i] = f16_to_f64(bits);
            }
            break;
        }
        case HTNS_DTYPE_BF16: {
            for (int64_t i = 0; i < dst_count; ++i) {
                uint16_t bits;
                memcpy(&bits, p + i * 2, 2);
                dst[i] = bf16_to_f64(bits);
            }
            break;
        }
        case HTNS_DTYPE_I8: {
            for (int64_t i = 0; i < dst_count; ++i) dst[i] = (double)(int8_t)p[i];
            break;
        }
        case HTNS_DTYPE_U8: case HTNS_DTYPE_BOOL: {
            for (int64_t i = 0; i < dst_count; ++i) dst[i] = (double)p[i];
            break;
        }
        case HTNS_DTYPE_I16: {
            for (int64_t i = 0; i < dst_count; ++i) {
                int16_t v; memcpy(&v, p + i * 2, 2); dst[i] = (double)v;
            }
            break;
        }
        case HTNS_DTYPE_U16: {
            for (int64_t i = 0; i < dst_count; ++i) {
                uint16_t v; memcpy(&v, p + i * 2, 2); dst[i] = (double)v;
            }
            break;
        }
        case HTNS_DTYPE_I32: {
            for (int64_t i = 0; i < dst_count; ++i) {
                int32_t v; memcpy(&v, p + i * 4, 4); dst[i] = (double)v;
            }
            break;
        }
        case HTNS_DTYPE_U32: {
            for (int64_t i = 0; i < dst_count; ++i) {
                uint32_t v; memcpy(&v, p + i * 4, 4); dst[i] = (double)v;
            }
            break;
        }
        case HTNS_DTYPE_I64: {
            for (int64_t i = 0; i < dst_count; ++i) {
                int64_t v; memcpy(&v, p + i * 8, 8); dst[i] = (double)v;
            }
            break;
        }
        case HTNS_DTYPE_U64: {
            for (int64_t i = 0; i < dst_count; ++i) {
                uint64_t v; memcpy(&v, p + i * 8, 8); dst[i] = (double)v;
            }
            break;
        }
    }
    return 0;
}
