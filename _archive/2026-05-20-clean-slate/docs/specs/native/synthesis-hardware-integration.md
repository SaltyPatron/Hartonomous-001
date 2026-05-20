# Synthesis Compute Primitives — Hardware Integration Spec

**Status:** Reference for Phase C synthesizer implementation. Captures research findings on Intel oneAPI / oneMKL / Eigen / Spectra integration patterns for the per-layer-type synthesizers' math primitives.

**Authority:** Research dispatched 2026-05-09. Reference-grade integration guide for native C/C++ implementation of synthesis math in `ext/libhartonomous/`.

---

## Library landscape recommendation per primitive

| Synthesizer | Math primitive | Primary library | Fallback / cross-arch |
|---|---|---|---|
| AttentionQkv | Thin SVD on dense `R^{V × V}` consensus matrix (V up to 128k+) | `oneMKL dgesdd` (divide-and-conquer SVD) when V ≤ 32k; sparse SVD via Spectra `PartialSVDSolver` for larger | OpenBLAS `dgesdd` on ARM; Apple Accelerate on macOS |
| AttentionVo | Same as AttentionQkv | Same | Same |
| Ffn (Approach 1: direct + SVD compression) | Thin SVD; dense BLAS GEMM for construction | `oneMKL dgesdd` + `cblas_dgemm` | Eigen `BDCSVD` + `MatrixXd::operator*` (auto-routes to MKL via `EIGEN_USE_MKL_ALL`) |
| Ffn (Approach 2: per-dim Levenberg-Marquardt) | Nonlinear LSQ via LM | Eigen `LevenbergMarquardt` (header-only; uses MKL for inner BLAS calls) | Same |
| Embedding (centroid mode) | 4D centroid aggregate; 4D → H expansion | substrate `st_4d_centroid` (existing PG function); Eigen vectorized expansion | n/a |
| Embedding (shape-archetype mode) | Hausdorff / Fréchet on 4D point sets | substrate `st_4d_hausdorff_distance`, `st_4d_frechet_distance` (existing) | n/a |
| LmHead | PCA via Lanczos eigendecomposition on sparse covariance | Spectra `SymEigsSolver` with shift-invert mode; Eigen `SelfAdjointEigenSolver` for dense | OpenBLAS LAPACK on ARM |
| LayerNorm | Exact mean / sum; vector aggregates | Eigen vectorized reductions; trivial native loop | n/a |
| MoeRouter | Sparse projection with cluster-mapping | Eigen `SparseMatrix<double>` ops | n/a |
| MoeExpert | Per-expert FFN (reuse Ffn synthesizer) | See Ffn | See Ffn |
| LoRAAdapter | SVD truncation / zero-pad | `oneMKL dgesdd` for thin SVD | Eigen `BDCSVD` |

---

## Intel oneAPI integration

**Build dependency.** Install Intel oneAPI Base Toolkit (free, available via Intel installer or apt/yum/conda). Provides oneMKL, oneDNN, oneTBB, plus the Intel C++ compiler (icpx) which optimizes vectorization aggressively.

**CMake integration:**

```cmake
# In ext/libhartonomous/CMakeLists.txt:
find_package(MKL CONFIG REQUIRED)
target_link_libraries(libhartonomous PRIVATE MKL::MKL)

# Eigen with MKL backend (header-only Eigen, MKL acceleration)
target_compile_definitions(libhartonomous PRIVATE
    EIGEN_USE_MKL_ALL
    EIGEN_USE_LAPACKE_STRICT
)

# Spectra (header-only, requires Eigen)
include(FetchContent)
FetchContent_Declare(spectra
    GIT_REPOSITORY https://github.com/yixuan/spectra.git
    GIT_TAG v1.0.1)
FetchContent_MakeAvailable(spectra)
target_link_libraries(libhartonomous PRIVATE Spectra)
```

With `EIGEN_USE_MKL_ALL`, Eigen routes its dense linear algebra (SVD, eigendecomposition, GEMM, LU, QR) to oneMKL automatically. Sparse operations stay in Eigen's native sparse code paths.

**oneDNN consideration:** oneDNN is for forward/backward DL primitives (convolution, pooling, attention forward, RNN cells). Hartonomous synthesizers do INVERSE / SYNTHESIS work, which is closer to LAPACK / BLAS territory than DL primitives. **Recommendation:** use oneMKL for the synthesizer math; oneDNN may be useful for any forward-pass validation ("does this synthesized weight matrix produce sensible activations?") but isn't load-bearing for synthesis itself.

