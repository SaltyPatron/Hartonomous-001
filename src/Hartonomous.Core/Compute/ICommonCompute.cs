using System;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Compute;

/// <summary>
/// Instance facade over the static Common.* compute primitives. Exists so passes
/// can take the facade by interface for DI / test isolation rather than depending
/// on global static class methods. Implementation delegates to the static classes
/// 1:1 — no logic, only indirection. Per docs/specs/csharp/compute-facade.md.
/// </summary>
public interface ICommonCompute
{
    int HashLen { get; }

    void Blake3(ReadOnlySpan<byte> input, Span<byte> output32);

    byte[] Blake3(ReadOnlySpan<byte> input);

    Blake3Hasher CreateBlake3Hasher();

    /// <summary>
    /// In-place modified Gram-Schmidt over <paramref name="basis"/>, treated as
    /// <paramref name="k"/> column vectors of length <paramref name="n"/> packed in
    /// column-major order (basis[col*n + row]).
    /// </summary>
    void GramSchmidtOrthonormalize(double[] basis, int k, int n);
}
