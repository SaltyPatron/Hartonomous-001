using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public class UcdXmlParserTests
{
    private const string XmlPath = @"D:\Models\UCD\Public\UCD\latest\ucdxml\ucd.all.grouped.xml";

    [Fact]
    public void Parse_ReturnsCodepoints()
    {
        if (!File.Exists(XmlPath))
        {
            return;
        }

        ReferenceTableCollector refs = new();
        List<CodepointRecord> codepoints = UcdXmlParser.Parse(XmlPath, refs, CancellationToken.None)
            .Take(1000)
            .ToList();

        Assert.NotEmpty(codepoints);
        Assert.True(codepoints.Count == 1000, $"Expected 1000 codepoints, got {codepoints.Count}");
    }

    [Fact]
    public void Parse_LatinCapitalA_HasCorrectProperties()
    {
        if (!File.Exists(XmlPath))
        {
            return;
        }

        ReferenceTableCollector refs = new();
        CodepointRecord? letterA = UcdXmlParser.Parse(XmlPath, refs, CancellationToken.None)
            .FirstOrDefault(cp => cp.Value == 0x0041);

        Assert.NotNull(letterA);
        Assert.Equal("Lu", letterA.GeneralCategory);
        Assert.Equal("Latn", letterA.Script);
        Assert.True(letterA.IsAlphabetic);
        Assert.True(letterA.IsCased);
        Assert.True(letterA.IsUppercase);
        Assert.False(letterA.IsLowercase);
        Assert.True(letterA.IsGraphemeBase);
        Assert.True(letterA.IsIdStart);
        Assert.Equal(0x0061, letterA.SimpleLowercase);
    }

    [Fact]
    public void Parse_CollectsReferenceTableValues()
    {
        if (!File.Exists(XmlPath))
        {
            return;
        }

        ReferenceTableCollector refs = new();

        // Parse enough to collect reference values.
        int count = 0;
        foreach (CodepointRecord _ in UcdXmlParser.Parse(XmlPath, refs, CancellationToken.None))
        {
            if (++count >= 500)
            {
                break;
            }
        }

        Assert.NotEmpty(refs.GeneralCategories);
        Assert.NotEmpty(refs.Scripts);
        Assert.Contains("Cc", refs.GeneralCategories.Keys);
        Assert.Contains("Zyyy", refs.Scripts.Keys);
    }

    [Fact]
    public void Parse_HandlesFullFile()
    {
        if (!File.Exists(XmlPath))
        {
            return;
        }

        ReferenceTableCollector refs = new();
        int totalCodepoints = 0;

        foreach (CodepointRecord _ in UcdXmlParser.Parse(XmlPath, refs, CancellationToken.None))
        {
            totalCodepoints++;
        }

        // Full Unicode range is 1,114,112 code points (U+0000 to U+10FFFF).
        // We include assigned, reserved, noncharacter, and surrogate.
        Assert.True(totalCodepoints > 1_000_000,
            $"Expected >1M codepoints (full Unicode range), got {totalCodepoints}");
        Assert.True(refs.GeneralCategories.Count >= 25,
            $"Expected >=25 general categories, got {refs.GeneralCategories.Count}");
        Assert.True(refs.Scripts.Count >= 150,
            $"Expected >=150 scripts, got {refs.Scripts.Count}");
    }
}
