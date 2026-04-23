// test_svd.cc — SVD correctness and determinism.
//
// Covers: 2×2 known-answer, m>n and m<n thin shapes, reconstruction
// A ≈ U·S·V^T, orthogonality of U and V, descending singular values,
// rejection of null/zero-shape args, bit-identical output across runs.

#include <gtest/gtest.h>
#include "hartonomous.h"
#include <cmath>
#include <vector>
#include <cstring>

namespace {

// Multiply C = A · B where A is m×k and B is k×n, all row-major.
void matmul(int64_t m, int64_t k, int64_t n,
            const double* a, const double* b, double* c) {
    for (int64_t i = 0; i < m; ++i) {
        for (int64_t j = 0; j < n; ++j) {
            double s = 0.0;
            for (int64_t t = 0; t < k; ++t) {
                s += a[i * k + t] * b[t * n + j];
            }
            c[i * n + j] = s;
        }
    }
}

// Compose U · diag(s) · V^T into `out` (m × n, row-major).
void reconstruct(int64_t m, int64_t n, int64_t kk,
                 const double* u, const double* s, const double* vt,
                 double* out) {
    std::vector<double> us(m * kk);
    for (int64_t i = 0; i < m; ++i) {
        for (int64_t j = 0; j < kk; ++j) {
            us[i * kk + j] = u[i * kk + j] * s[j];
        }
    }
    matmul(m, kk, n, us.data(), vt, out);
}

}  // namespace

TEST(SvdF64, TwoByTwoKnownSingularValues) {
    // A = [[3, 0], [0, 4]] → singular values (4, 3), U = [[0,1],[1,0]] up to sign,
    // V = I up to sign.
    double a[4] = {3.0, 0.0, 0.0, 4.0};
    double u[4], s[2], vt[4];
    int rc = hartonomous_svd_f64(2, 2, a, u, s, vt);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(s[0], 4.0, 1e-14);
    EXPECT_NEAR(s[1], 3.0, 1e-14);
}

TEST(SvdF64, ReconstructionMatchesOriginalSquare) {
    // 3x3 moderately conditioned matrix.
    double a[9] = {
        1.0, 2.0, 3.0,
        4.0, 5.0, 6.0,
        7.0, 8.0, 10.0
    };
    double a_save[9];
    std::memcpy(a_save, a, sizeof(a));
    double u[9], s[3], vt[9];
    ASSERT_EQ(0, hartonomous_svd_f64(3, 3, a, u, s, vt));

    double r[9];
    reconstruct(3, 3, 3, u, s, vt, r);
    for (int i = 0; i < 9; ++i) {
        EXPECT_NEAR(r[i], a_save[i], 1e-12) << "idx=" << i;
    }
    // Descending order.
    EXPECT_GE(s[0], s[1]);
    EXPECT_GE(s[1], s[2]);
    EXPECT_GE(s[2], 0.0);
    // Input preserved.
    for (int i = 0; i < 9; ++i) {
        EXPECT_DOUBLE_EQ(a[i], a_save[i]);
    }
}

TEST(SvdF64, ThinTallShapeReconstructs) {
    // 5×3 tall matrix. k=3. U is 5×3 with orthonormal cols, Vt is 3×3.
    const int64_t m = 5, n = 3, kk = 3;
    double a[] = {
        1.0,  0.0, -1.0,
        2.0,  1.0,  0.0,
        0.0,  3.0,  1.0,
       -1.0,  2.0,  4.0,
        1.0,  1.0,  1.0
    };
    double a_save[15];
    std::memcpy(a_save, a, sizeof(a));
    double u[m * kk], s[kk], vt[kk * n];
    ASSERT_EQ(0, hartonomous_svd_f64(m, n, a, u, s, vt));

    double r[m * n];
    reconstruct(m, n, kk, u, s, vt, r);
    for (int i = 0; i < m * n; ++i) {
        EXPECT_NEAR(r[i], a_save[i], 1e-12);
    }
    // U columns orthonormal: U^T · U = I_k.
    for (int64_t j1 = 0; j1 < kk; ++j1) {
        for (int64_t j2 = 0; j2 < kk; ++j2) {
            double d = 0.0;
            for (int64_t i = 0; i < m; ++i) {
                d += u[i * kk + j1] * u[i * kk + j2];
            }
            double expected = (j1 == j2) ? 1.0 : 0.0;
            EXPECT_NEAR(d, expected, 1e-12) << "UtU[" << j1 << "," << j2 << "]";
        }
    }
}

