#include <cmath>
#include <cstdint>
#include <random>
#include <vector>

#include <gtest/gtest.h>
#include "hartonomous.h"

namespace {

double Dot(const double* a, const double* b, int64_t n) {
    double s = 0.0;
    for (int64_t i = 0; i < n; ++i) s += a[i] * b[i];
    return s;
}

}  // namespace

TEST(GramSchmidt, RejectsBadArgs) {
    EXPECT_EQ(-1, hartonomous_gram_schmidt_f64(2, 2, nullptr, 2));
    double v = 0.0;
    EXPECT_EQ(-2, hartonomous_gram_schmidt_f64(0, 2, &v, 2));
    EXPECT_EQ(-2, hartonomous_gram_schmidt_f64(2, 2, &v, 1));  // ld < n
}

TEST(GramSchmidt, AlreadyOrthonormal) {
    // Two orthonormal rows — output should be (up to sign) itself.
    std::vector<double> v = {1, 0, 0, 1};
    int rc = hartonomous_gram_schmidt_f64(2, 2, v.data(), 2);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(Dot(&v[0], &v[0], 2), 1.0, 1e-12);
    EXPECT_NEAR(Dot(&v[2], &v[2], 2), 1.0, 1e-12);
    EXPECT_NEAR(Dot(&v[0], &v[2], 2), 0.0, 1e-12);
}

TEST(GramSchmidt, Orthonormalizes3Vectors) {
    const int64_t n = 8, k = 3;
    std::mt19937_64 rng(42);
    std::uniform_real_distribution<double> uni(-1.0, 1.0);
    std::vector<double> V(k * n);
    for (auto& x : V) x = uni(rng);

    int rc = hartonomous_gram_schmidt_f64(k, n, V.data(), n);
    ASSERT_EQ(rc, 0);

    for (int64_t i = 0; i < k; ++i) {
        EXPECT_NEAR(Dot(&V[i * n], &V[i * n], n), 1.0, 1e-10);
        for (int64_t j = i + 1; j < k; ++j) {
            EXPECT_NEAR(Dot(&V[i * n], &V[j * n], n), 0.0, 1e-10);
        }
    }
}

TEST(GramSchmidt, Determinism) {
    const int64_t n = 16, k = 5;
    std::mt19937_64 rng(0xF00DD00DULL);
    std::uniform_real_distribution<double> uni(-1.0, 1.0);
    std::vector<double> base(k * n);
    for (auto& x : base) x = uni(rng);

    std::vector<double> a = base, b = base;
    ASSERT_EQ(0, hartonomous_gram_schmidt_f64(k, n, a.data(), n));
    ASSERT_EQ(0, hartonomous_gram_schmidt_f64(k, n, b.data(), n));
    for (size_t i = 0; i < a.size(); ++i) EXPECT_EQ(a[i], b[i]);
}
