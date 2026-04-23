using System;
using System.Collections.Generic;
using System.Text;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Shared physicality geometry for decomposers and recomposers:
/// <list type="bullet">
///   <item>Super-Fibonacci S³ projection keyed by Unicode codepoint (universal frame).</item>
///   <item>Surface-form → ordered list of S³ vertices (each codepoint is one vertex).</item>
/// </list>
/// Every codepoint in the Unicode space (0..0x10FFFF) has a deterministic S³ position. Higher-tier
/// entities (lemma, word_sense, synset, language_name, text_composition, bpe_token) are trajectories
/// through those positions in surface-form order; callers feed the vertex list to
/// <see cref="Hartonomous.Core.Ingestion.IIngestionBatch.AddPhysicalityLineString4d"/> for ≥2 vertices
/// or <see cref="Hartonomous.Core.Ingestion.IIngestionBatch.AddPhysicalityPoint4d"/> for a single vertex.
/// No PostGIS WKB is produced — the substrate-native point4d / linestring4d types own all 4D physicality.
/// </summary>
public static class PhysicalityEmitter
{
    public const int UnicodeCodepointSpace = 0x110000;

    private const double TwoPi = 2.0 * Math.PI;
    private const double Phi = 1.4142135623730951;
    private const double Psi = 1.533751168755204288118041;

    public static (double X, double Y, double Z, double M) SuperFibonacciS3(int index, int totalPoints)
    {
        double s = index + 0.5;
        double n = totalPoints;
        double r = Math.Sqrt(s / n);
        double bigR = Math.Sqrt(1.0 - s / n);
        double alpha = TwoPi * s / Phi;
        double beta = TwoPi * s / Psi;
        return (
            r * Math.Sin(alpha),
            r * Math.Cos(alpha),
            bigR * Math.Sin(beta),
            bigR * Math.Cos(beta));
    }

    public static (double X, double Y, double Z, double M) CodepointS3Position(int codepoint)
    {
        return SuperFibonacciS3(codepoint, UnicodeCodepointSpace);
    }

    /// <summary>
    /// Walks <paramref name="surfaceForm"/> as Unicode runes and returns the ordered list of
    /// S³ positions for each codepoint. Empty string returns an empty list.
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
