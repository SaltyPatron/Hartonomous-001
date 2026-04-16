namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Super-Fibonacci projection onto S3 (unit 3-sphere in 4D).
/// Produces evenly distributed points where UCA-adjacent codepoints are S3-adjacent.
/// </summary>
internal static class SuperFibonacciS3
{
    private const double TwoPi = 2.0 * Math.PI;
    private const double Phi = 1.4142135623730951; // sqrt(2)
    private const double Psi = 1.533751168755204288118041;

    public static (double X, double Y, double Z, double M) Project(int index, int totalPoints)
    {
        double s = index + 0.5;
        double n = totalPoints;
        double r = Math.Sqrt(s / n);
        double bigR = Math.Sqrt(1.0 - s / n);
        double alpha = TwoPi * s / Phi;
        double beta = TwoPi * s / Psi;

        return (
            X: r * Math.Sin(alpha),
            Y: r * Math.Cos(alpha),
            Z: bigR * Math.Sin(beta),
            M: bigR * Math.Cos(beta)
        );
    }
}
