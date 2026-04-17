using System.Text;
using Hartonomous.Core.Text.Segmentation;
using Xunit;

namespace Hartonomous.Core.Tests.Text;

public class WordBoundariesTests
{
    [Fact]
    public void EnumerateWords_empty_input_yields_none()
    {
        List<WordRange> result = WordBoundaries.EnumerateWords(ReadOnlySpan<byte>.Empty, new FakeCodepointProperties());
        Assert.Empty(result);
    }

    [Fact]
    public void EnumerateWords_ascii_words_round_trip()
    {
        byte[] input = Encoding.UTF8.GetBytes("hello world");
        FakeCodepointProperties props = SetupAscii();

        List<WordRange> words = WordBoundaries.EnumerateWords(input, props);
        Assert.Equal(2, words.Count);
        Assert.Equal(WordKind.AlphaNumeric, words[0].Kind);
        Assert.Equal(WordKind.AlphaNumeric, words[1].Kind);
    }

    [Fact]
    public void EnumerateWords_numeric_is_classified()
    {
        byte[] input = Encoding.UTF8.GetBytes("123");
        FakeCodepointProperties props = new FakeCodepointProperties()
            .WithWb('1', WordBreak.Numeric)
            .WithWb('2', WordBreak.Numeric)
            .WithWb('3', WordBreak.Numeric);

        List<WordRange> words = WordBoundaries.EnumerateWords(input, props);
        Assert.Single(words);
        Assert.Equal(WordKind.Numeric, words[0].Kind);
    }

    [Fact]
    public void EnumerateBoundaries_starts_at_zero_and_ends_at_length()
    {
        byte[] input = Encoding.UTF8.GetBytes("ab cd");
        FakeCodepointProperties props = SetupAscii();

        List<long> boundaries = WordBoundaries.EnumerateBoundaries(input, props);
        Assert.Equal(0, boundaries[0]);
        Assert.Equal(input.Length, boundaries[^1]);
    }

    [Fact]
    public void MidLetter_between_letters_suppresses_break()
    {
        // "can't" — apostrophe is WB6/WB7 suppressed.
        byte[] input = Encoding.UTF8.GetBytes("can't");
        FakeCodepointProperties props = SetupAscii();
        props.WithWb('\'', WordBreak.MidLetter);

        List<WordRange> words = WordBoundaries.EnumerateWords(input, props);
        Assert.Single(words);
        Assert.Equal(5, words[0].ByteLength);
    }

    private static FakeCodepointProperties SetupAscii()
    {
        FakeCodepointProperties p = new();
        for (char c = 'a'; c <= 'z'; c++)
        {
            p.WithWb(c, WordBreak.ALetter);
        }
        for (char c = 'A'; c <= 'Z'; c++)
        {
            p.WithWb(c, WordBreak.ALetter);
        }
        p.WithWb(' ', WordBreak.WSegSpace);
        return p;
    }
}