TEST(SvdF64, ThinWideShapeReconstructs) {
    // 2×5 wide matrix. k=2.
    const int64_t m = 2, n = 5, kk = 2;
    double a[] = {
        1.0, 2.0, 3.0, 4.0,  5.0,
        5.0, 4.0, 3.0, 2.0, -1.0
    };
    double a_save[10];
    std::memcpy(a_save, a, sizeof(a));
    double u[m * kk], s[kk], vt[kk * n];
    ASSERT_EQ(0, hartonomous_svd_f64(m, n, a, u, s, vt));
    double r[m * n];
    reconstruct(m, n, kk, u, s, vt, r);
    for (int i = 0; i < m * n; ++i) {
        EXPECT_NEAR(r[i], a_save[i], 1e-12);
    }
    // V rows (Vt rows) orthonormal: Vt · V = I_k.
    for (int64_t i1 = 0; i1 < kk; ++i1) {
        for (int64_t i2 = 0; i2 < kk; ++i2) {
            double d = 0.0;
            for (int64_t j = 0; j < n; ++j) {
                d += vt[i1 * n + j] * vt[i2 * n + j];
            }
            double expected = (i1 == i2) ? 1.0 : 0.0;
            EXPECT_NEAR(d, expected, 1e-12);
        }
    }
}

TEST(SvdF64, DeterministicAcrossRuns) {
    // Same input twice → bit-identical output.
    const int64_t m = 6, n = 4, kk = 4;
    double a[24];
    // Deterministic pseudo-random fill (no PRNG; pure arithmetic).
    for (int i = 0; i < 24; ++i) {
        double x = static_cast<double>(i + 1);
        a[i] = std::sin(x * 0.5) + 0.25 * std::cos(x * 1.3);
    }
    double u1[m * kk], s1[kk], vt1[kk * n];
    double u2[m * kk], s2[kk], vt2[kk * n];
    ASSERT_EQ(0, hartonomous_svd_f64(m, n, a, u1, s1, vt1));
    ASSERT_EQ(0, hartonomous_svd_f64(m, n, a, u2, s2, vt2));
    for (int i = 0; i < m * kk; ++i) EXPECT_DOUBLE_EQ(u1[i], u2[i]);
    for (int i = 0; i < kk; ++i)     EXPECT_DOUBLE_EQ(s1[i], s2[i]);
    for (int i = 0; i < kk * n; ++i) EXPECT_DOUBLE_EQ(vt1[i], vt2[i]);
}

TEST(SvdF64, RejectsNullArgs) {
    double a[4] = {1, 2, 3, 4};
    double u[4], s[2], vt[4];
    EXPECT_EQ(-1, hartonomous_svd_f64(2, 2, nullptr, u, s, vt));
    EXPECT_EQ(-1, hartonomous_svd_f64(2, 2, a, nullptr, s, vt));
    EXPECT_EQ(-1, hartonomous_svd_f64(2, 2, a, u, nullptr, vt));
    EXPECT_EQ(-1, hartonomous_svd_f64(2, 2, a, u, s, nullptr));
}

TEST(SvdF64, RejectsBadShape) {
    double a[1] = {1.0};
    double u[1], s[1], vt[1];
    EXPECT_EQ(-2, hartonomous_svd_f64(0, 1, a, u, s, vt));
    EXPECT_EQ(-2, hartonomous_svd_f64(1, 0, a, u, s, vt));
    EXPECT_EQ(-2, hartonomous_svd_f64(-1, 1, a, u, s, vt));
}

TEST(SvdF64, SingularValuesDescending) {
    const int64_t m = 4, n = 4, kk = 4;
    double a[16];
    for (int i = 0; i < 16; ++i) a[i] = std::cos(i * 0.7) + 1.5 * std::sin(i * 0.3);
    double u[16], s[4], vt[16];
    ASSERT_EQ(0, hartonomous_svd_f64(m, n, a, u, s, vt));
    for (int i = 1; i < kk; ++i) {
        EXPECT_GE(s[i - 1], s[i]) << "i=" << i;
    }
    for (int i = 0; i < kk; ++i) {
        EXPECT_GE(s[i], 0.0) << "i=" << i;
    }
}
