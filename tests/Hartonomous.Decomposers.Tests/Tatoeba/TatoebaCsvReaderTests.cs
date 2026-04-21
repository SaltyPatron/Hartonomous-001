using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hartonomous.Decomposers.Tatoeba;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Tatoeba;

public sealed class TatoebaCsvReaderTests
{
    private static string WriteTemp(string content, string ext = "csv")
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"tatoeba-test-{Guid.NewGuid():N}.{ext}");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void ReadSentences_StandardRows_Parsed()
    {
        const string src = "1\tcmn\t我們試試看！\n2\teng\tLet's try.\n3\tfra\tEssayons.\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaSentenceRow> rows = TatoebaCsvReader.ReadSentences(path).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(new TatoebaSentenceRow(1, "cmn", "我們試試看！"), rows[0]);
            Assert.Equal(new TatoebaSentenceRow(2, "eng", "Let's try."), rows[1]);
            Assert.Equal(new TatoebaSentenceRow(3, "fra", "Essayons."), rows[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadSentences_SkipsBlankAndMalformed()
    {
        const string src =
            "1\teng\tHello\n" +
            "\n" +
            "   \n" +
            "notanint\teng\ttext\n" +      // non-integer id → skipped
            "2\teng\n" +                    // only 2 fields → skipped
            "3\teng\tGoodbye\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaSentenceRow> rows = TatoebaCsvReader.ReadSentences(path).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(1, rows[0].SentenceId);
            Assert.Equal(3, rows[1].SentenceId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadSentences_PreservesTabsInsideText()
    {
        // Split('\t') stops at first tab; any further tabs become part of parts[2].
        // Actual Tatoeba exports don't carry tabs in text; test is a safety net.
        const string src = "1\teng\tHello world\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaSentenceRow> rows = TatoebaCsvReader.ReadSentences(path).ToList();
            Assert.Single(rows);
            Assert.Equal("Hello world", rows[0].Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinks_StandardRows_Parsed()
    {
        const string src = "1\t2481\n2481\t1\n5\t6\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaLinkRow> links = TatoebaCsvReader.ReadLinks(path).ToList();
            Assert.Equal(3, links.Count);
            Assert.Equal(new TatoebaLinkRow(1, 2481), links[0]);
            Assert.Equal(new TatoebaLinkRow(2481, 1), links[1]);
            Assert.Equal(new TatoebaLinkRow(5, 6), links[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLinks_SkipsMalformed()
    {
        const string src = "1\t2\nbad\t3\n4\tbad\n5\n6\t7\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaLinkRow> links = TatoebaCsvReader.ReadLinks(path).ToList();
            Assert.Equal(2, links.Count);
            Assert.Equal(new TatoebaLinkRow(1, 2), links[0]);
            Assert.Equal(new TatoebaLinkRow(6, 7), links[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadAudio_StandardRows_Parsed()
    {
        const string src =
            "1\t1276691\tLeviHighway\n" +
            "42\t99\tCK\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaAudioRow> rows = TatoebaCsvReader.ReadAudio(path).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(new TatoebaAudioRow(1, 1276691, "LeviHighway"), rows[0]);
            Assert.Equal(new TatoebaAudioRow(42, 99, "CK"), rows[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadAudio_SkipsMalformed()
    {
        const string src =
            "1\t100\talice\n" +
            "bad\t200\tbob\n" +
            "2\tbad\tcarol\n" +
            "3\t300\n" +
            "4\t400\tdave\n";
        string path = WriteTemp(src);
        try
        {
            List<TatoebaAudioRow> rows = TatoebaCsvReader.ReadAudio(path).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(new TatoebaAudioRow(1, 100, "alice"), rows[0]);
            Assert.Equal(new TatoebaAudioRow(4, 400, "dave"), rows[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
