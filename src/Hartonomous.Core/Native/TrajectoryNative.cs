using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

/// <summary>
/// P/Invoke bindings for libhartonomous's PostGIS WKB trajectory walker.
/// Parses LINESTRINGZM / MULTILINESTRINGZM EWKB byte streams and fires a
/// per-vertex callback. Pure C kernel — no SPI, no allocation. Used by
/// <see cref="Hartonomous.Core.Substrate.SubstrateTierWalker"/> to walk
/// composition trajectories pulled from <c>substrate.physicality.geom</c>
/// without managed-side WKB parsing.
///
/// <para>
/// Vertex coordinates are returned raw. Callers interpret per context:
/// composition <c>ingestion_trajectory</c> vertices carry mantissa-packed
/// identity (X = child hash bits 0..51, Y = ordinal + RLE, Z = child hash
/// bits 52..103, M = metadata — unpack via
/// <see cref="Hartonomous.Core.Compute.Common.MantissaPacking"/>); atom
/// physicality and edge geom vertices carry real metric coordinates.
/// </para>
/// </summary>
public static partial class TrajectoryNative
{
    private const string Library = "hartonomous";

    /// <summary>
    /// Per-vertex callback. Return 0 to continue; non-zero aborts the
    /// walk and propagates as the unpack function's return code.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int VertexCallback(
        IntPtr ctx,
        int subIdx,
        int vertexIdx,
        double x,
        double y,
        double z,
        double m);

    /// <summary>
    /// Walk a PostGIS WKB / EWKB <c>LINESTRINGZM</c> or
    /// <c>MULTILINESTRINGZM</c> byte stream and fire <paramref name="cb"/>
    /// once per vertex in trajectory order. Returns 0 on success, the
    /// callback's non-zero return code if aborted early, or -1 on parse
    /// error (truncated input, wrong dimensionality, unsupported
    /// geometry type).
    /// </summary>
#pragma warning disable CA1401 // P/Invoke method should not be visible
    [DllImport(Library, EntryPoint = "hartonomous_trajectory_unpack",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int Unpack(
        byte* wkb,
        nuint wkbLen,
        VertexCallback cb,
        IntPtr ctx);
#pragma warning restore CA1401
}
