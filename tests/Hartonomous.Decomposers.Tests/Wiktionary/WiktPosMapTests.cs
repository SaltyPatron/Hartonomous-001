using Hartonomous.Decomposers.Wiktionary;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Wiktionary;

public sealed class WiktPosMapTests
{
    [Theory]
    [InlineData("noun", "NOUN")]
    [InlineData("proper-noun", "PROPN")]
    [InlineData("name", "PROPN")]
    [InlineData("verb", "VERB")]
    [InlineData("adj", "ADJ")]
    [InlineData("adjective", "ADJ")]
    [InlineData("adv", "ADV")]
    [InlineData("pron", "PRON")]
    [InlineData("det", "DET")]
    [InlineData("article", "DET")]
    [InlineData("num", "NUM")]
    [InlineData("conj", "CCONJ")]
    [InlineData("prep", "ADP")]
    [InlineData("postp", "ADP")]
    [InlineData("intj", "INTJ")]
    [InlineData("particle", "PART")]
    [InlineData("punct", "PUNCT")]
    [InlineData("symbol", "SYM")]
    [InlineData("aux", "AUX")]
    public void ToUpos_KnownMapping_ReturnsCode(string wikt, string expected)
    {
        Assert.Equal(expected, WiktPosMap.ToUpos(wikt));
    }

    [Theory]
    [InlineData("NOUN")]
    [InlineData("Verb")]
    [InlineData("ADJECTIVE")]
    public void ToUpos_CaseInsensitive(string wikt)
    {
        Assert.NotNull(WiktPosMap.ToUpos(wikt));
    }

    [Theory]
    [InlineData("character")]
    [InlineData("letter")]
    [InlineData("phrase")]
    [InlineData("proverb")]
    [InlineData("abbreviation")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void ToUpos_Unmapped_ReturnsNull(string wikt)
    {
        Assert.Null(WiktPosMap.ToUpos(wikt));
    }
}
