namespace Hartonomous.Core.Compute.Internal;

internal static class NativeError
{
    /// <summary>Translates the libhartonomous integer error contract into a typed exception.</summary>
    internal static void ThrowIfError(int code, string operation)
    {
        if (code == 0)
        {
            return;
        }
        switch (code)
        {
            case -1:
                throw new ComputeArgumentException($"{operation}: null argument");
            case -2:
                throw new ComputeArgumentException($"{operation}: invalid shape or size");
            case -3:
                throw new ComputeArgumentException($"{operation}: degenerate geometry (e.g. antipodal points on S³)");
            case -6:
                throw new ComputeConvergenceException($"{operation}: did not converge");
            case -8:
                throw new UnsupportedDtypeException($"{operation}: unsupported dtype");
            case -9:
                throw new ComputeAllocationException($"{operation}: native allocation failed");
            default:
                throw new ComputeException($"{operation}: native error {code}");
        }
    }
}
