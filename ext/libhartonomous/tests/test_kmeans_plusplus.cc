// test_kmeans_plusplus.cc
#include <gtest/gtest.h>
#include "hartonomous.h"
#include <vector>
#include <cmath>

TEST(KMeansPlusPlus, TwoWellSeparatedClusters) {
    // Cluster A near (0,0), cluster B near (10,10).
    std::vector<double> pts = {
        0.0, 0.0,  0.1, 0.0,  0.0, 0.1,  -0.1, 0.05,
        10.0, 10.0, 10.1, 10.0, 10.0, 10.1, 9.95, 9.95,
    };
    const int64_t n = 8, d = 2, k = 2;
    std::vector<int64_t> asg(n);
    std::vector<double> centers(k * d);
    int64_t iters = 0;

    int rc = hartonomous_kmeans_plusplus_f64(
        n, d, k, pts.data(), 50, 123, asg.data(), centers.data(), &iters);
    ASSERT_EQ(rc, 0);

    // The two clusters must be separated: asg[0..3] equal, asg[4..7] equal,
    // and the two groups differ.
    EXPECT_EQ(asg[0], asg[1]);
    EXPECT_EQ(asg[0], asg[2]);
    EXPECT_EQ(asg[0], asg[3]);
    EXPECT_EQ(asg[4], asg[5]);
    EXPECT_EQ(asg[4], asg[6]);
    EXPECT_EQ(asg[4], asg[7]);
    EXPECT_NE(asg[0], asg[4]);
}

TEST(KMeansPlusPlus, Deterministic) {
    std::vector<double> pts;
    for (int i = 0; i < 30; ++i) {
        pts.push_back(std::sin(0.3 * i));
        pts.push_back(std::cos(0.3 * i));
    }
    const int64_t n = 30, d = 2, k = 3;
    std::vector<int64_t> a1(n), a2(n);
    std::vector<double> c1(k * d), c2(k * d);
    int64_t it1 = 0, it2 = 0;

    ASSERT_EQ(hartonomous_kmeans_plusplus_f64(n, d, k, pts.data(), 100, 42, a1.data(), c1.data(), &it1), 0);
    ASSERT_EQ(hartonomous_kmeans_plusplus_f64(n, d, k, pts.data(), 100, 42, a2.data(), c2.data(), &it2), 0);

    for (int i = 0; i < n; ++i) EXPECT_EQ(a1[i], a2[i]);
    for (int i = 0; i < k * d; ++i) EXPECT_EQ(c1[i], c2[i]);
    EXPECT_EQ(it1, it2);
}

TEST(KMeansPlusPlus, DegenerateAllEqualPoints) {
    // All points identical — k-means++ should still return without crash,
    // clusters collapse but algorithm terminates with valid assignment.
    std::vector<double> pts(20, 1.0);  // 10 points in 2D all at (1,1)
    const int64_t n = 10, d = 2, k = 3;
    std::vector<int64_t> asg(n);
    std::vector<double> centers(k * d);
    int64_t iters = 0;

    int rc = hartonomous_kmeans_plusplus_f64(
        n, d, k, pts.data(), 10, 7, asg.data(), centers.data(), &iters);
    EXPECT_EQ(rc, 0);
    for (int64_t i = 0; i < n; ++i) {
        EXPECT_GE(asg[i], 0);
        EXPECT_LT(asg[i], k);
    }
}

TEST(KMeansPlusPlus, RejectsBadShape) {
    std::vector<double> pts = {0, 0, 1, 1};
    std::vector<int64_t> asg(2);
    std::vector<double> centers(2);
    int64_t it = 0;
    EXPECT_EQ(hartonomous_kmeans_plusplus_f64(0, 2, 1, pts.data(), 10, 1, asg.data(), centers.data(), &it), -2);
    EXPECT_EQ(hartonomous_kmeans_plusplus_f64(2, 2, 3, pts.data(), 10, 1, asg.data(), centers.data(), &it), -2);  // k > n
    EXPECT_EQ(hartonomous_kmeans_plusplus_f64(2, 2, 1, nullptr, 10, 1, asg.data(), centers.data(), &it), -1);
}
