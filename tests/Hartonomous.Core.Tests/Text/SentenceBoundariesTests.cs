using System.Text;
using Hartonomous.Core.Text.Segmentation;
using Xunit;

namespace Hartonomous.Core.Tests.Text;

public class SentenceBoundariesTests
{
    [Fact]
    public void Enumerate_empty_yields_none()
    {
        List<SentenceRange> result = SentenceBoundaries.Enumerate(ReadOnlySpan<byte>.Empty, new FakeCodepointProperties());
        Assert.Empty(result);
    }

    [Fact]
    public void Enumerate_splits_at_period_space_upper()
    {
        byte[] input = Encoding.UTF8.GetBytes("Hello. World.");
        FakeCodepointProperties props = SetupAscii();

        List<SentenceRange> sentences = SentenceBoundaries.Enumerate(input, props);
        Assert.Equal(2, sentences.Count);
    }

    [Fact]
    public void Enumerate_does_not_split_inside_sentence()
    {
        byte[] input = Encoding.UTF8.GetBytes("Hello world");
        FakeCodepointProperties props = SetupAscii();

        List<SentenceRange> sentences = SentenceBoundaries.Enumerate(input, props);
        Assert.Single(sentences);
        Assert.Equal(input.Length, sentences[0].ByteLength);
    }

    [Fact]
    public void Enumerate_splits_at_mandatory_newline()
    {
        byte[] input = Encoding.UTF8.GetBytes("a\nb");
        FakeCodepointProperties props = SetupAscii()
            .WithSb('\n', SentenceBreak.LF);

        List<SentenceRange> sentences = SentenceBoundaries.Enumerate(input, props);
        Assert.Equal(2, sentences.Count);
    }

    private static FakeCodepointProperties SetupAscii()
    {
        FakeCodepointProperties p = new();
        for (char c = 'a'; c <= 'z'; c++)
        {
            p.WithSb(c, SentenceBreak.Lower);
        }
        for (char c = 'A'; c <= 'Z'; c++)
        {
            p.WithSb(c, SentenceBreak.Upper);
        }
        p.WithSb(' ', SentenceBreak.Sp);
        p.WithSb('.', SentenceBreak.ATerm);
        return p;
    }
}
