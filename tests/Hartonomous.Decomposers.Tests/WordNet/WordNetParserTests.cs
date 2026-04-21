using Hartonomous.Decomposers.WordNet;

namespace Hartonomous.Decomposers.Tests.WordNet;

public sealed class WordNetParserTests : IDisposable
{
    private readonly string _tempDir;

    public WordNetParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hartonomous_wn_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string WriteFile(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParseDataFile_ParsesNounSynset()
    {
        string path = WriteFile("data.noun",
            "  1 License header line\n" +
            "00001740 03 n 01 entity 0 003 ~ 00001930 n 0000 ~ 00002137 n 0000 ~ 04424418 n 0000 | that which is perceived\n");

        List<SynsetRecord> result = WordNetParser.ParseDataFile(path);

        Assert.Single(result);
        SynsetRecord s = result[0];
        Assert.Equal(1740, s.Offset);
        Assert.Equal(3, s.LexFileNum);
        Assert.Equal('n', s.SsType);
        Assert.Single(s.Words);
        Assert.Equal("entity", s.Words[0].Word);
        Assert.Equal(3, s.Pointers.Count);
        Assert.Equal("~", s.Pointers[0].Symbol);
        Assert.Equal(1930, s.Pointers[0].TargetOffset);
        Assert.Equal("that which is perceived", s.Gloss);
    }

    [Fact]
    public void ParseDataFile_ParsesMultiWordSynset()
    {
        string path = WriteFile("data.noun",
            "00002684 03 n 02 object 0 physical_object 0 001 @ 00001930 n 0000 | a tangible entity\n");

        List<SynsetRecord> result = WordNetParser.ParseDataFile(path);

        Assert.Single(result);
        Assert.Equal(2, result[0].Words.Count);
        Assert.Equal("object", result[0].Words[0].Word);
        Assert.Equal("physical_object", result[0].Words[1].Word);
    }

    [Fact]
    public void ParseDataFile_SkipsLicenseHeader()
    {
        string path = WriteFile("data.noun",
            "  1 This software\n" +
            "  2 is provided\n" +
            "00001740 03 n 01 entity 0 000 | test\n");

        List<SynsetRecord> result = WordNetParser.ParseDataFile(path);

        Assert.Single(result);
        Assert.Equal(1740, result[0].Offset);
    }

    [Fact]
    public void ParseSenseIndex_ParsesAllFields()
    {
        string path = WriteFile("index.sense",
            "entity%1:03:00:: 00001740 1 42\n" +
            "run%2:38:04:: 01926311 3 0\n");

        List<SenseIndexEntry> result = WordNetParser.ParseSenseIndex(path);

        Assert.Equal(2, result.Count);
        Assert.Equal("entity%1:03:00::", result[0].SenseKey);
        Assert.Equal(1740, result[0].SynsetOffset);
        Assert.Equal(1, result[0].SenseNumber);
        Assert.Equal(42, result[0].TagCount);
        Assert.Equal(0, result[1].TagCount);
    }

    [Fact]
    public void ParseExceptionFile_ParsesSingleBase()
    {
        string path = WriteFile("noun.exc", "aardwolves aardwolf\n");

        List<MorphException> result = WordNetParser.ParseExceptionFile(path, 'n');

        Assert.Single(result);
        Assert.Equal("aardwolves", result[0].InflectedForm);
        Assert.Single(result[0].BaseForms);
        Assert.Equal("aardwolf", result[0].BaseForms[0]);
        Assert.Equal('n', result[0].Pos);
    }

    [Fact]
    public void ParseExceptionFile_ParsesMultipleBases()
    {
        string path = WriteFile("noun.exc", "indices index indice\n");

        List<MorphException> result = WordNetParser.ParseExceptionFile(path, 'n');

        Assert.Single(result);
        Assert.Equal(2, result[0].BaseForms.Count);
        Assert.Equal("index", result[0].BaseForms[0]);
        Assert.Equal("indice", result[0].BaseForms[1]);
    }

    [Fact]
    public void ParseSentences_ParsesIdAndTemplate()
    {
        string path = WriteFile("sents.vrb",
            "1 The children %s to the playground\n" +
            "10 The cars %s down the avenue\n");

        List<VerbSentence> result = WordNetParser.ParseSentences(path);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Id);
        Assert.Equal("The children %s to the playground", result[0].Template);
        Assert.Equal(10, result[1].Id);
    }

