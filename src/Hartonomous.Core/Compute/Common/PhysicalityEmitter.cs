using System;
using System.Collections.Generic;
using System.Text;
using Hartonomous.Core.Native;
using Hartonomous.Core.Text;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Shared physicality geometry for decomposers and recomposers:
/// <list type="bullet">
///   <item>Super-Fibonacci S³ projection by Super-Fib index — pure math primitive.</item>
///   <item>Per-codepoint S³ centroid lookup against the embedded UCD blob
///         (<c>hartonomous_ucd_cp_centroid</c>): <em>UCA-collation-rank ordered</em>,
///         so case/accent pairs cluster and substrate Law #6 holds across all callers.</item>
///   <item>Surface-form → ordered list of S³ vertices (each codepoint is one vertex).</item>
/// </list>
/// All codepoint-keyed centroids in the substrate (decomposers, prompt path, query path,
/// recomposer) MUST come through <see cref="CodepointS3Position"/> — which delegates to the
/// embedded blob — so that the C# physicality matches the substrate-side
/// <c>substrate.text_decompose</c> output byte-for-byte.
/// Higher-tier entities (lemma, word_sense, synset, language_name, text_composition, bpe_token)
/// are trajectories through those centroids in surface-form order; callers feed the vertex list to
/// <see cref="Hartonomous.Core.Ingestion.IIngestionBatch.AddPhysicalityLineString4d"/> for ≥2 vertices
/// or <see cref="Hartonomous.Core.Ingestion.IIngestionBatch.AddPhysicalityPoint4d"/> for a single vertex.
/// </summary>
public static class PhysicalityEmitter
{
    public const int UnicodeCodepointSpace = 0x110000;

    /// <summary>
    /// Native Super-Fibonacci S³ projection. Deterministic given (index, totalPoints).
    /// Used by callers that have their OWN ordering (e.g. embedding fireflies in
    /// concept space). For Unicode codepoints, callers MUST use
    /// <see cref="CodepointS3Position"/> instead — the embedded blob's UCA-rank
    /// ordering is the substrate-canonical projection.
    /// </summary>
    public static (double X, double Y, double Z, double M) SuperFibonacciS3(int index, int totalPoints)
    {
        Span<double> parameters = stackalloc double[] { index + 0.5, totalPoints };
        Span<double> result = stackalloc double[4];
        SuperFibonacci.Project(parameters, result);
        return (result[0], result[1], result[2], result[3]);
    }

    /// <summary>
    /// UCA-rank-ordered Super-Fibonacci S³ centroid for <paramref name="codepoint"/>.
    /// Reads the precomputed centroid out of the embedded UCD blob via
    /// <see cref="TextDecomposeNative.UcdCpCentroid"/>. Same value the
    /// substrate-side <c>substrate.text_decompose</c> emits, byte-for-byte.
    /// Throws <see cref="InvalidOperationException"/> if the blob has no
    /// centroid for the codepoint (out-of-range or unmapped).
    /// </summary>
    public static unsafe (double X, double Y, double Z, double M) CodepointS3Position(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        Span<double> buf = stackalloc double[4];
        int rc;
        fixed (double* p = buf)
        {
            rc = TextDecomposeNative.UcdCpCentroid(codepoint, p);
        }
        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"PhysicalityEmitter: embedded UCD blob has no centroid for codepoint U+{codepoint:X4}.");
        }
        return (buf[0], buf[1], buf[2], buf[3]);
    }

    /// <summary>
    /// Walks <paramref name="surfaceForm"/> as Unicode runes and returns the ordered list of
    /// embedded-blob S³ centroids for each codepoint.
    /// </summary>
    public static List<(double X, double Y, double Z, double M)> SurfaceFormVertices(string surfaceForm)
    {
        List<(double, double, double, double)> points = new(surfaceForm.Length);
        foreach (Rune rune in surfaceForm.EnumerateRunes())
        {
            points.Add(CodepointS3Position(rune.Value));
        }
        return points;
    }
}
