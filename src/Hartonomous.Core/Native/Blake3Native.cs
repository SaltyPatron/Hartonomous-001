using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

/// <summary>
/// P/Invoke bindings for libhartonomous BLAKE3 functions.
/// Native library is expected at the platform's standard load path
/// (bin/ on Windows, LD_LIBRARY_PATH on Linux, DYLD_FALLBACK_LIBRARY_PATH on macOS).
/// </summary>
public static partial class Blake3Native
{
    public const int HashLen = 32;

    private const string Library = "hartonomous";

    [LibraryImport(Library, EntryPoint = "hartonomous_blake3")]
    public static partial void Blake3(
        ReadOnlySpan<byte> data,
        nuint len,
        Span<byte> outHash);
}
