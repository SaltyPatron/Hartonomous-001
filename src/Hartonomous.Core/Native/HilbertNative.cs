using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

internal static partial class HilbertNative
{
    private const string Library = "hartonomous";

    [LibraryImport(Library, EntryPoint = "hartonomous_hilbert_index")]
    internal static partial ulong HilbertIndex(ReadOnlySpan<double> point, int order);

    [LibraryImport(Library, EntryPoint = "hartonomous_hilbert_inverse")]
    internal static partial int HilbertInverse(ulong index, int order, Span<double> result);
}