    [Fact]
    public void ParseSentenceIndex_ParsesCommaDelimitedIds()
    {
        string path = WriteFile("sentidx.vrb",
            "abash%2:37:00:: 126,127\n" +
            "abhor%2:37:00:: 138,139,15\n");

        List<VerbSentenceIndex> result = WordNetParser.ParseSentenceIndex(path);

        Assert.Equal(2, result.Count);
        Assert.Equal("abash%2:37:00::", result[0].SenseKey);
        Assert.Equal(2, result[0].SentenceIds.Count);
        Assert.Equal(126, result[0].SentenceIds[0]);
        Assert.Equal(127, result[0].SentenceIds[1]);
        Assert.Equal(3, result[1].SentenceIds.Count);
    }

    [Fact]
    public void PointerSymbolToRelation_MapsAllSymbols()
    {
        Assert.Equal("antonym", WordNetParser.PointerSymbolToRelation("!"));
        Assert.Equal("hypernym", WordNetParser.PointerSymbolToRelation("@"));
        Assert.Equal("instance_hypernym", WordNetParser.PointerSymbolToRelation("@i"));
        Assert.Equal("hyponym", WordNetParser.PointerSymbolToRelation("~"));
        Assert.Equal("member_meronym", WordNetParser.PointerSymbolToRelation("%m"));
        Assert.Equal("entailment", WordNetParser.PointerSymbolToRelation("*"));
        Assert.Equal("cause", WordNetParser.PointerSymbolToRelation(">"));
        Assert.Equal("pertainym", WordNetParser.PointerSymbolToRelation(@"\"));
    }

    [Fact]
    public void PosCharToUdPos_MapsCorrectly()
    {
        Assert.Equal("NOUN", WordNetParser.PosCharToUdPos('n'));
        Assert.Equal("VERB", WordNetParser.PosCharToUdPos('v'));
        Assert.Equal("ADJ", WordNetParser.PosCharToUdPos('a'));
        Assert.Equal("ADJ", WordNetParser.PosCharToUdPos('s'));
        Assert.Equal("ADV", WordNetParser.PosCharToUdPos('r'));
    }

    [Fact]
    public void ParseGloss_DefinitionOnly()
    {
        (string def, List<string> examples) = WordNetParser.ParseGloss("that which is perceived or known");

        Assert.Equal("that which is perceived or known", def);
        Assert.Empty(examples);
    }

    [Fact]
    public void ParseGloss_DefinitionWithOneExample()
    {
        (string def, List<string> examples) = WordNetParser.ParseGloss(
            "a person who is of equal standing with another; \"he was combative toward his peers\"");

        Assert.Equal("a person who is of equal standing with another", def);
        Assert.Single(examples);
        Assert.Equal("he was combative toward his peers", examples[0]);
    }

    [Fact]
    public void ParseGloss_DefinitionWithMultipleExamples()
    {
        (string def, List<string> examples) = WordNetParser.ParseGloss(
            "an upward slope; \"the survey showed a steep grade\"; \"the car climbed the gradient\"");

        Assert.Equal("an upward slope", def);
        Assert.Equal(2, examples.Count);
        Assert.Equal("the survey showed a steep grade", examples[0]);
        Assert.Equal("the car climbed the gradient", examples[1]);
    }

    [Fact]
    public void ParseGloss_EmptyGloss()
    {
        (string def, List<string> examples) = WordNetParser.ParseGloss("");

        Assert.Equal("", def);
        Assert.Empty(examples);
    }
}
