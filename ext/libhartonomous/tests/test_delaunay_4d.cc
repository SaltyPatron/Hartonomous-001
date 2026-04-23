// test_delaunay_4d.cc
#include <gtest/gtest.h>
#include "hartonomous.h"
#include <vector>
#include <set>
#include <array>
#include <cstdlib>
#include <cmath>

TEST(Delaunay4D, FivePointsYieldOneSimplex) {
    // A 4-simplex (5 points in general position) → exactly one simplex.
    std::vector<double> pts = {
        0, 0, 0, 0,
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };
    int64_t count = 0;
    int rc = hartonomous_delaunay_4d_f64(5, pts.data(), &count, nullptr, 0);
    ASSERT_EQ(rc, 0);
    EXPECT_EQ(count, 1);

    std::vector<int64_t> out(5 * count);
    rc = hartonomous_delaunay_4d_f64(5, pts.data(), &count, out.data(), count);
    ASSERT_EQ(rc, 0);
    std::set<int64_t> verts(out.begin(), out.end());
    EXPECT_EQ(verts.size(), 5u);
    EXPECT_EQ(*verts.begin(), 0);
    EXPECT_EQ(*verts.rbegin(), 4);
}

TEST(Delaunay4D, SixPointsProduceValidTriangulation) {
    // Six points in general position. The 6th splits one 4-simplex into
    // several. Exact count depends on geometry but must be > 1.
    std::vector<double> pts = {
        0, 0, 0, 0,
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
        0.3, 0.3, 0.3, 0.3,
    };
    int64_t count = 0;
    ASSERT_EQ(hartonomous_delaunay_4d_f64(6, pts.data(), &count, nullptr, 0), 0);
    EXPECT_GE(count, 2);

    std::vector<int64_t> out(5 * count);
    ASSERT_EQ(hartonomous_delaunay_4d_f64(6, pts.data(), &count, out.data(), count), 0);

    // Every returned index is in [0, n).
    for (int64_t v : out) {
        EXPECT_GE(v, 0);
        EXPECT_LT(v, 6);
    }
    // Each simplex has 5 distinct vertices.
    for (int64_t i = 0; i < count; ++i) {
        std::set<int64_t> s(out.begin() + i * 5, out.begin() + (i + 1) * 5);
        EXPECT_EQ(s.size(), 5u);
    }
}

TEST(Delaunay4D, Deterministic) {
    // Same input → bit-identical output ordering.
    std::vector<double> pts;
    std::srand(1234);
    for (int i = 0; i < 12; ++i) {
        for (int j = 0; j < 4; ++j) {
            pts.push_back(0.001 * (std::rand() % 1000));
        }
    }
    int64_t c1 = 0, c2 = 0;
    ASSERT_EQ(hartonomous_delaunay_4d_f64(12, pts.data(), &c1, nullptr, 0), 0);
    ASSERT_EQ(hartonomous_delaunay_4d_f64(12, pts.data(), &c2, nullptr, 0), 0);
    EXPECT_EQ(c1, c2);
    EXPECT_GT(c1, 0);

    std::vector<int64_t> a(5 * c1), b(5 * c2);
    ASSERT_EQ(hartonomous_delaunay_4d_f64(12, pts.data(), &c1, a.data(), c1), 0);
    ASSERT_EQ(hartonomous_delaunay_4d_f64(12, pts.data(), &c2, b.data(), c2), 0);
    for (int64_t i = 0; i < 5 * c1; ++i) EXPECT_EQ(a[i], b[i]);
}

TEST(Delaunay4D, RejectsTooFewPoints) {
    std::vector<double> pts(16, 0.0);
    int64_t count = 0;
    EXPECT_EQ(hartonomous_delaunay_4d_f64(4, pts.data(), &count, nullptr, 0), -2);
    EXPECT_EQ(hartonomous_delaunay_4d_f64(5, nullptr,    &count, nullptr, 0), -1);
}
