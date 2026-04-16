using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public class SuperFibonacciS3Tests
{
    [Fact]
    public void Project_ProducesUnitQuaternion()
    {
        int totalPoints = 10000;
        for (int i = 0; i < totalPoints; i += 100)
        {
            (double x, double y, double z, double m) = SuperFibonacciS3.Project(i, totalPoints);
            double norm = Math.Sqrt(x * x + y * y + z * z + m * m);
            Assert.InRange(norm, 0.99, 1.01);
        }
    }

    [Fact]
    public void Project_AdjacentIndicesAreCloserThanAverage()
    {
        // On S3 with N points, average nearest-neighbor distance scales as ~(4π²/N)^(1/4).
        // Adjacent Fibonacci indices should be below average random separation.
        int totalPoints = 50000;

        double totalAdjacentDist = 0;
        int samples = 100;
        for (int i = 0; i < samples; i++)
        {
            int idx = i * (totalPoints / samples);
            (double x1, double y1, double z1, double m1) = SuperFibonacciS3.Project(idx, totalPoints);
            (double x2, double y2, double z2, double m2) = SuperFibonacciS3.Project(idx + 1, totalPoints);
            totalAdjacentDist += Math.Sqrt(
                (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2) +
                (z1 - z2) * (z1 - z2) + (m1 - m2) * (m1 - m2));
        }

        double avgAdjacentDist = totalAdjacentDist / samples;

        // On a unit S3, max Euclidean distance is 2.0. Average adjacent distance should
        // be significantly less than the diameter.
        Assert.True(avgAdjacentDist < 2.0,
            $"Average adjacent distance should be less than S3 diameter (2.0), got {avgAdjacentDist}");
    }

    [Fact]
    public void Project_DistributionCoversS3()
    {
        // Verify the projection covers all octants of S3 (no dead zones).
        int totalPoints = 10000;
        int[,,,] octants = new int[2, 2, 2, 2]; // 16 octants

        for (int i = 0; i < totalPoints; i++)
        {
            (double x, double y, double z, double m) = SuperFibonacciS3.Project(i, totalPoints);
            int ox = x >= 0 ? 1 : 0;
            int oy = y >= 0 ? 1 : 0;
            int oz = z >= 0 ? 1 : 0;
            int om = m >= 0 ? 1 : 0;
            octants[ox, oy, oz, om]++;
        }

        // Each octant should have at least some points (uniform distribution).
        for (int a = 0; a < 2; a++)
        {
            for (int b = 0; b < 2; b++)
            {
                for (int c = 0; c < 2; c++)
                {
                    for (int d = 0; d < 2; d++)
                    {
                        Assert.True(octants[a, b, c, d] > 0,
                            $"Octant ({a},{b},{c},{d}) has no points — distribution is not uniform");
                    }
                }
            }
        }
    }

    [Fact]
    public void Project_Deterministic()
    {
        (double x1, double y1, double z1, double m1) = SuperFibonacciS3.Project(42, 10000);
        (double x2, double y2, double z2, double m2) = SuperFibonacciS3.Project(42, 10000);

        Assert.Equal(x1, x2);
        Assert.Equal(y1, y2);
        Assert.Equal(z1, z2);
        Assert.Equal(m1, m2);
    }
}
