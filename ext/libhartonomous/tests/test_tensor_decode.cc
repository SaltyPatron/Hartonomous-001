#include <cstdint>
#include <cstring>
#include <vector>

#include <gtest/gtest.h>
#include "hartonomous.h"

namespace {

enum {
    DT_F64 = 0, DT_F32 = 1, DT_F16 = 2, DT_BF16 = 3,
    DT_I8 = 4, DT_U8 = 5, DT_I16 = 6, DT_I32 = 7,
    DT_I64 = 8, DT_U16 = 9, DT_U32 = 10, DT_U64 = 11,
    DT_BOOL = 12,
};

uint16_t F32ToBf16Bits(float f) {
    uint32_t b;
    std::memcpy(&b, &f, 4);
    return static_cast<uint16_t>(b >> 16);
}

uint16_t F32ToF16Bits(float f) {
    uint32_t x;
    std::memcpy(&x, &f, 4);
    uint32_t sign = (x >> 31) & 1u;
    int exp = static_cast<int>((x >> 23) & 0xFFu) - 127;
    uint32_t frac = x & 0x7FFFFFu;
    uint16_t out;
    if (exp < -14) {
        out = static_cast<uint16_t>(sign << 15);
    } else if (exp > 15) {
        out = static_cast<uint16_t>((sign << 15) | (0x1F << 10));
    } else {
        out = static_cast<uint16_t>((sign << 15) | ((exp + 15) << 10) | (frac >> 13));
    }
    return out;
}

}  // namespace

TEST(TensorDecode, F32RoundTrip) {
    std::vector<float> src = {1.0f, -2.5f, 3.14159f, -0.0f, 1e-6f, 1e6f};
    std::vector<double> dst(src.size(), 0.0);
    int rc = hartonomous_tensor_decode_f64(
        src.data(), src.size() * 4, DT_F32, dst.data(), static_cast<int64_t>(src.size()));
    ASSERT_EQ(rc, 0);
    for (size_t i = 0; i < src.size(); ++i) {
        EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(src[i]));
    }
}

TEST(TensorDecode, F64Passthrough) {
    std::vector<double> src = {0.1, 0.2, 0.3, -1e300, 1e-300};
    std::vector<double> dst(src.size(), 0.0);
    int rc = hartonomous_tensor_decode_f64(
        src.data(), src.size() * 8, DT_F64, dst.data(), static_cast<int64_t>(src.size()));
    ASSERT_EQ(rc, 0);
    for (size_t i = 0; i < src.size(); ++i) {
        EXPECT_EQ(dst[i], src[i]);
    }
}

TEST(TensorDecode, Bf16KnownBits) {
    // bf16 = high 16 bits of f32. 0x3F80 == 1.0f.
    std::vector<uint16_t> src = {
        0x3F80,  // 1.0
        0xBF80,  // -1.0
        0x4000,  // 2.0
        0x3F00,  // 0.5
        0x0000,  // +0.0
    };
    std::vector<double> dst(src.size(), 0.0);
    int rc = hartonomous_tensor_decode_f64(
        src.data(), src.size() * 2, DT_BF16, dst.data(), static_cast<int64_t>(src.size()));
    ASSERT_EQ(rc, 0);
    EXPECT_DOUBLE_EQ(dst[0], 1.0);
    EXPECT_DOUBLE_EQ(dst[1], -1.0);
    EXPECT_DOUBLE_EQ(dst[2], 2.0);
    EXPECT_DOUBLE_EQ(dst[3], 0.5);
    EXPECT_DOUBLE_EQ(dst[4], 0.0);
}

TEST(TensorDecode, Bf16AvxBlockBoundary) {
    // dst_count = 17 exercises both the AVX2 block8 path (2 blocks = 16 elts)
    // and the scalar tail (1 elt). If the block kernel over-reads or
    // mis-aligns, this should trip /GS or produce a mismatch.
    std::vector<float> values;
    for (int i = 0; i < 17; ++i) {
        values.push_back(static_cast<float>(i) * 0.5f - 3.0f);
    }
    std::vector<uint16_t> src;
    for (float f : values) src.push_back(F32ToBf16Bits(f));

    std::vector<double> dst(src.size(), 0.0);
    int rc = hartonomous_tensor_decode_f64(
        src.data(), src.size() * 2, DT_BF16, dst.data(), static_cast<int64_t>(src.size()));
    ASSERT_EQ(rc, 0);
    for (size_t i = 0; i < values.size(); ++i) {
        // bf16 round-trip from an f32 that fits in bf16 is lossless.
        EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(values[i]));
    }
}

