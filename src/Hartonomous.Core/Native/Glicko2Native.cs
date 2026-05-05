using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

/// <summary>
/// P/Invoke binding for libhartonomous Glicko-2 bulk update.
///
/// The canonical Glicko-2 formula lives in C
/// (<c>ext/libhartonomous/src/glicko_bulk.c</c>) — Glickman 2012, Tau=0.5,
/// epsilon=1e-6, Illinois variant of regula falsi for the volatility step.
/// Same inputs → bit-identical outputs across repeated runs (Law #6).
///
/// The PostgreSQL side calls the same C function via the
/// <c>public.glicko2_bulk_update(...)</c> SQL function (wrapped in
/// <c>ext/hartonomous_pg/src/pg_glicko_bulk.c</c>). Both
/// <c>substrate.record_comparison</c> and <c>substrate.record_outcome</c>
/// route through that wrapper. The C# managed reference at
/// <c>Hartonomous.Core.Compute.Common.Glicko2</c> is test-only — production
/// C# paths that need a Glicko-2 update should call this binding.
///
/// Each input row is one independent (player vs single opponent) update.
/// Multi-opponent aggregation (Glicko-2 paper's general Update with N
/// opponents) is not supported by this bulk API — for a 1:1 winner/loser
/// pair, pass n=2 with the two perspectives as separate rows.
/// </summary>
public static partial class Glicko2Native
{
    private const string Library = "hartonomous";

    /// <summary>
    /// Per-row independent Glicko-2 update. All input/output arrays must have
    /// length <paramref name="n"/>. Returns 0 on success, -1 on null arg, -2
    /// on n &lt; 0.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_glicko2_bulk_update")]
    public static partial int Glicko2BulkUpdate(
        long n,
        ReadOnlySpan<double> mu,
        ReadOnlySpan<double> sigma,
        ReadOnlySpan<double> volatility,
        ReadOnlySpan<double> oppMu,
        ReadOnlySpan<double> oppSigma,
        ReadOnlySpan<double> score,
        Span<double> newMu,
        Span<double> newSigma,
        Span<double> newVolatility);
}
