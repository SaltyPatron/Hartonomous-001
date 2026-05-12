using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hartonomous.Decomposers.Wiktionary;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Wiktionary;

public sealed class WiktionaryJsonlParserTests
{
    [Fact]
    public void ParseLine_Minimal_ReturnsEntry()
    {
        const string line = """{"word":"bank","lang":"English","lang_code":"en","pos":"noun"}""";
        WiktEntry? entry = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(entry);
        Assert.Equal("bank", entry!.Word);
        Assert.Equal("English", entry.Lang);
        Assert.Equal("en", entry.LangCode);
        Assert.Equal("noun", entry.Pos);
        Assert.Empty(entry.Senses);
        Assert.Empty(entry.Forms);
        Assert.Empty(entry.Translations);
        Assert.Empty(entry.Synonyms);
        Assert.Null(entry.EtymologyNumber);
        Assert.Null(entry.EtymologyText);
    }

    [Fact]
    public void ParseLine_BlankOrWhitespace_ReturnsNull()
    {
        Assert.Null(WiktionaryJsonlParser.ParseLine(""));
        Assert.Null(WiktionaryJsonlParser.ParseLine("   \t  "));
    }

    [Fact]
    public void ParseLine_MissingRequiredFields_ReturnsNull()
    {
        // No word.
        Assert.Null(WiktionaryJsonlParser.ParseLine("""{"lang_code":"en","pos":"noun"}"""));
        // No lang_code.
        Assert.Null(WiktionaryJsonlParser.ParseLine("""{"word":"x","pos":"noun"}"""));
        // No pos.
        Assert.Null(WiktionaryJsonlParser.ParseLine("""{"word":"x","lang_code":"en"}"""));
    }

    [Fact]
    public void ParseLine_SensesWithGlossesAndExamples_Parsed()
    {
        const string line = """
{"word":"bank","lang_code":"en","pos":"noun","senses":[
  {"glosses":["A financial institution."],"examples":[{"text":"He went to the bank.","type":"quote"}],"senseid":["en:financial"],"wikidata":["Q22687"]},
  {"glosses":["The side of a river."],"tags":["geography"]}
]}
""";
        WiktEntry? e = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(e);
        Assert.Equal(2, e!.Senses.Count);

        WiktSense s0 = e.Senses[0];
        Assert.Equal(new[] { "A financial institution." }, s0.Glosses);
        Assert.Single(s0.Examples);
        Assert.Equal("He went to the bank.", s0.Examples[0].Text);
        Assert.Equal("quote", s0.Examples[0].Type);
        Assert.Contains("en:financial", s0.Senseid);
        Assert.Contains("Q22687", s0.Wikidata);

        WiktSense s1 = e.Senses[1];
        Assert.Equal(new[] { "The side of a river." }, s1.Glosses);
        Assert.Contains("geography", s1.Tags);
        Assert.Empty(s1.Examples);
    }

    [Fact]
    public void ParseLine_FormsWithTags_Parsed()
    {
        const string line = """
{"word":"run","lang_code":"en","pos":"verb","forms":[
  {"form":"ran","tags":["past"]},
  {"form":"running","tags":["present-participle"]}
]}
""";
        WiktEntry? e = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(e);
        Assert.Equal(2, e!.Forms.Count);
        Assert.Equal("ran", e.Forms[0].Form);
        Assert.Contains("past", e.Forms[0].Tags);
        Assert.Equal("running", e.Forms[1].Form);
        Assert.Contains("present-participle", e.Forms[1].Tags);
    }

    [Fact]
    public void ParseLine_SoundsHyphenations_Parsed()
    {
        const string line = """
{"word":"example","lang_code":"en","pos":"noun","sounds":[
  {"ipa":"/ɪɡˈzɑːm.pəl/","tags":["UK"]},
  {"audio":"en-us-example.ogg","ogg_url":"https://example.org/en.ogg","mp3_url":"https://example.org/en.mp3"}
],"hyphenations":[{"parts":["ex","am","ple"]}]}
""";
        WiktEntry? e = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(e);
        Assert.Equal(2, e!.Sounds.Count);
        Assert.Equal("/ɪɡˈzɑːm.pəl/", e.Sounds[0].Ipa);
        Assert.Contains("UK", e.Sounds[0].Tags);
        Assert.Equal("en-us-example.ogg", e.Sounds[1].Audio);
        Assert.Equal("https://example.org/en.ogg", e.Sounds[1].OggUrl);
        Assert.Single(e.Hyphenations);
        Assert.Equal(new[] { "ex", "am", "ple" }, e.Hyphenations[0].Parts);
    }

