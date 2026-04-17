using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Compute;

/// <summary>
/// Instance facade over the static Ingestion.* compute primitives — k-NN graph
/// construction, sparse symmetric eigensolve, dense GEMM, tensor decode. Used
/// by IModelAnalysisPass implementations and decomposer code; replaces direct
/// static-method calls so passes can be unit-tested with a fake compute facade.
/// </summary>
public interface IIngestionCompute
{
    KnnGraphF64 BuildKnnCosineGraphF64(int n, int d, double[] flat, int k);

    SparseEigsResult SparseSymEigsF64(
        int n, long nnz,
        long[] rowPtr, long[] colIdx, double[] values,
        int k, int maxIter,
        ulong seed,
        double[] eigvalsOut, double[] eigvecsColMajorOut);
}
