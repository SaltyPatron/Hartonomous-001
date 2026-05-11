// test_knearest_exact.cc — exact k-nearest-neighbour query correctness.

#include <gtest/gtest.h>
#include "hartonomous.h"
#include <vector>
#include <cmath>
#include <algorithm>
#include <limits>

TEST(KnearestExact, TrivialSelfQueryReturnsSelfFirst) {
    // Corpus is just its own queries. Top-1 must be self at distance 0.
    std::vector<double> pts = {
        0.0, 0.0, 0.0,
        1.0, 0.0, 0.0,
        0.0, 1.0, 0.0,
        0.0, 0.0, 1.0,
    };
    const int64_t n = 4, d = 3, k = 1;
    std::vector<int64_t> idx(n * k);
    std::vector<double> dist(n * k);
    ASSERT_EQ(hartonomous_knearest_exact_f64(n, n, d, pts.data(), pts.data(), k, idx.data(), dist.data()), 0);
    for (int64_t q = 0; q < n; ++q) {
        EXPECT_EQ(idx[q], q);
        EXPECT_NEAR(dist[q], 0.0, 1e-12);
    }
}

TEST(KnearestExact, Top2OnLineReturnsNearestNeighbours) {
    // 5 points on a line at x = 0, 1, 2, 3, 4. Top-2 for each is self + immediate neighbour,
    // except endpoints which pick the only adjacent + next.
    std::vector<double> pts(5 * 1);
    for (int i = 0; i < 5; ++i) pts[i] = (double)i;
    const int64_t n = 5, d = 1, k = 2;
    std::vector<int64_t> idx(n * k);
    std::vector<double> dist(n * k);
    ASSERT_EQ(hartonomous_knearest_exact_f64(n, n, d, pts.data(), pts.data(), k, idx.data(), dist.data()), 0);
    // Query 0: nearest = 0 (d=0), 1 (d=1).
    EXPECT_EQ(idx[0], 0);
    EXPECT_EQ(idx[1], 1);
    // Query 2: nearest = 2 (d=0), then tie between 1 and 3 (both d=1) — tie-break idx asc → 1.
    EXPECT_EQ(idx[2 * k + 0], 2);
    EXPECT_EQ(idx[2 * k + 1], 1);
    EXPECT_NEAR(dist[2 * k + 0], 0.0, 1e-12);
    EXPECT_NEAR(dist[2 * k + 1], 1.0, 1e-12);
}

TEST(KnearestExact, CrossCorpusQuery) {
    // Queries and corpus differ.
    std::vector<double> corpus = {0, 0, 1, 0, 2, 0, 3, 0};  // 4 points at (0,0),(1,0),(2,0),(3,0)
    std::vector<double> queries = {0.6, 0, 2.2, 0};  // 2 queries
    const int64_t nq = 2, nc = 4, d = 2, k = 2;
    std::vector<int64_t> idx(nq * k);
    std::vector<double> dist(nq * k);
    ASSERT_EQ(hartonomous_knearest_exact_f64(nq, nc, d, queries.data(), corpus.data(), k, idx.data(), dist.data()), 0);
    // Query 0 (0.6, 0): nearest = 1 (d²=0.16), then 0 (d²=0.36).
    EXPECT_EQ(idx[0], 1);
    EXPECT_EQ(idx[1], 0);
    EXPECT_NEAR(dist[0], 0.16, 1e-12);
    EXPECT_NEAR(dist[1], 0.36, 1e-12);
    // Query 1 (2.2, 0): nearest = 2 (d²=0.04), then 3 (d²=0.64).
    EXPECT_EQ(idx[2], 2);
    EXPECT_EQ(idx[3], 3);
    EXPECT_NEAR(dist[2], 0.04, 1e-12);
    EXPECT_NEAR(dist[3], 0.64, 1e-12);
}

TEST(KnearestExact, DistancesAreNonNegativeAndAscending) {
    // Random-ish but fixed points, verify: distances nonnegative and ascending per row.
    std::vector<double> pts = {
         1.0,  2.0,  3.0,
        -1.0,  0.5, -2.0,
         0.0,  0.0,  0.0,
         3.0,  3.0,  3.0,
         4.0, -1.0,  2.0,
    };
    const int64_t n = 5, d = 3, k = 3;
    std::vector<int64_t> idx(n * k);
    std::vector<double> dist(n * k);
    ASSERT_EQ(hartonomous_knearest_exact_f64(n, n, d, pts.data(), pts.data(), k, idx.data(), dist.data()), 0);
    for (int64_t q = 0; q < n; ++q) {
        for (int64_t t = 0; t < k; ++t) {
            EXPECT_GE(dist[q * k + t], 0.0);
            if (t > 0) {
                EXPECT_GE(dist[q * k + t], dist[q * k + t - 1]);
            }
        }
    }
}

TEST(KnearestExact, DeterministicAcrossRuns) {
    std::vector<double> pts;
    for (int i = 0; i < 60; ++i) pts.push_back(std::sin(0.3 * i) + 0.1 * i);
    const int64_t n = 20, d = 3, k = 5;
    std::vector<int64_t> i1(n * k), i2(n * k);
    std::vector<double> d1(n * k), d2(n * k);
    ASSERT_EQ(hartonomous_knearest_exact_f64(n, n, d, pts.data(), pts.data(), k, i1.data(), d1.data()), 0);
    ASSERT_EQ(hartonomous_knearest_exact_f64(n, n, d, pts.data(), pts.data(), k, i2.data(), d2.data()), 0);
    for (int64_t t = 0; t < n * k; ++t) {
        EXPECT_EQ(i1[t], i2[t]);
        EXPECT_EQ(d1[t], d2[t]);
    }
}

TEST(KnearestExact, RejectsBadArgs) {
    std::vector<double> pts(6, 0.0);
    std::vector<int64_t> idx(4);
    std::vector<double> dist(4);
    EXPECT_EQ(hartonomous_knearest_exact_f64(2, 3, 2, nullptr, pts.data(), 2, idx.data(), dist.data()), -1);
    EXPECT_EQ(hartonomous_knearest_exact_f64(2, 3, 2, pts.data(), nullptr, 2, idx.data(), dist.data()), -1);
    EXPECT_EQ(hartonomous_knearest_exact_f64(0, 3, 2, pts.data(), pts.data(), 2, idx.data(), dist.data()), -2);
    EXPECT_EQ(hartonomous_knearest_exact_f64(2, 3, 2, pts.data(), pts.data(), 0, idx.data(), dist.data()), -2);
    EXPECT_EQ(hartonomous_knearest_exact_f64(2, 3, 2, pts.data(), pts.data(), 5, idx.data(), dist.data()), -2);  // k > nc
}

TEST(KnearestExact, RejectsOutputShapeOverflowBeforeAllocation) {
    double point = 0.0;
    int64_t idx = 0;
    double dist = 0.0;

    EXPECT_EQ(hartonomous_knearest_exact_f64(
        std::numeric_limits<int64_t>::max() / 2,
        4,
        4,
        &point,
        &point,
        4,
        &idx,
        &dist),
        -3);
}
