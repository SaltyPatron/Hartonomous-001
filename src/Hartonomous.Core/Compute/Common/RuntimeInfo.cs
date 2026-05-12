using System;
using System.Runtime.InteropServices;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

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