    [Fact]
    public void ParseLine_Translations_Parsed()
    {
        const string line = """
{"word":"water","lang_code":"en","pos":"noun","translations":[
  {"lang":"German","code":"de","word":"Wasser","sense":"liquid"},
  {"lang":"Spanish","code":"es","word":"agua","sense":"liquid","roman":"agua"}
]}
""";
        WiktEntry? e = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(e);
        Assert.Equal(2, e!.Translations.Count);
        Assert.Equal("de", e.Translations[0].LangCode);
        Assert.Equal("Wasser", e.Translations[0].Word);
        Assert.Equal("liquid", e.Translations[0].Sense);
        Assert.Equal("es", e.Translations[1].LangCode);
        Assert.Equal("agua", e.Translations[1].Roman);
    }

    [Fact]
    public void ParseLine_AllSemanticRelations_Parsed()
    {
        const string line = """
{"word":"dog","lang_code":"en","pos":"noun",
 "synonyms":[{"word":"canine"}],
 "antonyms":[{"word":"cat"}],
 "hypernyms":[{"word":"animal"}],
 "hyponyms":[{"word":"poodle"},{"word":"labrador"}],
 "meronyms":[{"word":"tail"}],
 "coordinate_terms":[{"word":"wolf"}],
 "derived":[{"word":"doghouse"}],
 "related":[{"word":"hound"}]}
""";
        WiktEntry? e = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(e);
        Assert.Single(e!.Synonyms);
        Assert.Equal("canine", e.Synonyms[0].Word);
        Assert.Single(e.Antonyms);
        Assert.Equal("cat", e.Antonyms[0].Word);
        Assert.Single(e.Hypernyms);
        Assert.Equal(2, e.Hyponyms.Count);
        Assert.Single(e.Meronyms);
        Assert.Single(e.CoordinateTerms);
        Assert.Single(e.Derived);
        Assert.Equal("hound", e.Related[0].Word);
    }

    [Fact]
    public void ParseLine_EtymologyTemplates_ArgsExtracted()
    {
        const string line = """
{"word":"example","lang_code":"en","pos":"noun","etymology_number":1,"etymology_text":"From Latin exemplum.","etymology_templates":[
  {"name":"inh","args":{"1":"en","2":"exemplum","3":"example"},"expansion":"Latin exemplum"},
  {"name":"m","args":{"1":"la","2":"exemplum"}}
]}
""";
        WiktEntry? e = WiktionaryJsonlParser.ParseLine(line);
        Assert.NotNull(e);
        Assert.Equal(1, e!.EtymologyNumber);
        Assert.Equal("From Latin exemplum.", e.EtymologyText);
        Assert.Equal(2, e.EtymologyTemplates.Count);
        Assert.Equal("inh", e.EtymologyTemplates[0].Name);
        Assert.Equal("en", e.EtymologyTemplates[0].Args["1"]);
        Assert.Equal("exemplum", e.EtymologyTemplates[0].Args["2"]);
        Assert.Equal("Latin exemplum", e.EtymologyTemplates[0].Expansion);
        Assert.Equal("m", e.EtymologyTemplates[1].Name);
    }

    [Fact]
    public void Parse_MultipleLines_SkipsBlanksAndMalformed()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"wikt-test-{System.Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, string.Join('\n',
            [
                """{"word":"a","lang_code":"en","pos":"noun"}""",
                "",
                "   ",
                """{"word":"b","lang_code":"en","pos":"verb"}""",
                """{"word":"c"}""",                     // missing required fields → skipped
                """{"word":"d","lang_code":"fr","pos":"noun"}""",
            ]), Encoding.UTF8);

            List<WiktEntry> entries = WiktionaryJsonlParser.Parse(path).ToList();
            Assert.Equal(3, entries.Count);
            Assert.Equal(new[] { "a", "b", "d" }, entries.Select(e => e.Word).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadChunks_AssignsMonotonicIndexesAfterPrefilter()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"wikt-chunks-{System.Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, string.Join('\n',
            [
                """{"word":"a","lang_code":"en","pos":"noun"}""",
                """{"word":"b","lang_code":"fr","pos":"noun"}""",
                """{"word":"c","lang_code":"en","pos":"verb"}""",
                """{"word":"d","lang_code":"en","pos":"noun"}""",
            ]), Encoding.UTF8);

            using WiktionaryJsonlStreamingReader reader = new(path, ["en"]);
            List<WiktionaryJsonlLineChunk> chunks = reader.ReadChunks(2).ToList();

            Assert.Equal(2, chunks.Count);
            Assert.Equal(0, chunks[0].Index);
            Assert.Equal(1, chunks[1].Index);
            Assert.Equal(2, chunks[0].Lines.Count);
            Assert.Single(chunks[1].Lines);
            Assert.All(chunks.SelectMany(c => c.Lines), line => Assert.Contains("\"lang_code\":\"en\"", line));
            Assert.Equal(reader.TotalBytes, chunks[^1].BytesReadAfterChunk);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseLine_NonObjectRoot_ReturnsNull()
    {
        Assert.Null(WiktionaryJsonlParser.ParseLine("[]"));
        Assert.Null(WiktionaryJsonlParser.ParseLine("\"string\""));
        Assert.Null(WiktionaryJsonlParser.ParseLine("42"));
    }
}
