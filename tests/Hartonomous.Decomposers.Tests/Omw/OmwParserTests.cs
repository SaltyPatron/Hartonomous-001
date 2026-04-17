using Hartonomous.Decomposers.Omw;

namespace Hartonomous.Decomposers.Tests.Omw;

public sealed class OmwParserTests : IDisposable
{
    private readonly string _tempDir;

    public OmwParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hartonomous_omw_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void ParseTabFile_ParsesLemmaEntries()
    {
        string path = WriteFile("wn-data-jpn.tab",
            "# Japanese Wordnet\tjpn\thttp://example.com\n" +
            "00001740-n\tjpn:lemma\t実体\n" +
            "00001740-v\tjpn:lemma\t吐く\n");

        List<OmwTabEntry> result = OmwParser.ParseTabFile(path);

        Assert.Equal(2, result.Count);
        Assert.Equal("00001740-n", result[0].SynsetCode);
        Assert.Equal("jpn", result[0].LangCode);
        Assert.Equal("lemma", result[0].Relation);
        Assert.Equal("実体", result[0].Word);
        Assert.Equal("00001740-v", result[1].SynsetCode);
    }

    [Fact]
    public void ParseTabFile_SkipsComments()
    {
        string path = WriteFile("test.tab",
            "# comment line\n" +
            "00001740-n\tfra:lemma\tentité\n");

        List<OmwTabEntry> result = OmwParser.ParseTabFile(path);

        Assert.Single(result);
        Assert.Equal("entité", result[0].Word);
    }

    [Fact]
    public void ParseTabFile_SkipsShortLines()
    {
        string path = WriteFile("test.tab",
            "00001740-n\tfra:lemma\n" +
            "00001740-n\tfra:lemma\tentité\n");

        List<OmwTabEntry> result = OmwParser.ParseTabFile(path);

        Assert.Single(result);
    }

    [Fact]
    public void DiscoverTabFiles_FindsCuratedFiles()
    {
        string wnsDir = Path.Combine(_tempDir, "wns");
        WriteFile("wns/jpn/wn-data-jpn.tab", "# test\n");
        WriteFile("wns/fra/wn-data-fra.tab", "# test\n");

        List<OmwSourceInfo> result = OmwParser.DiscoverTabFiles(wnsDir);

        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.Equal(OmwSourceTier.Curated, s.Tier));
        Assert.Contains(result, s => s.LangCode == "jpn");
        Assert.Contains(result, s => s.LangCode == "fra");
    }

    [Fact]
    public void DiscoverTabFiles_FindsCldrFiles()
    {
        string wnsDir = Path.Combine(_tempDir, "wns");
        WriteFile("wns/cldr/wn-cldr-deu.tab", "# test\n");

        List<OmwSourceInfo> result = OmwParser.DiscoverTabFiles(wnsDir);

        Assert.Single(result);
        Assert.Equal(OmwSourceTier.Cldr, result[0].Tier);
        Assert.Equal("deu", result[0].LangCode);
    }

    [Fact]
    public void DiscoverTabFiles_FindsWiktionaryFiles()
    {
        string wnsDir = Path.Combine(_tempDir, "wns");
        WriteFile("wns/wikt/wn-wikt-kor.tab", "# test\n");

        List<OmwSourceInfo> result = OmwParser.DiscoverTabFiles(wnsDir);

        Assert.Single(result);
        Assert.Equal(OmwSourceTier.Wiktionary, result[0].Tier);
        Assert.Equal("kor", result[0].LangCode);
    }

    [Fact]
    public void DiscoverTabFiles_SkipsEnDirectory()
    {
        string wnsDir = Path.Combine(_tempDir, "wns");
        WriteFile("wns/en/wn-data-en.tab", "# test\n");
        WriteFile("wns/jpn/wn-data-jpn.tab", "# test\n");

        List<OmwSourceInfo> result = OmwParser.DiscoverTabFiles(wnsDir);

        Assert.Single(result);
        Assert.Equal("jpn", result[0].LangCode);
    }

    [Fact]
    public void DiscoverTabFiles_ReturnsEmptyForMissingDir()
    {
        List<OmwSourceInfo> result = OmwParser.DiscoverTabFiles("/nonexistent/path");

        Assert.Empty(result);
    }
}
