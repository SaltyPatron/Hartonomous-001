namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Snapshot of the native runtime's acceleration state at the moment of the
/// call. Lets callers assert MKL is linked, see which CBWR branch is active,
/// and observe the MKL/OpenMP thread pools.
/// </summary>
public readonly record struct RuntimeInfoSnapshot(
    bool HasMkl,
    string MklVersion,
    int MklMaxThreads,
    int OmpMaxThreads,
    int CbwrBranch);
