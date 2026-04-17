namespace Hartonomous.Core.Compute.Ingestion;

public sealed class KnnGraphF64
{
    public long NRows { get; }
    public long Nnz { get; }
    public long[] RowPtr { get; }
    public long[] ColIdx { get; }
    public double[] Values { get; }

    public KnnGraphF64(long nRows, long nnz, long[] rowPtr, long[] colIdx, double[] values)
    {
        NRows = nRows;
        Nnz = nnz;
        RowPtr = rowPtr;
        ColIdx = colIdx;
        Values = values;
    }
}
