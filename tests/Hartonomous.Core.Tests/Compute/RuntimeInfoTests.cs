using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Introspection gate: native compute really is routing through MKL + OpenMP
/// with CBWR active. Without these tests, a scalar-fallback build or a
/// silently mis-linked MKL would still pass every correctness gate (because
/// correctness doesn't depend on which kernel computed it) — but we'd be
/// shipping a library that isn't doing what its name says.
/// </summary>
public sealed class RuntimeInfoTests
{
    [Fact]
    public void MklIsLinked()
    {
        RuntimeInfoSnapshot info = RuntimeInfo.Query();
        Assert.True(info.HasMkl, "MKL not linked — native library is running in scalar fallback");
    }

    [Fact]
    public void MklVersion_NonEmpty_LooksLikeMkl()
    {
        RuntimeInfoSnapshot info = RuntimeInfo.Query();
        Assert.False(string.IsNullOrWhiteSpace(info.MklVersion),
            "MKL version string empty — mkl_get_version_string failed");
        Assert.Contains("Intel", info.MklVersion);
    }

    [Fact]
    public void OpenMp_HasAtLeastOneThread()
    {
        RuntimeInfoSnapshot info = RuntimeInfo.Query();
        Assert.True(info.OmpMaxThreads >= 1,
            $"OpenMP reports {info.OmpMaxThreads} threads — runtime not linked");
    }

    [Fact]
    public void Mkl_HasAtLeastOneThread()
    {
        RuntimeInfoSnapshot info = RuntimeInfo.Query();
        Assert.True(info.MklMaxThreads >= 1,
            $"MKL reports {info.MklMaxThreads} threads — pool not initialized");
    }

    [Fact]
    public void Cbwr_IsActive_NonNegativeBranch()
    {
        // After any MKL GEMM the CBWR state must be settled. Force one, then
        // observe. mkl_cbwr_get returns the currently active branch — negative
        // is failure, non-negative is the resolved AUTO|STRICT branch.
        double[] a = [1.0, 2.0, 3.0, 4.0];
        double[] b = [1.0, 0.0, 0.0, 1.0];
        double[] c = new double[4];
        Hartonomous.Core.Compute.Ingestion.Gemm.F64(
            Hartonomous.Core.Compute.Ingestion.TransposeOp.None,
            Hartonomous.Core.Compute.Ingestion.TransposeOp.None,
            2, 2, 2, 1.0, a, 2, b, 2, 0.0, c, 2);

        RuntimeInfoSnapshot info = RuntimeInfo.Query();
        Assert.True(info.CbwrBranch >= 0,
            $"MKL CBWR branch resolved to {info.CbwrBranch} — not in AUTO|STRICT mode");
    }

    [Fact]
    public void EnvironmentIsMultiCore_MklOrOmpReflectsIt()
    {
        // On any realistic dev/CI box at least one of the two pools should see
        // >1 core. If BOTH are pinned to 1 we're silently serial — flag it.
        RuntimeInfoSnapshot info = RuntimeInfo.Query();
        int cores = System.Environment.ProcessorCount;
        if (cores <= 1) { return; }
        Assert.True(info.MklMaxThreads > 1 || info.OmpMaxThreads > 1,
            $"Both MKL and OpenMP capped at 1 thread on {cores}-core host — check env vars");
    }
}
