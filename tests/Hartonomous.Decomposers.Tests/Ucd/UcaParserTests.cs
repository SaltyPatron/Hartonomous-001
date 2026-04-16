using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public class UcaParserTests
{
    private const string AllKeysPath = @"D:\Models\UCD\Public\UCD\latest\uca\allkeys.txt";

    [Fact]
    public void ParseAllKeys_ReturnsNonEmptyMap()
    {
        if (!File.Exists(AllKeysPath))
        {
            return; // skip if data not available
        }

        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(AllKeysPath);

        Assert.NotEmpty(map);
        Assert.True(map.Count > 30000, $"Expected >30K entries, got {map.Count}");
    }

    [Fact]
    public void ParseAllKeys_ContainsLatinCapitalA()
    {
        if (!File.Exists(AllKeysPath))
        {
            return;
        }

        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(AllKeysPath);

        Assert.True(map.ContainsKey(0x0041), "Expected U+0041 (LATIN CAPITAL LETTER A) in collation map");
        CollationWeight w = map[0x0041];
        Assert.True(w.Primary > 0, "Expected non-zero primary weight for 'A'");
    }

    [Fact]
    public void ParseAllKeys_PrimaryWeightOrdering_DigitsBeforeLetters()
    {
        if (!File.Exists(AllKeysPath))
        {
            return;
        }

        Dictionary<int, CollationWeight> map = UcaParser.ParseAllKeys(AllKeysPath);

        // UCA: digits sort before letters
        if (map.TryGetValue(0x0030, out CollationWeight digit0) &&
            map.TryGetValue(0x0041, out CollationWeight letterA))
        {
            Assert.True(digit0.Primary < letterA.Primary,
                $"Expected '0' (primary={digit0.Primary}) to sort before 'A' (primary={letterA.Primary})");
        }
    }

    [Fact]
    public void CollationWeightComparer_OrdersByPrimaryThenSecondaryThenTertiary()
    {
        CollationWeight a = new(100, 20, 3);
        CollationWeight b = new(100, 20, 5);
        CollationWeight c = new(100, 30, 1);
        CollationWeight d = new(200, 10, 1);

        CollationWeightComparer cmp = CollationWeightComparer.Instance;

        Assert.True(cmp.Compare(a, b) < 0);
        Assert.True(cmp.Compare(a, c) < 0);
        Assert.True(cmp.Compare(c, d) < 0);
        Assert.Equal(0, cmp.Compare(a, a));
    }
}