TEST(TensorDecode, Bf16LargeStress) {
    // Representative of a real safetensors tensor: ~1M elements. Exercises
    // the OMP parallel AVX2 block path at scale. Bf16 has only 7 mantissa
    // bits, so we generate values that are exactly representable by first
    // round-tripping our "expected" through bf16.
    const int64_t n = 1 << 20;
    std::vector<uint16_t> src(n);
    std::vector<float> expected_f32(n);
    for (int64_t i = 0; i < n; ++i) {
        float f = static_cast<float>((i % 1000) - 500) * 0.001f;
        uint16_t bits = F32ToBf16Bits(f);
        src[i] = bits;
        // Recover the exact f32 value the bf16 encodes (zero low 16 bits).
        uint32_t recovered = static_cast<uint32_t>(bits) << 16;
        std::memcpy(&expected_f32[i], &recovered, 4);
    }
    std::vector<double> dst(n, 0.0);
    int rc = hartonomous_tensor_decode_f64(
        src.data(), static_cast<size_t>(n) * 2, DT_BF16, dst.data(), n);
    ASSERT_EQ(rc, 0);
    for (int64_t i = 0; i < n; i += 1024) {
        EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(expected_f32[i]));
    }
}

TEST(TensorDecode, F16RoundTripWholeValues) {
    std::vector<float> values = {1.0f, -1.0f, 2.0f, 0.5f, 0.0f, -2.5f};
    std::vector<uint16_t> src;
    for (float f : values) src.push_back(F32ToF16Bits(f));
    std::vector<double> dst(src.size(), 0.0);
    int rc = hartonomous_tensor_decode_f64(
        src.data(), src.size() * 2, DT_F16, dst.data(), static_cast<int64_t>(src.size()));
    ASSERT_EQ(rc, 0);
    for (size_t i = 0; i < values.size(); ++i) {
        EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(values[i]));
    }
}

TEST(TensorDecode, IntegerDtypes) {
    // i8
    {
        std::vector<int8_t> src = {-128, -1, 0, 1, 127};
        std::vector<double> dst(src.size(), 0.0);
        ASSERT_EQ(0, hartonomous_tensor_decode_f64(
            src.data(), src.size(), DT_I8, dst.data(), static_cast<int64_t>(src.size())));
        for (size_t i = 0; i < src.size(); ++i) EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(src[i]));
    }
    // i32
    {
        std::vector<int32_t> src = {-2147483647 - 1, -1, 0, 1, 2147483647};
        std::vector<double> dst(src.size(), 0.0);
        ASSERT_EQ(0, hartonomous_tensor_decode_f64(
            src.data(), src.size() * 4, DT_I32, dst.data(), static_cast<int64_t>(src.size())));
        for (size_t i = 0; i < src.size(); ++i) EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(src[i]));
    }
    // i64
    {
        std::vector<int64_t> src = {INT64_MIN, -1, 0, 1, INT64_MAX};
        std::vector<double> dst(src.size(), 0.0);
        ASSERT_EQ(0, hartonomous_tensor_decode_f64(
            src.data(), src.size() * 8, DT_I64, dst.data(), static_cast<int64_t>(src.size())));
        // int64 → f64 is lossy at the extremes, but same C cast semantics.
        for (size_t i = 0; i < src.size(); ++i) EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(src[i]));
    }
    // u8 / bool
    {
        std::vector<uint8_t> src = {0, 1, 2, 254, 255};
        std::vector<double> dst(src.size(), 0.0);
        ASSERT_EQ(0, hartonomous_tensor_decode_f64(
            src.data(), src.size(), DT_U8, dst.data(), static_cast<int64_t>(src.size())));
        for (size_t i = 0; i < src.size(); ++i) EXPECT_DOUBLE_EQ(dst[i], static_cast<double>(src[i]));
    }
}

TEST(TensorDecode, RejectsUnsupportedDtype) {
    double d = 0.0;
    uint8_t src = 0;
    EXPECT_EQ(-8, hartonomous_tensor_decode_f64(&src, 1, /*unknown*/ 99, &d, 1));
}

TEST(TensorDecode, RejectsShortSource) {
    uint8_t src[4] = {0, 0, 0, 0};
    double dst[4] = {};
    // Request 4 f64 elements (32 bytes) from a 4-byte buffer.
    EXPECT_EQ(-2, hartonomous_tensor_decode_f64(src, 4, DT_F64, dst, 4));
}
