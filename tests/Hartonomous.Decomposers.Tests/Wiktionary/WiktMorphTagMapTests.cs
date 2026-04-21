using System.Collections.Generic;
using System.Linq;
using Hartonomous.Decomposers.Wiktionary;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Wiktionary;

public sealed class WiktMorphTagMapTests
{
    [Fact]
    public void Translate_Empty_ReturnsEmpty()
    {
        Assert.Empty(WiktMorphTagMap.Translate([]));
    }

    [Fact]
    public void Translate_SingleTag_ReturnsSingleFeature()
    {
        IReadOnlyList<(string Key, string Value)> f = WiktMorphTagMap.Translate(["plural"]);
        Assert.Single(f);
        Assert.Equal(("Number", "Plur"), f[0]);
    }

    [Fact]
    public void Translate_CompoundTag_ExpandsToMultipleFeatures()
    {
        IReadOnlyList<(string Key, string Value)> f = WiktMorphTagMap.Translate(["past-participle"]);
        Assert.Equal(2, f.Count);
        Assert.Contains(("VerbForm", "Part"), f);
        Assert.Contains(("Tense", "Past"), f);
    }

    [Fact]
    public void Translate_MultipleTags_Aggregates()
    {
        IReadOnlyList<(string Key, string Value)> f =
            WiktMorphTagMap.Translate(["plural", "feminine", "genitive"]);
        Assert.Contains(("Number", "Plur"), f);
        Assert.Contains(("Gender", "Fem"), f);
        Assert.Contains(("Case", "Gen"), f);
    }

    [Fact]
    public void Translate_UnknownTag_Ignored()
    {
        IReadOnlyList<(string Key, string Value)> f =
            WiktMorphTagMap.Translate(["plural", "not-a-real-tag", "bogus"]);
        Assert.Single(f);
        Assert.Equal(("Number", "Plur"), f[0]);
    }

    [Theory]
    [InlineData("plural", "Number", "Plur")]
    [InlineData("singular", "Number", "Sing")]
    [InlineData("masculine", "Gender", "Masc")]
    [InlineData("nominative", "Case", "Nom")]
    [InlineData("indicative", "Mood", "Ind")]
    [InlineData("active", "Voice", "Act")]
    [InlineData("comparative", "Degree", "Cmp")]
    [InlineData("definite", "Definite", "Def")]
    [InlineData("perfective", "Aspect", "Perf")]
    public void Translate_CoverageSpotCheck(string tag, string expectedKey, string expectedValue)
    {
        IReadOnlyList<(string Key, string Value)> f = WiktMorphTagMap.Translate([tag]);
        Assert.Contains((expectedKey, expectedValue), f);
    }

    [Fact]
    public void Translate_IsCaseInsensitive()
    {
        IReadOnlyList<(string Key, string Value)> f = WiktMorphTagMap.Translate(["Plural", "FEMININE"]);
        Assert.Contains(("Number", "Plur"), f);
        Assert.Contains(("Gender", "Fem"), f);
    }
}