**Cross-architecture:**
- **x86_64 (Intel/AMD):** oneMKL native; AVX2/AVX512/AMX auto-detected and used.
- **ARM64 (Apple Silicon, AWS Graviton):** oneMKL not available. Use **OpenBLAS** + Eigen native code paths. Apple Silicon: **Apple Accelerate framework** is also an option for BLAS/LAPACK with NEON SIMD.
- **Build system handles both:** CMake `if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64")` to select MKL vs OpenBLAS.

**Docker images** (per user mention of having oneAPI Docker setup):
- `intel/oneapi-basekit:latest` — full oneAPI base toolkit including oneMKL, oneDNN, icpx
- `intel/oneapi-hpckit:latest` — adds HPC tools (MPI, etc.) — likely overkill for Hartonomous
- `intel/oneapi-runtime:latest` — runtime-only; smaller; for production deployment

For development: `intel/oneapi-basekit`. For Hartonomous deployment containers: `intel/oneapi-runtime` linked against the libhartonomous build.

---

## Eigen + Spectra integration patterns

**Eigen 3.4** (header-only, MIT-like license):
- `Eigen::MatrixXd` for dense matrices
- `Eigen::BDCSVD` for medium-size thin SVD (faster than `JacobiSVD` for matrices > ~32 dim)
- `Eigen::JacobiSVD` for small matrices (more accurate but O(N³) constant factor higher than BDC)
- `Eigen::SelfAdjointEigenSolver` for symmetric/Hermitian eigendecomposition (PCA on covariance matrices)
- `Eigen::SparseMatrix<double>` + `Eigen::SimplicialLDLT` / `Eigen::ConjugateGradient` / `Eigen::LeastSquaresConjugateGradient` for sparse linear algebra
- `Eigen::LevenbergMarquardt` (in unsupported/Eigen/NonLinearOptimization) for nonlinear LSQ

**Spectra 1.0+** (header-only, MPL2 license):
- `Spectra::SymEigsSolver` for sparse symmetric eigenvalue problems (Lanczos with implicit restart)
- `Spectra::GenEigsSolver` for general (non-symmetric) sparse eigenvalue problems
- `Spectra::PartialSVDSolver` for sparse SVD when V × V is too large for dense SVD
- Built on top of Eigen's sparse matrix types; integrates seamlessly

**Pattern: "use Eigen for everything; let MKL accelerate dense ops; use Spectra for sparse eigensolvers":**

```cpp
#include <Eigen/Dense>
#include <Eigen/Sparse>
#include <Eigen/Eigenvalues>
#include <Eigen/SVD>
#include <unsupported/Eigen/NonLinearOptimization>
#include <Spectra/SymEigsSolver.h>
#include <Spectra/MatOp/SparseSymMatProd.h>

// Dense thin SVD (auto-routes to MKL with EIGEN_USE_MKL_ALL):
Eigen::MatrixXd consensus_S = ...;
Eigen::BDCSVD<Eigen::MatrixXd> svd(consensus_S, Eigen::ComputeThinU | Eigen::ComputeThinV);
auto Q = svd.matrixU().leftCols(target_head_dim) * svd.singularValues().head(target_head_dim).cwiseSqrt().asDiagonal();
auto K = svd.matrixV().leftCols(target_head_dim) * svd.singularValues().head(target_head_dim).cwiseSqrt().asDiagonal();
```

**Pattern for sparse Lanczos PCA:**

```cpp
Eigen::SparseMatrix<double> covariance = ...;  // V × V sparse
Spectra::SparseSymMatProd<double> op(covariance);
Spectra::SymEigsSolver<Spectra::SparseSymMatProd<double>> eigs(op, target_pca_components, 2 * target_pca_components + 1);
eigs.init();
eigs.compute(Spectra::SortRule::LargestAlge);
Eigen::VectorXd eigenvalues = eigs.eigenvalues();
Eigen::MatrixXd eigenvectors = eigs.eigenvectors();
```

---

## Determinism guarantees

**Hartonomous Substrate Law #6:** byte-identical output across runs on same ISA class.

