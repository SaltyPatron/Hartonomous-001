#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include <random>
#include <vector>

#include <gtest/gtest.h>
#include "hartonomous.h"

namespace {

std::vector<double> Reference(
    int op_a, int op_b,
    int64_t m, int64_t n, int64_t k,
    double alpha,
    const std::vector<double>& a, int64_t lda,
    const std::vector<double>& b, int64_t ldb,
    double beta,
    std::vector<double> c, int64_t ldc
) {
    auto A = [&](int64_t i, int64_t j) {
        return op_a ? a[j * lda + i] : a[i * lda + j];
    };
    auto B = [&](int64_t i, int64_t j) {
        return op_b ? b[j * ldb + i] : b[i * ldb + j];
    };
    for (int64_t i = 0; i < m; ++i) {
        for (int64_t j = 0; j < n; ++j) {
            double s = 0.0;
            for (int64_t p = 0; p < k; ++p) {
                s += A(i, p) * B(p, j);
            }
            c[i * ldc + j] = alpha * s + beta * c[i * ldc + j];
        }
    }
    return c;
}

}  // namespace

TEST(Gemm, Identity3x3) {
    std::vector<double> I = {1, 0, 0, 0, 1, 0, 0, 0, 1};
    std::vector<double> b = {1, 2, 3, 4, 5, 6, 7, 8, 9};
    std::vector<double> c(9, 0.0);
    int rc = hartonomous_gemm_f64(0, 0, 3, 3, 3, 1.0, I.data(), 3, b.data(), 3, 0.0, c.data(), 3);
    ASSERT_EQ(rc, 0);
    for (size_t i = 0; i < 9; ++i) {
        EXPECT_DOUBLE_EQ(c[i], b[i]);
    }
}

TEST(Gemm, TransposeAndScale) {
    // C = 2 · A^T · B, A is 2×3, B is 2×4, result is 3×4.
    std::vector<double> a = {1, 2, 3, 4, 5, 6};     // 2×3 row-major
    std::vector<double> b = {7, 8, 9, 10, 11, 12, 13, 14}; // 2×4 row-major
    std::vector<double> c(12, 0.0);
    int rc = hartonomous_gemm_f64(1, 0, 3, 4, 2, 2.0, a.data(), 3, b.data(), 4, 0.0, c.data(), 4);
    ASSERT_EQ(rc, 0);
    auto ref = Reference(1, 0, 3, 4, 2, 2.0, a, 3, b, 4, 0.0, std::vector<double>(12, 0.0), 4);
    for (size_t i = 0; i < 12; ++i) {
        EXPECT_NEAR(c[i], ref[i], 1e-12);
    }
}

TEST(Gemm, BetaAccumulates) {
    std::vector<double> a = {1, 1, 1, 1};   // 2×2
    std::vector<double> b = {2, 2, 2, 2};   // 2×2
    std::vector<double> c = {10, 20, 30, 40};
    int rc = hartonomous_gemm_f64(0, 0, 2, 2, 2, 1.0, a.data(), 2, b.data(), 2, 0.5, c.data(), 2);
    ASSERT_EQ(rc, 0);
    // A·B = [[4,4],[4,4]]; c = 1·AB + 0.5·c_old = [4+5, 4+10, 4+15, 4+20] = [9, 14, 19, 24]
    EXPECT_DOUBLE_EQ(c[0], 9.0);
    EXPECT_DOUBLE_EQ(c[1], 14.0);
    EXPECT_DOUBLE_EQ(c[2], 19.0);
    EXPECT_DOUBLE_EQ(c[3], 24.0);
}

TEST(Gemm, DeterminismAcrossRuns) {
    // Two GEMMs on identical random inputs must produce bit-identical output.
    // This verifies CBWR=AUTO,STRICT is in effect.
    std::mt19937_64 rng(12345);
    std::uniform_real_distribution<double> uni(-1.0, 1.0);
    const int64_t m = 128, n = 128, k = 64;
    std::vector<double> a(m * k), b(k * n);
    for (auto& x : a) x = uni(rng);
    for (auto& x : b) x = uni(rng);

    std::vector<double> c1(m * n, 0.0), c2(m * n, 0.0);
    ASSERT_EQ(0, hartonomous_gemm_f64(0, 0, m, n, k, 1.0, a.data(), k, b.data(), n, 0.0, c1.data(), n));
    ASSERT_EQ(0, hartonomous_gemm_f64(0, 0, m, n, k, 1.0, a.data(), k, b.data(), n, 0.0, c2.data(), n));
    for (int64_t i = 0; i < m * n; ++i) {
        ASSERT_EQ(c1[i], c2[i]) << "non-determinism at i=" << i;
    }
}

TEST(Gemm, RejectsNull) {
    double dummy = 0.0;
    EXPECT_EQ(-1, hartonomous_gemm_f64(0, 0, 1, 1, 1, 1.0, nullptr, 1, &dummy, 1, 0.0, &dummy, 1));
    EXPECT_EQ(-1, hartonomous_gemm_f64(0, 0, 1, 1, 1, 1.0, &dummy, 1, nullptr, 1, 0.0, &dummy, 1));
    EXPECT_EQ(-1, hartonomous_gemm_f64(0, 0, 1, 1, 1, 1.0, &dummy, 1, &dummy, 1, 0.0, nullptr, 1));
}

TEST(Gemm, RejectsBadShape) {
    double d = 0.0;
    EXPECT_EQ(-2, hartonomous_gemm_f64(0, 0, 0, 1, 1, 1.0, &d, 1, &d, 1, 0.0, &d, 1));
    EXPECT_EQ(-2, hartonomous_gemm_f64(2, 0, 1, 1, 1, 1.0, &d, 1, &d, 1, 0.0, &d, 1));
    EXPECT_EQ(-2, hartonomous_gemm_f64(0, 0, 1, 1, 1, 1.0, &d, 0, &d, 1, 0.0, &d, 1));
}

TEST(Gemm, RejectsMklIntOverflowBeforeCast) {
    double d = 0.0;
    EXPECT_EQ(-3, hartonomous_gemm_f64(
        0, 0,
        static_cast<int64_t>(std::numeric_limits<int>::max()) + 1, 1, 1,
        1.0,
        &d, 1,
        &d, 1,
        0.0,
        &d, 1));
}

TEST(Gemm, KnnRepresentativeSize) {
    // Mirrors the exact GEMM shape that knn.c issues for an EmbeddingFireflyPass
    // chunk: 64 × 384 × N where N is vocab size. Run two slices at MiniLM scale
    // (N = 4096) to catch any sizing regression before the crash path hits.
    const int64_t bs = 64, d = 384, N = 4096;
    std::mt19937_64 rng(0xC0FFEEULL);
    std::uniform_real_distribution<double> uni(-0.1, 0.1);
    std::vector<double> a(bs * d), b(N * d), c(bs * N, 0.0);
    for (auto& x : a) x = uni(rng);
    for (auto& x : b) x = uni(rng);
    int rc = hartonomous_gemm_f64(0, 1, bs, N, d, 1.0, a.data(), d, b.data(), d, 0.0, c.data(), N);
    ASSERT_EQ(rc, 0);
    // Sanity: at least one nonzero output.
    bool any = false;
    for (double x : c) { if (x != 0.0) { any = true; break; } }
    EXPECT_TRUE(any);
}
