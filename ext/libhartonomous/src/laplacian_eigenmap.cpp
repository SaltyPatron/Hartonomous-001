/*
 * laplacian_eigenmap.cpp — build the normalized symmetric Laplacian
 *   L_sym = I − D^{-1/2} · A · D^{-1/2}
 * from a caller-provided symmetric CSR adjacency and compute the k
 * smallest-algebraic eigenpairs of L_sym via Spectra's Lanczos on
 *   L_rev = (λ_max_bound · I) − L_sym
 * whose largest eigenvalues correspond (after a λ ↦ λ_max_bound − λ
 * flip) to the smallest of L_sym.
 *
 * λ_max_bound = 2 + EPS. The normalized Laplacian has spectrum ⊂ [0, 2]
 * so this transformation preserves the spectral gap at both ends and
 * keeps Lanczos converging on the large end where it is fastest.
 *
 * The trivial eigenvalue λ₀ ≈ 0 (with eigenvector ∝ √(diag(D))) is
 * included in the returned k eigenpairs — callers strip it if they
 * want only non-trivial modes.
 *
 * Inputs:
 *   n, nnz           — nodes and nonzeros of the FULL symmetric adjacency
 *                      A (both upper and lower stored; degree computed
 *                      by row sum).
 *   row_ptr, col_idx, values
 *                    — CSR of A (f64 weights).
 *   k                — eigenpairs to return (1 ≤ k < n). k smallest-algebraic.
 *   max_iter         — Lanczos iterations (Spectra convention: ncv ≥ 2k+1).
 *   seed             — deterministic seed for the starting vector.
 *
 * Outputs:
 *   out_eigenvalues  — length k, ascending (smallest first).
 *   out_eigenvectors — row-major k × n (each row is an eigenvector of L_sym
 *                      at the corresponding eigenvalue).
 *   out_iters        — Spectra's reported iteration count.
 *
 * Determinism. Starting vector is xorshift64* from the user seed. Spectra
 * deflation and Ritz extraction use only BLAS/LAPACK under MKL
 * CBWR=AUTO,STRICT. Same input + same seed → bit-identical output.
 */

#include "hartonomous.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <algorithm>

#include <Eigen/Sparse>
#include <Eigen/Dense>

#include <Spectra/SymEigsSolver.h>
#include <Spectra/MatOp/SparseSymMatProd.h>