**oneMKL `MKL_CBWR=AUTO,STRICT`:** Conditional Bitwise Reproducibility flag. Forces oneMKL to use deterministic code paths even when faster non-deterministic options exist. Setting required:
```cpp
#include <mkl.h>
mkl_cbwr_set(MKL_CBWR_AUTO | MKL_CBWR_STRICT);
```
Or set environment variable `MKL_CBWR=AUTO,STRICT` before process start.

Same setting must be applied at libhartonomous startup; existing pattern is in `ext/libhartonomous/src/init.c`.

**Eigen determinism:**
- Default Eigen operations are deterministic (no randomization) on a given thread count
- `EIGEN_MAX_CPP_VER` ≥ 17 (C++17 features used)
- Multi-threaded Eigen ops via OpenMP — order of reductions matters for floating-point; ensure consistent thread count via `Eigen::setNbThreads(N)`
- `JacobiSVD` and `BDCSVD` are deterministic; `RandomizedSVD` (if used) requires seed

**Spectra determinism:**
- `SymEigsSolver` with deterministic initial vector → deterministic across runs
- `init(initial_vector)` overload allows passing a fixed seed-derived vector
- Default `init()` uses random initialization → MUST override with seeded vector for Hartonomous determinism

**Threading determinism:**
- oneTBB / OpenMP — set thread count at startup via `omp_set_num_threads(N)` or `tbb::global_control`
- Use a consistent `N` per environment (e.g., `cores/2` clamped per AP-24)
- Floating-point reduction order can vary by thread count; for substrate-state-affecting ops, use single-threaded code paths or deterministic-reduction patterns
- For SYNTHESIS (per spec §XI.2 — approximation permitted), threading non-determinism is acceptable; for INGEST (§XI.1 — strict), threading must be deterministic

---

## SIMD / hardware exploitation

**AVX2 (Haswell, 2013+):** 256-bit SIMD; broadly supported. oneMKL and Eigen auto-detect and use.

**AVX512 (Skylake-X, 2017+):** 512-bit SIMD; Intel Xeon and high-end consumer chips. oneMKL auto-detects; Eigen detects via `EIGEN_VECTORIZE_AVX512` define.

**Intel AMX (Advanced Matrix Extensions, Sapphire Rapids 2023+):** dedicated matrix multiplication units. oneMKL `cblas_*gemm` automatically uses AMX on supporting hardware. Massive speedup for the SVD inner loops on synthesizer math.

**Apple Silicon NEON:** Apple Accelerate framework auto-uses; OpenBLAS supports.

**Recommendation:** rely on oneMKL / Eigen / OpenBLAS auto-detection rather than hand-rolling SIMD. Code stays portable; library handles the per-arch optimization.

**Compiler flags** (in `ext/libhartonomous/CMakeLists.txt`):
```cmake
if(CMAKE_CXX_COMPILER_ID STREQUAL "IntelLLVM")
    target_compile_options(libhartonomous PRIVATE -O3 -xHost -qopenmp)
elseif(CMAKE_CXX_COMPILER_ID STREQUAL "GNU" OR CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
    target_compile_options(libhartonomous PRIVATE -O3 -march=native -fopenmp)
endif()
```

`-march=native` on dev machines uses the local CPU's full feature set; for distribution, target a specific microarchitecture (`-march=skylake-avx512` for AVX512 baseline, `-march=x86-64-v3` for AVX2 baseline) to support a hardware range.

---

## P/Invoke surface to C#

Pattern (existing in `src/Hartonomous.Core/Native/`):

```cpp
// In ext/libhartonomous/src/synthesis_attention_qkv.cpp:
extern "C" {
HARTONOMOUS_EXPORT int hartonomous_synthesize_attention_qkv(
    const double* consensus_matrix,    // input: V × V dense (or sparse via separate args)
    int64_t vocab_size,
    int64_t head_dim,
    double* out_Q,                      // output: V × head_dim
    double* out_K,                      // output: V × head_dim
    int64_t* out_coverage_count          // output: # of cells with non-zero values
);
}
```

C# binding:
```csharp
[DllImport(NativeLibraryName, EntryPoint = "hartonomous_synthesize_attention_qkv")]
private static extern int hartonomous_synthesize_attention_qkv(
    IntPtr consensus_matrix, long vocab_size, long head_dim,
    IntPtr out_Q, IntPtr out_K, out long out_coverage_count);
```

