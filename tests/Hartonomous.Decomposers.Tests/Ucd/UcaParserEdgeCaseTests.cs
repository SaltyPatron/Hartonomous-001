using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public sealed class UcaParserEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public UcaParserEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hartonomous_uca_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void ParseAllKeys_EmptyFile_ReturnsEmpty()
    {
        string path = WriteFile("empty.txt", "# comment only\n@version 17.0.0\n");
        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(path);
        Assert.Empty(map);
    }

    [Fact]
    public void ParseAllKeys_SingleEntry_ParsesCorrectly()
    {
        string content = "# UCA\n@version 17.0.0\n0041 ; [.1C47.0020.0008] # LATIN CAPITAL LETTER A\n";
        string path = WriteFile("single.txt", content);

        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(path);

        Assert.Single(map);
        Assert.True(map.ContainsKey(0x0041));
        Assert.Equal(0x1C47, map[0x0041].Primary);
        Assert.Equal(0x0020, map[0x0041].Secondary);
        Assert.Equal(0x0008, map[0x0041].Tertiary);
    }

    [Fact]
    public void ParseAllKeys_MultiCodepointSequence_Skipped()
    {
        string content = "# UCA\n0041 0042 ; [.1C47.0020.0008] # multi-cp sequence\n0043 ; [.1C48.0020.0008] # single\n";
        string path = WriteFile("multi.txt", content);

        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(path);

        Assert.Single(map);
        Assert.True(map.ContainsKey(0x0043));
        Assert.False(map.ContainsKey(0x0041)); // multi-cp skipped
    }

    [Fact]
    public void ParseAllKeys_ImplicitWeightLine_Skipped()
    {
        string content = "@implicitweights 17000..18AFF; FB00\n0041 ; [.1C47.0020.0008] # A\n";
        string path = WriteFile("implicit.txt", content);

        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(path);
        Assert.Single(map);
    }

    [Fact]
    public void CollationWeightComparer_PrimaryDominates()
    {
        CollationWeight low = new(100, 999, 999);
        CollationWeight high = new(200, 1, 1);

        Assert.True(CollationWeightComparer.Instance.Compare(low, high) < 0);
    }

    [Fact]
    public void CollationWeightComparer_SecondaryBreaksTie()
    {
        CollationWeight a = new(100, 20, 99);
        CollationWeight b = new(100, 30, 1);

        Assert.True(CollationWeightComparer.Instance.Compare(a, b) < 0);
    }

    [Fact]
    public void CollationWeightComparer_TertiaryBreaksTie()
    {
        CollationWeight a = new(100, 20, 5);
        CollationWeight b = new(100, 20, 8);

        Assert.True(CollationWeightComparer.Instance.Compare(a, b) < 0);
    }

    [Fact]
    public void CollationWeightComparer_EqualWeights_ReturnZero()
    {
        CollationWeight a = new(100, 20, 8);
        CollationWeight b = new(100, 20, 8);

        Assert.Equal(0, CollationWeightComparer.Instance.Compare(a, b));
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
