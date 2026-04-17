using System.Text;
using Hartonomous.Core.Text.Segmentation;
using Xunit;

namespace Hartonomous.Core.Tests.Text;

public class LineBreaksTests
{
    [Fact]
    public void Enumerate_empty_input_yields_single_mandatory()
    {
        List<LineBreakOpportunity> result = LineBreaks.Enumerate(ReadOnlySpan<byte>.Empty, new FakeCodepointProperties());
        Assert.Single(result);
        Assert.Equal(LineBreakClass.Mandatory, result[0].Class);
        Assert.Equal(0, result[0].ByteOffset);
    }

    [Fact]
    public void Enumerate_ascii_ends_with_mandatory_break_at_length()
    {
        byte[] input = Encoding.UTF8.GetBytes("hi");
        FakeCodepointProperties props = SetupAscii();

        List<LineBreakOpportunity> breaks = LineBreaks.Enumerate(input, props);
        Assert.Equal(LineBreakClass.Mandatory, breaks[^1].Class);
        Assert.Equal(input.Length, breaks[^1].ByteOffset);
    }

    [Fact]
    public void Enumerate_space_permits_direct_break()
    {
        byte[] input = Encoding.UTF8.GetBytes("a b");
        FakeCodepointProperties props = SetupAscii();

        List<LineBreakOpportunity> breaks = LineBreaks.Enumerate(input, props);
        Assert.Contains(breaks, b => b.Class == LineBreakClass.Direct);
    }

    [Fact]
    public void Enumerate_mandatory_break_on_cr_lf()
    {
        byte[] input = new byte[] { 0x61, 0x0D, 0x0A, 0x62 };
        FakeCodepointProperties props = SetupAscii()
            .WithLb(0x0D, LineBreak.CR)
            .WithLb(0x0A, LineBreak.LF);

        List<LineBreakOpportunity> breaks = LineBreaks.Enumerate(input, props);
        Assert.Contains(breaks, b => b.Class == LineBreakClass.Mandatory && b.ByteOffset < input.Length);
    }

    private static FakeCodepointProperties SetupAscii()
    {
        FakeCodepointProperties p = new();
        for (char c = 'a'; c <= 'z'; c++)
        {
            p.WithLb(c, LineBreak.AL);
        }
        p.WithLb(' ', LineBreak.SP);
        return p;
    }
}
