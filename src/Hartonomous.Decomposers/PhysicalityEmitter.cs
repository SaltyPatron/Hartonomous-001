using System;
using System.Collections.Generic;
using System.Text;

namespace Hartonomous.Decomposers;

/// <summary>
/// Shared physicality geometry for decomposers:
/// <list type="bullet">
///   <item>Super-Fibonacci S3 projection keyed by Unicode codepoint (universal frame).</item>
///   <item>WKB writers for POINTZM, LINESTRINGZM, MULTILINESTRINGZM.</item>
///   <item>Lemma/string → contour trajectory that threads codepoint S3 positions.</item>
/// </list>
/// Every codepoint in the Unicode space (0..0x10FFFF) has a deterministic S3 position. Higher-tier
/// entities (lemma, word_sense, synset, language_name, text_composition, bpe_token) are trajectories
/// through those positions in surface-form order.
/// </summary>
internal static class PhysicalityEmitter
{
    public const int UnicodeCodepointSpace = 0x110000;

    private const uint WkbPointZm = 0xC0000001u;
    private const uint WkbLineStringZm = 0xC0000002u;
    private const uint WkbMultiLineStringZm = 0xC0000005u;

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

    public static byte[] PointZmWkb(double x, double y, double z, double m)
    {
        byte[] wkb = new byte[37];
        wkb[0] = 1;
        BitConverter.TryWriteBytes(wkb.AsSpan(1), WkbPointZm);
        BitConverter.TryWriteBytes(wkb.AsSpan(5), x);
        BitConverter.TryWriteBytes(wkb.AsSpan(13), y);
        BitConverter.TryWriteBytes(wkb.AsSpan(21), z);
        BitConverter.TryWriteBytes(wkb.AsSpan(29), m);
        return wkb;
    }

    public static byte[] LineStringZmWkb(IReadOnlyList<(double X, double Y, double Z, double M)> points)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("LINESTRINGZM requires at least 2 vertices", nameof(points));
        }
        int totalBytes = 1 + 4 + 4 + points.Count * 32;
        byte[] wkb = new byte[totalBytes];
        wkb[0] = 1;
        BitConverter.TryWriteBytes(wkb.AsSpan(1), WkbLineStringZm);
        BitConverter.TryWriteBytes(wkb.AsSpan(5), (uint)points.Count);
        int offset = 9;
        foreach ((double x, double y, double z, double m) in points)
        {
            BitConverter.TryWriteBytes(wkb.AsSpan(offset), x);
            BitConverter.TryWriteBytes(wkb.AsSpan(offset + 8), y);
            BitConverter.TryWriteBytes(wkb.AsSpan(offset + 16), z);
            BitConverter.TryWriteBytes(wkb.AsSpan(offset + 24), m);
            offset += 32;
        }
        return wkb;
    }

    public static byte[] MultiLineStringZmWkb(IReadOnlyList<IReadOnlyList<(double X, double Y, double Z, double M)>> lineStrings)
    {
        if (lineStrings.Count == 0)
        {
            throw new ArgumentException("MULTILINESTRINGZM requires at least 1 linestring", nameof(lineStrings));
        }
        int totalBytes = 1 + 4 + 4;
        foreach (IReadOnlyList<(double, double, double, double)> ls in lineStrings)
        {
            totalBytes += 1 + 4 + 4 + ls.Count * 32;
        }
        byte[] wkb = new byte[totalBytes];
        wkb[0] = 1;
        BitConverter.TryWriteBytes(wkb.AsSpan(1), WkbMultiLineStringZm);
        BitConverter.TryWriteBytes(wkb.AsSpan(5), (uint)lineStrings.Count);
        int offset = 9;
        foreach (IReadOnlyList<(double X, double Y, double Z, double M)> ls in lineStrings)
        {
            wkb[offset] = 1;
            BitConverter.TryWriteBytes(wkb.AsSpan(offset + 1), WkbLineStringZm);
            BitConverter.TryWriteBytes(wkb.AsSpan(offset + 5), (uint)ls.Count);
            offset += 9;
            foreach ((double x, double y, double z, double m) in ls)
            {
                BitConverter.TryWriteBytes(wkb.AsSpan(offset), x);
                BitConverter.TryWriteBytes(wkb.AsSpan(offset + 8), y);
                BitConverter.TryWriteBytes(wkb.AsSpan(offset + 16), z);
                BitConverter.TryWriteBytes(wkb.AsSpan(offset + 24), m);
                offset += 32;
            }
        }
        return wkb;
    }

    /// <summary>
    /// Builds a LINESTRINGZM contour through the S3 positions of the codepoints that spell
    /// <paramref name="surfaceForm"/>. Returns null if the string has fewer than 2 codepoints
    /// (single-codepoint entities are points, not trajectories).
    /// </summary>
    public static byte[]? SurfaceFormContourWkb(string surfaceForm)
    {
        List<(double, double, double, double)> points = new(surfaceForm.Length);
        foreach (Rune rune in surfaceForm.EnumerateRunes())
        {
            points.Add(CodepointS3Position(rune.Value));
        }
        if (points.Count < 2)
        {
            return null;
        }
        return LineStringZmWkb(points);
    }

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
