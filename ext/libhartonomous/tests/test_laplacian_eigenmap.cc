// test_laplacian_eigenmap.cc — spectral correctness and determinism for
// the normalized-Laplacian eigenmap primitive.

#include <gtest/gtest.h>
#include "hartonomous.h"
#include <vector>
#include <cmath>
#include <algorithm>

namespace {
// Build full symmetric CSR from an edge list (undirected, duplicates both ways).
void edges_to_csr(
    int64_t n,
    const std::vector<std::tuple<int64_t, int64_t, double>>& edges,
    std::vector<int64_t>& row_ptr,
    std::vector<int64_t>& col_idx,
    std::vector<double>& values
) {
    std::vector<std::vector<std::pair<int64_t, double>>> rows(n);
    for (auto& [i, j, w] : edges) {
        rows[i].push_back({j, w});
        if (i != j) rows[j].push_back({i, w});
    }
    row_ptr.assign(n + 1, 0);
    for (int64_t i = 0; i < n; ++i) {
        std::sort(rows[i].begin(), rows[i].end(),
            [](auto& a, auto& b) { return a.first < b.first; });
        row_ptr[i + 1] = row_ptr[i] + (int64_t)rows[i].size();
    }
    int64_t nnz = row_ptr[n];
    col_idx.resize(nnz);
    values.resize(nnz);
    int64_t p = 0;
    for (int64_t i = 0; i < n; ++i) {
        for (auto& [j, w] : rows[i]) {
            col_idx[p] = j;
            values[p] = w;
            ++p;
        }
    }
}
} // namespace

TEST(LaplacianEigenmap, TwoDisconnectedComponentsHaveTwoZeroEigenvalues) {
    // 6-node graph in two 3-cliques with no inter-component edges.
    // Normalized Laplacian has two zero eigenvalues (one per component).
    std::vector<std::tuple<int64_t, int64_t, double>> edges = {
        {0, 1, 1.0}, {0, 2, 1.0}, {1, 2, 1.0},
        {3, 4, 1.0}, {3, 5, 1.0}, {4, 5, 1.0},
    };
    std::vector<int64_t> row_ptr, col_idx;
    std::vector<double> values;
    edges_to_csr(6, edges, row_ptr, col_idx, values);

    const int64_t n = 6, k = 3;
    std::vector<double> evals(k);
    std::vector<double> evecs(k * n);
    int64_t iters = 0;

    int rc = hartonomous_laplacian_eigenmap_f64(
        n, (int64_t)col_idx.size(),
        row_ptr.data(), col_idx.data(), values.data(),
        k, /*max_iter=*/n, /*seed=*/42,
        evals.data(), evecs.data(), &iters);
    ASSERT_EQ(rc, 0);

    // First two eigenvalues ~ 0 (connected-component multiplicity).
    EXPECT_NEAR(evals[0], 0.0, 1e-9);
    EXPECT_NEAR(evals[1], 0.0, 1e-9);
    // Third eigenvalue strictly positive.
    EXPECT_GT(evals[2], 1e-6);
    // Ascending order.
    EXPECT_LE(evals[0], evals[1]);
    EXPECT_LE(evals[1], evals[2]);
}

TEST(LaplacianEigenmap, PathGraphSmallestEigenvalueIsZero) {
    // 5-node path: 0-1-2-3-4. Connected → λ₀ = 0.
    std::vector<std::tuple<int64_t, int64_t, double>> edges = {
        {0, 1, 1.0}, {1, 2, 1.0}, {2, 3, 1.0}, {3, 4, 1.0},
    };
    std::vector<int64_t> row_ptr, col_idx;
    std::vector<double> values;
    edges_to_csr(5, edges, row_ptr, col_idx, values);

    const int64_t n = 5, k = 3;
    std::vector<double> evals(k);
    std::vector<double> evecs(k * n);
    int64_t iters = 0;

    int rc = hartonomous_laplacian_eigenmap_f64(
        n, (int64_t)col_idx.size(),
        row_ptr.data(), col_idx.data(), values.data(),
        k, /*max_iter=*/n, /*seed=*/7,
        evals.data(), evecs.data(), &iters);
    ASSERT_EQ(rc, 0);
    EXPECT_NEAR(evals[0], 0.0, 1e-9);
    EXPECT_GT(evals[1], 0.0);
}

TEST(LaplacianEigenmap, DeterministicAcrossRuns) {
    // Path graph, same seed, two runs must produce bit-identical eigenvalues.
    std::vector<std::tuple<int64_t, int64_t, double>> edges = {
        {0, 1, 1.0}, {1, 2, 1.0}, {2, 3, 1.0}, {3, 4, 1.0},
        {0, 2, 0.5}, {2, 4, 0.5},
    };
    std::vector<int64_t> row_ptr, col_idx;
    std::vector<double> values;
    edges_to_csr(5, edges, row_ptr, col_idx, values);

    const int64_t n = 5, k = 3;
    std::vector<double> e1(k), e2(k);
    std::vector<double> v1(k * n), v2(k * n);
    int64_t it1 = 0, it2 = 0;

    ASSERT_EQ(hartonomous_laplacian_eigenmap_f64(
        n, (int64_t)col_idx.size(), row_ptr.data(), col_idx.data(), values.data(),
        k, n, 12345ULL, e1.data(), v1.data(), &it1), 0);
    ASSERT_EQ(hartonomous_laplacian_eigenmap_f64(
        n, (int64_t)col_idx.size(), row_ptr.data(), col_idx.data(), values.data(),
        k, n, 12345ULL, e2.data(), v2.data(), &it2), 0);

    for (int i = 0; i < k; ++i) EXPECT_EQ(e1[i], e2[i]);
    for (int i = 0; i < k * n; ++i) EXPECT_EQ(v1[i], v2[i]);
    EXPECT_EQ(it1, it2);
}

TEST(LaplacianEigenmap, RejectsBadArgs) {
    std::vector<int64_t> row_ptr = {0, 1, 2};
    std::vector<int64_t> col_idx = {1, 0};
    std::vector<double> values = {1.0, 1.0};
    std::vector<double> ev(2), vec(6);
    int64_t it = 0;

    // null
    EXPECT_EQ(hartonomous_laplacian_eigenmap_f64(
        2, 2, nullptr, col_idx.data(), values.data(), 1, 10, 1, ev.data(), vec.data(), &it), -1);
    // shape
    EXPECT_EQ(hartonomous_laplacian_eigenmap_f64(
        0, 2, row_ptr.data(), col_idx.data(), values.data(), 1, 10, 1, ev.data(), vec.data(), &it), -2);
    EXPECT_EQ(hartonomous_laplacian_eigenmap_f64(
        2, 2, row_ptr.data(), col_idx.data(), values.data(), 2, 10, 1, ev.data(), vec.data(), &it), -2);  // k >= n
    EXPECT_EQ(hartonomous_laplacian_eigenmap_f64(
        2, 2, row_ptr.data(), col_idx.data(), values.data(), 1, 1, 1, ev.data(), vec.data(), &it), -2);   // max_iter <= k

    // negative weight -> -3
    std::vector<double> bad_vals = {-1.0, -1.0};
    EXPECT_EQ(hartonomous_laplacian_eigenmap_f64(
        2, 2, row_ptr.data(), col_idx.data(), bad_vals.data(), 1, 10, 1, ev.data(), vec.data(), &it), -3);
}
