using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public class PointZMWkbTests
{
    [Fact]
    public void PointZMToWkb_ProducesCorrectLength()
    {
        byte[] wkb = UcdUcaDecomposer.PointZMToWkb(1.0, 2.0, 3.0, 4.0);
        Assert.Equal(37, wkb.Length);
    }

    [Fact]
    public void PointZMToWkb_LittleEndianByteOrder()
    {
        byte[] wkb = UcdUcaDecomposer.PointZMToWkb(0, 0, 0, 0);
        Assert.Equal(1, wkb[0]); // little-endian
    }

    [Fact]
    public void PointZMToWkb_CorrectTypeFlag()
    {
        byte[] wkb = UcdUcaDecomposer.PointZMToWkb(0, 0, 0, 0);
        uint wkbType = BitConverter.ToUInt32(wkb, 1);
        Assert.Equal(0xC0000001u, wkbType); // PointZM
    }

    [Fact]
    public void PointZMToWkb_EncodesCoordinatesCorrectly()
    {
        double x = 0.5, y = -0.3, z = 0.7, m = 0.1;
        byte[] wkb = UcdUcaDecomposer.PointZMToWkb(x, y, z, m);

        Assert.Equal(x, BitConverter.ToDouble(wkb, 5));
        Assert.Equal(y, BitConverter.ToDouble(wkb, 13));
        Assert.Equal(z, BitConverter.ToDouble(wkb, 21));
        Assert.Equal(m, BitConverter.ToDouble(wkb, 29));
    }
}
