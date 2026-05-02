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
        // After the whitespace-keeping fix, "hello world" yields 3 ranges:
        // 'hello' (AlphaNumeric), ' ' (Other), 'world' (AlphaNumeric).
        // recompose_text walks the sequence and reproduces the input byte-
        // for-byte; previously the Other range was dropped, eating spaces.
        byte[] input = Encoding.UTF8.GetBytes("hello world");
        FakeCodepointProperties props = SetupAscii();

        List<WordRange> words = WordBoundaries.EnumerateWords(input, props);
        Assert.Equal(3, words.Count);
        Assert.Equal(WordKind.AlphaNumeric, words[0].Kind);
        Assert.Equal(WordKind.Other,        words[1].Kind);
        Assert.Equal(WordKind.AlphaNumeric, words[2].Kind);
        // Byte spans cover the entire input contiguously — no gaps.
        Assert.Equal(0, words[0].ByteOffset);
        Assert.Equal(5, words[0].ByteLength);
        Assert.Equal(5, words[1].ByteOffset);
        Assert.Equal(1, words[1].ByteLength);
        Assert.Equal(6, words[2].ByteOffset);
        Assert.Equal(5, words[2].ByteLength);
    }

    [Fact]
    public void EnumerateWords_other_ranges_are_kept_for_whitespace_recompose()
    {
        // Regression: the canonical text decomposer relies on Other ranges
        // appearing here so it can emit raw_span text_compositions for
        // inter-word whitespace. Prior to the fix, recompose_text walked
        // sequence trees missing all whitespace and produced runs of
        // word letters with no separators ("acompetitorwhoholds...").
        byte[] input = Encoding.UTF8.GetBytes("a b\tc");
        FakeCodepointProperties props = SetupAscii();
        props.WithWb('\t', WordBreak.WSegSpace);

        List<WordRange> words = WordBoundaries.EnumerateWords(input, props);
        // Expect: a (Alpha), space (Other), b (Alpha), tab (Other), c (Alpha) = 5
        Assert.Equal(5, words.Count);
        Assert.Equal(WordKind.Other, words[1].Kind);
        Assert.Equal(WordKind.Other, words[3].Kind);

        // Total byte length equals input length — proves no gaps.
        long totalLen = 0;
        foreach (WordRange w in words)
        {
            totalLen += w.ByteLength;
        }
        Assert.Equal(input.Length, totalLen);
    }

    [Fact]
    public void EnumerateWords_punctuation_only_input_yields_other()
    {
        byte[] input = Encoding.UTF8.GetBytes("...");
        FakeCodepointProperties props = SetupAscii();
        props.WithWb('.', WordBreak.MidNumLet); // any non-Alpha non-Numeric

        List<WordRange> words = WordBoundaries.EnumerateWords(input, props);
        Assert.NotEmpty(words);
        // Every range is Other because none of the codepoints are Alpha/Numeric/etc.
        foreach (WordRange w in words)
        {
            Assert.Equal(WordKind.Other, w.Kind);
        }
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
