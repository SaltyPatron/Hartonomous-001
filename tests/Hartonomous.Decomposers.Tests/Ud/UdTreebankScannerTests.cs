using System.IO;
using System.Linq;
using Hartonomous.Decomposers.Ud;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Ud;

public sealed class UdTreebankScannerTests : System.IDisposable
{
    private readonly string _root;

    public UdTreebankScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ud-scan-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Scan_FindsTreebanksWithConlluFiles()
    {
        string tb = Path.Combine(_root, "UD_English-EWT");
        Directory.CreateDirectory(tb);
        File.WriteAllText(Path.Combine(tb, "en_ewt-ud-train.conllu"), "");
        File.WriteAllText(Path.Combine(tb, "en_ewt-ud-dev.conllu"), "");
        File.WriteAllText(Path.Combine(tb, "en_ewt-ud-test.conllu"), "");

        var banks = UdTreebankScanner.Scan(_root);
        Assert.Single(banks);
        UdTreebankInfo b = banks[0];
        Assert.Equal("UD_English-EWT", b.DirectoryName);
        Assert.Equal("English", b.LanguageName);
        Assert.Equal("EWT", b.TreebankName);
        Assert.Equal("en", b.LanguageCode);
        Assert.Equal(3, b.ConlluFiles.Count);
    }

    [Fact]
    public void Scan_SkipsDirectoriesWithoutConllu()
    {
        string tb = Path.Combine(_root, "UD_Afrikaans-Empty");
        Directory.CreateDirectory(tb);
        File.WriteAllText(Path.Combine(tb, "README.md"), "no conllu");

        var banks = UdTreebankScanner.Scan(_root);
        Assert.Empty(banks);
    }

    [Fact]
    public void Scan_EmitsBanksInDirectoryOrder()
    {
        foreach (string name in new[] { "UD_Zulu-XYZ", "UD_Afrikaans-ABC", "UD_Maltese-MDT" })
        {
            string tb = Path.Combine(_root, name);
            Directory.CreateDirectory(tb);
            string isoPrefix = name switch
            {
                "UD_Zulu-XYZ" => "zu",
                "UD_Afrikaans-ABC" => "af",
                _ => "mt",
            };
            File.WriteAllText(Path.Combine(tb, $"{isoPrefix}_x-ud-test.conllu"), "");
        }

        var banks = UdTreebankScanner.Scan(_root);
        Assert.Equal(3, banks.Count);
        Assert.Equal("UD_Afrikaans-ABC", banks[0].DirectoryName);
        Assert.Equal("UD_Maltese-MDT", banks[1].DirectoryName);
        Assert.Equal("UD_Zulu-XYZ", banks[2].DirectoryName);
    }
}
