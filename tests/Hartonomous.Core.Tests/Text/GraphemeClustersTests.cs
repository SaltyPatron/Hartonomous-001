using System.Text;
using Hartonomous.Core.Text.Segmentation;
using Xunit;

namespace Hartonomous.Core.Tests.Text;

public class GraphemeClustersTests
{
    [Fact]
    public void Enumerate_empty_yields_no_ranges()
    {
        List<GraphemeRange> result = GraphemeClusters.Enumerate(ReadOnlySpan<byte>.Empty, new FakeCodepointProperties());
        Assert.Empty(result);
    }

    [Fact]
    public void Enumerate_pure_ascii_yields_one_cluster_per_codepoint()
    {
        byte[] input = Encoding.UTF8.GetBytes("hello");
        List<GraphemeRange> result = GraphemeClusters.Enumerate(input, new FakeCodepointProperties());
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Enumerate_cr_lf_is_one_cluster()
    {
        byte[] input = new byte[] { 0x0D, 0x0A };
        FakeCodepointProperties props = new FakeCodepointProperties()
            .WithGcb(0x0D, GraphemeBreak.CR)
            .WithGcb(0x0A, GraphemeBreak.LF);

        List<GraphemeRange> result = GraphemeClusters.Enumerate(input, props);
        Assert.Single(result);
    }

    [Fact]
    public void Enumerate_extend_attaches_to_preceding_cluster()
    {
        // A U+0041 + U+0301 (combining acute) = one cluster (á).
        byte[] input = new byte[] { 0x41, 0xCC, 0x81 };
        FakeCodepointProperties props = new FakeCodepointProperties()
            .WithGcb(0x0301, GraphemeBreak.Extend);

        List<GraphemeRange> result = GraphemeClusters.Enumerate(input, props);
        Assert.Single(result);
        Assert.Equal(3, result[0].ByteLength);
    }

    [Fact]
    public void Enumerate_regional_indicator_pair_is_one_cluster()
    {
        // Two RI codepoints = one flag grapheme.
        byte[] input = new byte[] { 0xF0, 0x9F, 0x87, 0xBA, 0xF0, 0x9F, 0x87, 0xB8 };
        FakeCodepointProperties props = new FakeCodepointProperties()
            .WithGcb(0x1F1FA, GraphemeBreak.RegionalIndicator)
            .WithGcb(0x1F1F8, GraphemeBreak.RegionalIndicator);

        List<GraphemeRange> result = GraphemeClusters.Enumerate(input, props);
        Assert.Single(result);
        Assert.Equal(8, result[0].ByteLength);
    }

    [Fact]
    public void Count_matches_enumerate_length()
    {
        byte[] input = Encoding.UTF8.GetBytes("abc def");
        FakeCodepointProperties props = new();

        long count = GraphemeClusters.Count(input, props);
        List<GraphemeRange> enumerated = GraphemeClusters.Enumerate(input, props);

        Assert.Equal(enumerated.Count, count);
    }
}
