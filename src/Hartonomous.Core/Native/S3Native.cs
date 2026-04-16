using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

internal static partial class S3Native
{
    private const string Library = "hartonomous";

    [LibraryImport(Library, EntryPoint = "hartonomous_s3_distance")]
    internal static partial double S3Distance(ReadOnlySpan<double> p1, ReadOnlySpan<double> p2);

    [LibraryImport(Library, EntryPoint = "hartonomous_s3_centroid")]
    internal static partial int S3Centroid(ReadOnlySpan<double> points, nuint pointCount, Span<double> result);
}
