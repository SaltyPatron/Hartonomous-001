#include <gtest/gtest.h>

#include <array>

extern "C" {
#include "hartonomous.h"
}

namespace {

constexpr double kTol = 1e-12;

TEST(Box4d, InitFromPointMakesDegenerate)
{
    std::array<double, 4> p{1.0, 2.0, 3.0, 4.0};
    std::array<double, 8> box{};
    hartonomous_bbox_init_point(p.data(), box.data());
    for (int i = 0; i < 4; ++i) {
        EXPECT_NEAR(box[i], p[i], kTol);
        EXPECT_NEAR(box[i + 4], p[i], kTol);
    }
}

TEST(Box4d, ExpandPointGrowsBox)
{
    std::array<double, 4> p1{0.0, 0.0, 0.0, 0.0};
    std::array<double, 4> p2{1.0, -1.0, 2.0, -2.0};
    std::array<double, 8> box{};
    hartonomous_bbox_init_point(p1.data(), box.data());
    hartonomous_bbox_expand_point(box.data(), p2.data());
    EXPECT_NEAR(box[0], 0.0, kTol);
    EXPECT_NEAR(box[1], -1.0, kTol);
    EXPECT_NEAR(box[2], 0.0, kTol);
    EXPECT_NEAR(box[3], -2.0, kTol);
    EXPECT_NEAR(box[4], 1.0, kTol);
    EXPECT_NEAR(box[5], 0.0, kTol);
    EXPECT_NEAR(box[6], 2.0, kTol);
    EXPECT_NEAR(box[7], 0.0, kTol);
}

TEST(Box4d, UnionSpansBoth)
{
    std::array<double, 8> a{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 8> b{2, 2, 2, 2, 3, 3, 3, 3};
    std::array<double, 8> out{};
    hartonomous_bbox_union(a.data(), b.data(), out.data());
    for (int i = 0; i < 4; ++i) {
        EXPECT_NEAR(out[i], 0.0, kTol);
        EXPECT_NEAR(out[i + 4], 3.0, kTol);
    }
}

TEST(Box4d, UnionInPlaceAliasing)
{
    std::array<double, 8> a{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 8> b{2, 2, 2, 2, 3, 3, 3, 3};
    hartonomous_bbox_union(a.data(), b.data(), a.data());  /* aliasing */
    EXPECT_NEAR(a[0], 0.0, kTol);
    EXPECT_NEAR(a[4], 3.0, kTol);
}

TEST(Box4d, OverlapsTrueWhenIntersecting)
{
    std::array<double, 8> a{0, 0, 0, 0, 2, 2, 2, 2};
    std::array<double, 8> b{1, 1, 1, 1, 3, 3, 3, 3};
    EXPECT_EQ(hartonomous_bbox_overlaps(a.data(), b.data()), 1);
}

TEST(Box4d, OverlapsFalseWhenSeparatedOnAnyAxis)
{
    std::array<double, 8> a{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 8> b{2, 0, 0, 0, 3, 1, 1, 1};  /* separated on axis 0 */
    EXPECT_EQ(hartonomous_bbox_overlaps(a.data(), b.data()), 0);
}

TEST(Box4d, OverlapsTouching)
{
    /* Closed intervals: shared face counts as overlap. */
    std::array<double, 8> a{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 8> b{1, 0, 0, 0, 2, 1, 1, 1};
    EXPECT_EQ(hartonomous_bbox_overlaps(a.data(), b.data()), 1);
}

TEST(Box4d, ContainsPoint)
{
    std::array<double, 8> box{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 4> inside{0.5, 0.5, 0.5, 0.5};
    std::array<double, 4> outside{1.5, 0.5, 0.5, 0.5};
    EXPECT_EQ(hartonomous_bbox_contains_point(box.data(), inside.data()), 1);
    EXPECT_EQ(hartonomous_bbox_contains_point(box.data(), outside.data()), 0);
}

TEST(Box4d, ContainsBox)
{
    std::array<double, 8> outer{0, 0, 0, 0, 10, 10, 10, 10};
    std::array<double, 8> inner{1, 2, 3, 4, 5, 6, 7, 8};
    std::array<double, 8> notInner{1, 2, 3, 4, 5, 6, 7, 11};  /* w-axis breaks */
    EXPECT_EQ(hartonomous_bbox_contains_box(outer.data(), inner.data()), 1);
    EXPECT_EQ(hartonomous_bbox_contains_box(outer.data(), notInner.data()), 0);
}

TEST(Box4d, Equality)
{
    std::array<double, 8> a{1, 2, 3, 4, 5, 6, 7, 8};
    std::array<double, 8> b{1, 2, 3, 4, 5, 6, 7, 8};
    std::array<double, 8> c{1, 2, 3, 4, 5, 6, 7, 8.0001};
    EXPECT_EQ(hartonomous_bbox_equals(a.data(), b.data()), 1);
    EXPECT_EQ(hartonomous_bbox_equals(a.data(), c.data()), 0);
}

TEST(Box4d, Volume)
{
    std::array<double, 8> box{0, 0, 0, 0, 2, 3, 4, 5};
    EXPECT_NEAR(hartonomous_bbox_volume(box.data()), 2 * 3 * 4 * 5, kTol);
}

TEST(Box4d, MinDistanceZeroWhenInside)
{
    std::array<double, 8> box{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 4> p{0.5, 0.5, 0.5, 0.5};
    EXPECT_NEAR(hartonomous_bbox_min_distance_4d(box.data(), p.data()), 0.0, kTol);
}

TEST(Box4d, MinDistanceAxisAligned)
{
    std::array<double, 8> box{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 4> p{4.0, 0.5, 0.5, 0.5};  /* 3 units past x-max */
    EXPECT_NEAR(hartonomous_bbox_min_distance_4d(box.data(), p.data()), 3.0, kTol);
}

TEST(Box4d, MinDistanceMultiAxis)
{
    std::array<double, 8> box{0, 0, 0, 0, 1, 1, 1, 1};
    std::array<double, 4> p{4.0, 5.0, 0.5, 0.5};  /* 3 units in x, 4 in y */
    EXPECT_NEAR(hartonomous_bbox_min_distance_4d(box.data(), p.data()), 5.0, kTol);
}

}  // namespace
