using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public sealed class UcdXmlParserSyntheticTests : IDisposable
{
    private readonly string _tempDir;

    public UcdXmlParserSyntheticTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hartonomous_xml_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string WriteXml(string xml)
    {
        string path = Path.Combine(_tempDir, "test.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void Parse_SingleChar_ReturnsOneCodepoint()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Lu" sc="Latn">
                  <char cp="0041" na="LATIN CAPITAL LETTER A" Alpha="Y" Upper="Y" Cased="Y" Gr_Base="Y" IDS="Y" slc="0061"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Single(cps);
        Assert.Equal(0x0041, cps[0].Value);
        Assert.Equal("LATIN CAPITAL LETTER A", cps[0].Name);
        Assert.Equal("Lu", cps[0].GeneralCategory);
        Assert.Equal("Latn", cps[0].Script);
        Assert.True(cps[0].IsAlphabetic);
        Assert.True(cps[0].IsUppercase);
        Assert.True(cps[0].IsCased);
        Assert.True(cps[0].IsGraphemeBase);
        Assert.True(cps[0].IsIdStart);
        Assert.Equal(0x0061, cps[0].SimpleLowercase);
    }

    [Fact]
    public void Parse_GroupInheritance_ChildOverridesGroup()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Lu" sc="Latn" Alpha="Y">
                  <char cp="0041" na="A"/>
                  <char cp="0030" na="DIGIT ZERO" gc="Nd" sc="Zyyy" Alpha="N"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Equal(2, cps.Count);

        CodepointRecord a = cps.First(c => c.Value == 0x0041);
        Assert.Equal("Lu", a.GeneralCategory);
        Assert.Equal("Latn", a.Script);
        Assert.True(a.IsAlphabetic);

        CodepointRecord zero = cps.First(c => c.Value == 0x0030);
        Assert.Equal("Nd", zero.GeneralCategory); // overridden
        Assert.Equal("Zyyy", zero.Script); // overridden
        Assert.False(zero.IsAlphabetic); // overridden
    }

    [Fact]
    public void Parse_ReservedRange_ExpandsAllCodepoints()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Cn">
                  <reserved first-cp="FDD0" last-cp="FDD5"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Equal(6, cps.Count); // FDD0 through FDD5
        Assert.Equal(0xFDD0, cps[0].Value);
        Assert.Equal(0xFDD5, cps[5].Value);
        Assert.All(cps, c => Assert.Equal("Cn", c.GeneralCategory));
    }

    [Fact]
    public void Parse_NoncharacterElement_Included()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Cn">
                  <noncharacter cp="FFFE"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Single(cps);
        Assert.Equal(0xFFFE, cps[0].Value);
    }

    [Fact]
    public void Parse_SurrogateElement_Included()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Cs">
                  <surrogate first-cp="D800" last-cp="D802"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Equal(3, cps.Count);
        Assert.Equal(0xD800, cps[0].Value);
        Assert.All(cps, c => Assert.Equal("Cs", c.GeneralCategory));
    }

    [Fact]
    public void Parse_CollectsReferenceValues()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Lu" sc="Latn" blk="Basic_Latin" GCB="XX" WB="ALetter">
                  <char cp="0041" na="A"/>
                  <char cp="0042" na="B" gc="Ll" sc="Grek"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> _ = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Contains("Lu", refs.GeneralCategories.Keys);
        Assert.Contains("Ll", refs.GeneralCategories.Keys);
        Assert.Contains("Latn", refs.Scripts.Keys);
        Assert.Contains("Grek", refs.Scripts.Keys);
        Assert.Contains("Basic_Latin", refs.Blocks.Keys);
        Assert.Contains(("XX", "GCB"), refs.BreakProperties.Keys);
        Assert.Contains(("ALetter", "WB"), refs.BreakProperties.Keys);
    }

    [Fact]
    public void Parse_EmptyName_FallsBackToNa1ThenCodepoint()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Cc">
                  <char cp="0000" na="" na1="NULL"/>
                  <char cp="FFFE" na=""/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        CodepointRecord nullChar = cps.First(c => c.Value == 0x0000);
        Assert.Equal("NULL", nullChar.Name); // fallback to na1

        CodepointRecord fffe = cps.First(c => c.Value == 0xFFFE);
        Assert.Equal("U+FFFE", fffe.Name); // fallback to U+hex
    }

    [Fact]
    public void Parse_Cancellation_Honored()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Lu" sc="Latn">
                  <char cp="0041" na="A"/>
                  <char cp="0042" na="B"/>
                  <char cp="0043" na="C"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        using CancellationTokenSource cts = new();
        int count = 0;
        bool cancelled = false;
        try
        {
            foreach (CodepointRecord _ in UcdXmlParser.Parse(path, refs, cts.Token))
            {
                count++;
                if (count == 1)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.True(cancelled, "Expected OperationCanceledException after cancellation");
        Assert.True(count <= 2, $"Expected cancellation to stop iteration early, got {count} items");
    }

    [Fact]
    public void Parse_DecompositionMapping_Parsed()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Lu" sc="Latn">
                  <char cp="00C0" na="A GRAVE" dt="can" dm="0041 0300"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Single(cps);
        Assert.Equal("can", cps[0].DecompositionType);
        Assert.NotNull(cps[0].DecompositionMapping);
        int[] dm = cps[0].DecompositionMapping!;
        Assert.Equal(2, dm.Length);
        Assert.Equal(0x0041, dm[0]);
        Assert.Equal(0x0300, dm[1]);
    }

    [Fact]
    public void Parse_BooleanProperties_DefaultToFalse()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Cn">
                  <char cp="FFFF" na=""/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        CodepointRecord cp = cps[0];
        Assert.False(cp.IsAlphabetic);
        Assert.False(cp.IsCased);
        Assert.False(cp.IsUppercase);
        Assert.False(cp.IsLowercase);
        Assert.False(cp.IsMath);
        Assert.False(cp.IsEmoji);
        Assert.False(cp.IsGraphemeBase);
        Assert.False(cp.IsIdStart);
        Assert.False(cp.IsIdContinue);
    }

    [Fact]
    public void Parse_MultipleGroups_EachInheritsOwnDefaults()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ucd xmlns="http://www.unicode.org/ns/2003/ucd/1.0">
              <repertoire>
                <group gc="Lu" sc="Latn">
                  <char cp="0041" na="A"/>
                </group>
                <group gc="Nd" sc="Zyyy">
                  <char cp="0030" na="DIGIT ZERO"/>
                </group>
              </repertoire>
            </ucd>
            """;
        string path = WriteXml(xml);
        ReferenceTableCollector refs = new();

        List<CodepointRecord> cps = UcdXmlParser.Parse(path, refs, CancellationToken.None).ToList();

        Assert.Equal(2, cps.Count);
        Assert.Equal("Lu", cps.First(c => c.Value == 0x0041).GeneralCategory);
        Assert.Equal("Nd", cps.First(c => c.Value == 0x0030).GeneralCategory);
    }
}
