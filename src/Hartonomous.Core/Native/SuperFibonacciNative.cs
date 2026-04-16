using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

internal static partial class SuperFibonacciNative
{
    private const string Library = "hartonomous";

    [LibraryImport(Library, EntryPoint = "hartonomous_super_fibonacci")]
    internal static partial int SuperFibonacci(ReadOnlySpan<double> parameters, nuint ndims, Span<double> result);
}
