using System;
using System.Runtime.InteropServices;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Snapshot of the native runtime's acceleration state at the moment of the
/// call. Lets callers assert MKL is linked, see which CBWR branch is active,
/// and observe the MKL/OpenMP thread pools. This is the introspection
/// surface used by boundary tests to prove that the expected native
/// acceleration is really what's running, not a silent scalar fallback.
/// </summary>
public readonly record struct RuntimeInfoSnapshot(
    bool HasMkl,
    string MklVersion,
    int MklMaxThreads,
    int OmpMaxThreads,
    int CbwrBranch);

public static class RuntimeInfo
{
    public static unsafe RuntimeInfoSnapshot Query()
    {
        NativeCompute.RuntimeInfoBlock block = default;
        NativeCompute.RuntimeInfo(&block);

        // NUL-terminated ASCII; Marshal.PtrToStringAnsi handles the strlen.
        string version = Marshal.PtrToStringAnsi((IntPtr)block.MklVersion)
            ?? string.Empty;

        return new RuntimeInfoSnapshot(
            HasMkl: block.HasMkl != 0,
            MklVersion: version,
            MklMaxThreads: block.MklMaxThreads,
            OmpMaxThreads: block.OmpMaxThreads,
            CbwrBranch: block.CbwrBranch);
    }
}