extern "C" int hartonomous_laplacian_eigenmap_f64(
    int64_t n, int64_t nnz,
    const int64_t* row_ptr,
    const int64_t* col_idx,
    const double*  values,
    int64_t k,
    int64_t max_iter,
    uint64_t seed,
    double* out_eigenvalues,
    double* out_eigenvectors,
    int64_t* out_iters
) {
    if (row_ptr == nullptr || col_idx == nullptr || values == nullptr ||
        out_eigenvalues == nullptr || out_eigenvectors == nullptr ||
        out_iters == nullptr) {
        return -1;
    }
    if (n <= 0 || nnz < 0 || k <= 0 || k >= n) {
        return -2;
    }
    /* Spectra requires nev < ncv <= n. Use k+2 as the minimum ncv. */
    if (max_iter <= k) {
        return -2;
    }
    if (max_iter > n) {
        max_iter = n;
    }

    /* Build degree vector from CSR row sums. Validate nonnegativity. */
    std::vector<double> deg(static_cast<size_t>(n), 0.0);
    for (int64_t i = 0; i < n; ++i) {
        double s = 0.0;
        for (int64_t p = row_ptr[i]; p < row_ptr[i + 1]; ++p) {
            s += values[p];
        }
        if (s < 0.0) {
            return -3;
        }
        deg[static_cast<size_t>(i)] = s;
    }

    /* Build L_rev = c·I − L_sym   where   L_sym = I − D^{-1/2} A D^{-1/2}.
     * ⇒ L_rev = (c − 1)·I + D^{-1/2} A D^{-1/2} ,    c = 2 + 1e-12.
     * Entries:
     *   i == j : (c − 1) + values[p] / deg[i]                (deg[i] > 0 assumed; isolated nodes treated as 0)
     *   i != j : values[p] / sqrt(deg[i] · deg[j])
     * Isolated nodes (deg[i] == 0) contribute only the diagonal (c − 1).
     */
    const double c_shift = 2.0 + 1e-12;

    /* Pre-count per-row nnz to allocate Eigen triplet list once. */
    using Trip = Eigen::Triplet<double>;
    std::vector<Trip> triplets;
    triplets.reserve(static_cast<size_t>(nnz) + static_cast<size_t>(n));

    for (int64_t i = 0; i < n; ++i) {
        double di = deg[static_cast<size_t>(i)];
        double rsqrt_di = (di > 0.0) ? 1.0 / std::sqrt(di) : 0.0;
        bool saw_diag = false;
        for (int64_t p = row_ptr[i]; p < row_ptr[i + 1]; ++p) {
            int64_t j = col_idx[p];
            if (j < 0 || j >= n) {
                return -2;
            }
            double dj = deg[static_cast<size_t>(j)];
            double rsqrt_dj = (dj > 0.0) ? 1.0 / std::sqrt(dj) : 0.0;
            double norm_val = values[p] * rsqrt_di * rsqrt_dj;
            if (i == j) {
                /* Diagonal of A contributes (c_shift - 1) + norm_val. */
                triplets.emplace_back(static_cast<int>(i), static_cast<int>(j), (c_shift - 1.0) + norm_val);
                saw_diag = true;
            } else {
                triplets.emplace_back(static_cast<int>(i), static_cast<int>(j), norm_val);
            }
        }
        if (!saw_diag) {
            triplets.emplace_back(static_cast<int>(i), static_cast<int>(i), (c_shift - 1.0));
        }
    }

    Eigen::SparseMatrix<double> L_rev(static_cast<int>(n), static_cast<int>(n));
    L_rev.setFromTriplets(triplets.begin(), triplets.end());
    L_rev.makeCompressed();

    /* Spectra requires ncv > k. Pick ncv = min(max_iter, n). */
    int ncv = static_cast<int>(max_iter);
    int nev = static_cast<int>(k);

    Spectra::SparseSymMatProd<double> op(L_rev);
    Spectra::SymEigsSolver<Spectra::SparseSymMatProd<double>> eigs(op, nev, ncv);

    /* Deterministic initial vector from xorshift64*. */
    Eigen::VectorXd init(static_cast<int>(n));
    uint64_t state = seed != 0ULL ? seed : 0x9E3779B97F4A7C15ULL;
    for (int i = 0; i < static_cast<int>(n); ++i) {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        uint64_t v = state * 0x2545F4914F6CDD1DULL;
        /* map to (-1, 1) */
        double x = static_cast<double>(v) / static_cast<double>(UINT64_MAX);
        init[i] = 2.0 * x - 1.0;
    }
    eigs.init(init.data());

    int nconv = eigs.compute(Spectra::SortRule::LargestAlge, static_cast<int>(max_iter), 1e-12);
    *out_iters = eigs.num_iterations();

    if (eigs.info() != Spectra::CompInfo::Successful) {
        return -6;
    }
    if (nconv < nev) {
        return -6;
    }

    /* Spectra returns the LARGEST eigenpairs of L_rev, ordered descending by
     * algebraic value. Convert back to L_sym eigenvalues (λ = c_shift − μ)
     * and reverse so output is ascending. */
    Eigen::VectorXd evals_rev = eigs.eigenvalues();
    Eigen::MatrixXd evecs_rev = eigs.eigenvectors();  // n × nev

    /* Build (λ, index) list, sort ascending by λ. */
    struct Pair { double lam; int j; };
    std::vector<Pair> pairs;
    pairs.reserve(nev);
    for (int j = 0; j < nev; ++j) {
        pairs.push_back({ c_shift - evals_rev[j], j });
    }
    std::sort(pairs.begin(), pairs.end(), [](const Pair& a, const Pair& b) {
        if (a.lam != b.lam) return a.lam < b.lam;
        return a.j < b.j;  /* deterministic tie-break */
    });

    /* Copy out: out_eigenvectors is row-major k × n, so row r = eigenvector r. */
    for (int r = 0; r < nev; ++r) {
        out_eigenvalues[r] = pairs[r].lam;
        const int src_col = pairs[r].j;
        for (int i = 0; i < static_cast<int>(n); ++i) {
            out_eigenvectors[static_cast<size_t>(r) * static_cast<size_t>(n) + static_cast<size_t>(i)]
                = evecs_rev(i, src_col);
        }
    }

    return 0;
}
