using Hartonomous.Decomposers.Iso639;

namespace Hartonomous.Decomposers.Tests.Iso639;

public sealed class Iso639ParserTests : IDisposable
{
    private readonly string _tempDir;

    public Iso639ParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hartonomous_iso639_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string WriteTsv(string filename, string content)
    {
        string path = Path.Combine(_tempDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParseLanguages_ParsesAllFields()
    {
        string path = WriteTsv("iso-639-3.tab",
            "Id\tPart2B\tPart2T\tPart1\tScope\tLanguage_Type\tRef_Name\n" +
            "eng\teng\teng\ten\tI\tL\tEnglish\n" +
            "cmn\t\t\t\tI\tL\tMandarin Chinese\n");

        List<Iso639Record> result = Iso639Parser.ParseLanguages(path);

        Assert.Equal(2, result.Count);

        Assert.Equal("eng", result[0].Id);
        Assert.Equal("eng", result[0].Part2b);
        Assert.Equal("eng", result[0].Part2t);
        Assert.Equal("en", result[0].Part1);
        Assert.Equal('I', result[0].Scope);
        Assert.Equal('L', result[0].LanguageType);
        Assert.Equal("English", result[0].RefName);

        Assert.Equal("cmn", result[1].Id);
        Assert.Null(result[1].Part2b);
        Assert.Null(result[1].Part2t);
        Assert.Null(result[1].Part1);
        Assert.Equal("Mandarin Chinese", result[1].RefName);
    }

    [Fact]
    public void ParseLanguages_SkipsShortLines()
    {
        string path = WriteTsv("iso-639-3.tab",
            "Id\tPart2B\tPart2T\tPart1\tScope\tLanguage_Type\tRef_Name\n" +
            "eng\teng\n" +
            "fra\tfre\tfra\tfr\tI\tL\tFrench\n");

        List<Iso639Record> result = Iso639Parser.ParseLanguages(path);

        Assert.Single(result);
        Assert.Equal("fra", result[0].Id);
    }

    [Fact]
    public void ParseLanguages_SkipsEmptyLines()
    {
        string path = WriteTsv("iso-639-3.tab",
            "Id\tPart2B\tPart2T\tPart1\tScope\tLanguage_Type\tRef_Name\n" +
            "\n" +
            "jpn\tjpn\tjpn\tja\tI\tL\tJapanese\n");

        List<Iso639Record> result = Iso639Parser.ParseLanguages(path);

        Assert.Single(result);
        Assert.Equal("jpn", result[0].Id);
    }

    [Fact]
    public void ParseMacrolanguages_ParsesAllFields()
    {
        string path = WriteTsv("macrolanguages.tab",
            "M_Id\tI_Id\tI_Status\n" +
            "zho\tcmn\tA\n" +
            "zho\tyue\tA\n");

        List<MacrolanguageMapping> result = Iso639Parser.ParseMacrolanguages(path);

        Assert.Equal(2, result.Count);
        Assert.Equal("zho", result[0].MacrolanguageId);
        Assert.Equal("cmn", result[0].IndividualId);
        Assert.Equal('A', result[0].Status);
        Assert.Equal("yue", result[1].IndividualId);
    }

    [Fact]
    public void ParseNameIndex_ParsesAllFields()
    {
        string path = WriteTsv("name_index.tab",
            "Id\tPrint_Name\tInverted_Name\n" +
            "eng\tEnglish\tEnglish\n" +
            "zho\tChinese\tChinese\n");

        List<NameIndexEntry> result = Iso639Parser.ParseNameIndex(path);

        Assert.Equal(2, result.Count);
        Assert.Equal("eng", result[0].Id);
        Assert.Equal("English", result[0].PrintName);
        Assert.Equal("English", result[0].InvertedName);
    }

    [Fact]
    public void ParseRetirements_ParsesAllFields()
    {
        string path = WriteTsv("retirements.tab",
            "Id\tRef_Name\tRet_Reason\tChange_To\tRet_Remedy\tEffective\n" +
            "mol\tMoldavian\tD\tron\tDuplicate of Romanian\t2008-11-03\n" +
            "bgh\tBodo Gadaba\tS\t\tSplit into gdb and gbj\t2014-01-14\n");

        List<RetirementRecord> result = Iso639Parser.ParseRetirements(path);

        Assert.Equal(2, result.Count);

        Assert.Equal("mol", result[0].Id);
        Assert.Equal("Moldavian", result[0].RefName);
        Assert.Equal('D', result[0].RetReason);
        Assert.Equal("ron", result[0].ChangeTo);
        Assert.Equal("Duplicate of Romanian", result[0].RetRemedy);
        Assert.Equal("2008-11-03", result[0].EffectiveDate);

        Assert.Equal("bgh", result[1].Id);
        Assert.Null(result[1].ChangeTo);
        Assert.Equal("Split into gdb and gbj", result[1].RetRemedy);
    }

    [Fact]
    public void ParseRetirements_SkipsShortLines()
    {
        string path = WriteTsv("retirements.tab",
            "Id\tRef_Name\tRet_Reason\tChange_To\tRet_Remedy\tEffective\n" +
            "bad\tshort\n" +
            "mol\tMoldavian\tD\tron\tDuplicate of Romanian\t2008-11-03\n");

        List<RetirementRecord> result = Iso639Parser.ParseRetirements(path);

        Assert.Single(result);
        Assert.Equal("mol", result[0].Id);
    }
}