C# facade in `Hartonomous.Core.Compute.Common`:
```csharp
public sealed class AttentionQkvSynthesisOps : IAttentionQkvSynthesisOps
{
    public (double[] Q, double[] K, long Coverage) Synthesize(
        ReadOnlySpan<double> consensusMatrix, int vocabSize, int headDim)
    {
        // Marshal via NativeCompute pattern; call native function.
    }
}
```

Existing pattern reference: `src/Hartonomous.Core/Native/Blake3Native.cs` and `src/Hartonomous.Core/Native/TextDecomposeNative.cs`.

---

## Per-primitive recommendations summary

For each Phase C synthesizer, the recommended implementation primitive + library:

| Synthesizer | Algorithm | Library | C# entry point |
|---|---|---|---|
| AttentionQkvLayerSynthesizer | Thin SVD + half-singular-value distribution | oneMKL `dgesdd` via Eigen `BDCSVD` | `Compute.Synthesis.AttentionQkv` |
| AttentionVoLayerSynthesizer | Same | Same | `Compute.Synthesis.AttentionVo` |
| FfnLayerSynthesizer (Approach 1) | Direct construction + thin SVD compression | Eigen sparse + `BDCSVD` | `Compute.Synthesis.FfnDirect` |
| FfnLayerSynthesizer (Approach 2 alt) | Per-dim Levenberg-Marquardt | Eigen `LevenbergMarquardt` | `Compute.Synthesis.FfnLM` |
| EmbeddingLayerSynthesizer | 4D centroid + forward expansion | substrate.st_4d_centroid (existing PG); Eigen vectorized expand | `Compute.Synthesis.EmbeddingFromFireflies` |
| LmHeadLayerSynthesizer | Sparse Lanczos PCA | Spectra `SymEigsSolver` | `Compute.Synthesis.LmHeadPca` |
| LayerNormLayerSynthesizer | Vector mean | Eigen reduction (or trivial native loop) | `Compute.Synthesis.LayerNorm` |
| MoeRouterLayerSynthesizer | Sparse projection | Eigen sparse | `Compute.Synthesis.MoeRouter` |
| MoeExpertLayerSynthesizer | Per-expert FFN (reuse Approach 1) | Same as Ffn | `Compute.Synthesis.MoeExpert` |
| LoRAAdapterLayerSynthesizer | SVD truncate / zero-pad | Eigen `BDCSVD` | `Compute.Synthesis.LoRA` |

All implementations live in `ext/libhartonomous/src/synthesis_*.cpp`, exposed via `extern "C"` to `src/Hartonomous.Core/Native/SynthesisNative.cs`, and surfaced through the `Hartonomous.Core.Compute.Synthesis.*` C# facade.

---

## Cross-references

- [`embedding-synthesis-from-fireflies.md`](../recomposers/algorithms/embedding-synthesis-from-fireflies.md)
- [`ffn-kv-inversion.md`](../recomposers/algorithms/ffn-kv-inversion.md)
- [`lottery-ticket-foundations.md`](../recomposers/algorithms/lottery-ticket-foundations.md)
- [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §XI (three-tier determinism boundary)
- [`docs/specs/recomposers/synthesis-library.md`](../recomposers/synthesis-library.md) (per-synthesizer specifications)
- `ext/libhartonomous/CMakeLists.txt` (build configuration)
- `src/Hartonomous.Core/Native/` (existing P/Invoke pattern)
- `src/Hartonomous.Core/Compute/` (existing C# facade pattern)

## References

- Intel oneAPI Math Kernel Library Documentation. https://www.intel.com/content/www/us/en/docs/onemkl/
- Eigen 3 Documentation. http://eigen.tuxfamily.org/dox/
- Spectra: A C++ Library for Large-Scale Eigenvalue Problems. https://spectralib.org/
- Anderson, E., et al. (1999). *LAPACK Users' Guide* (3rd ed.). SIAM. (Underlying linear algebra reference for `dgesdd` etc.)
- Lehoucq, R. B., Sorensen, D. C., & Yang, C. (1998). *ARPACK Users' Guide: Solution of Large-Scale Eigenvalue Problems with Implicitly Restarted Arnoldi Methods*. SIAM. (Theoretical foundation for Spectra.)
