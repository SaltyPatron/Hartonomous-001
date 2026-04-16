#include <gtest/gtest.h>

#include <array>
#include <cmath>
#include <vector>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kPi = 3.14159265358979323846;

TEST(SuperFib, OutputOnUnitSphere)
{
    const double n = 4096.0;
    for (int i = 0; i < 4096; i += 37) {
        std::array<double, 2> p{(double)i, n};
        std::array<double, 4> out{};
        ASSERT_EQ(hartonomous_super_fibonacci(p.data(), 2, out.data()), 0);
        double ns = 0.0;
        for (int k = 0; k < 4; ++k) ns += out[k] * out[k];
        EXPECT_NEAR(ns, 1.0, 1e-12) << "i=" << i;
    }
}

TEST(SuperFib, Deterministic)
{
    std::array<double, 2> p{17.0, 1024.0};
    std::array<double, 4> a{}, b{};
    ASSERT_EQ(hartonomous_super_fibonacci(p.data(), 2, a.data()), 0);
    ASSERT_EQ(hartonomous_super_fibonacci(p.data(), 2, b.data()), 0);
    for (int k = 0; k < 4; ++k) EXPECT_DOUBLE_EQ(a[k], b[k]);
}

TEST(SuperFib, DistinctIndicesDistinctPoints)
{
    std::array<double, 2> p1{0.0, 1024.0};
    std::array<double, 2> p2{1.0, 1024.0};
    std::array<double, 4> a{}, b{};
    ASSERT_EQ(hartonomous_super_fibonacci(p1.data(), 2, a.data()), 0);
    ASSERT_EQ(hartonomous_super_fibonacci(p2.data(), 2, b.data()), 0);
    double dsq = 0.0;
    for (int k = 0; k < 4; ++k) dsq += (a[k] - b[k]) * (a[k] - b[k]);
    EXPECT_GT(dsq, 1e-6);
}

TEST(SuperFib, QuasiUniformCoverage)
{
    /* Mean geodesic distance to nearest neighbor should be small and positive
     * for a well-distributed lattice. */
    const int N = 256;
    std::vector<std::array<double, 4>> pts(N);
    for (int i = 0; i < N; ++i) {
        std::array<double, 2> p{(double)i, (double)N};
        ASSERT_EQ(hartonomous_super_fibonacci(p.data(), 2, pts[i].data()), 0);
    }
    double mean_nn = 0.0;
    for (int i = 0; i < N; ++i) {
        double best = kPi;
        for (int j = 0; j < N; ++j) {
            if (i == j) continue;
            double d = hartonomous_s3_distance(pts[i].data(), pts[j].data());
            if (d < best) best = d;
        }
        mean_nn += best;
    }
    mean_nn /= (double)N;
    /* On S^3 with N=256, nearest-neighbor geodesic mean is roughly
     * O(N^{-1/3}) ≈ 0.16. Verify within a loose range. */
    EXPECT_GT(mean_nn, 0.02);
    EXPECT_LT(mean_nn, 0.6);
}

TEST(SuperFib, RejectsBadArguments)
{
    std::array<double, 4> out{};
    std::array<double, 2> p{0.0, 1024.0};
    EXPECT_EQ(hartonomous_super_fibonacci(nullptr, 2, out.data()), -1);
    EXPECT_EQ(hartonomous_super_fibonacci(p.data(), 1, out.data()), -2);
    std::array<double, 2> bad_n{0.0, 0.0};
    EXPECT_EQ(hartonomous_super_fibonacci(bad_n.data(), 2, out.data()), -2);
    std::array<double, 2> bad_i{-1.0, 1024.0};
    EXPECT_EQ(hartonomous_super_fibonacci(bad_i.data(), 2, out.data()), -2);
    std::array<double, 2> bad_i2{1024.0, 1024.0};
    EXPECT_EQ(hartonomous_super_fibonacci(bad_i2.data(), 2, out.data()), -2);
}

}  // namespace
