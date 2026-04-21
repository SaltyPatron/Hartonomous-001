using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hartonomous.Decomposers.Ud;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Ud;

public sealed class UdConllUParserTests
{
    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"ud-test-{System.Guid.NewGuid():N}.conllu");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void Parse_SingleSentence_ReturnsTokensInOrder()
    {
        const string src = """
# sent_id = test-1
# text = The cat sat.
1	The	the	DET	DT	Definite=Def|PronType=Art	2	det	_	_
2	cat	cat	NOUN	NN	Number=Sing	3	nsubj	_	_
3	sat	sit	VERB	VBD	Mood=Ind|Tense=Past|VerbForm=Fin	0	root	_	_
4	.	.	PUNCT	.	_	3	punct	_	_

""";
        string path = WriteTemp(src);
        try
        {
            List<UdSentenceRecord> sents = UdConllUParser.Parse(path).ToList();
            Assert.Single(sents);
            UdSentenceRecord s = sents[0];
            Assert.Equal("test-1", s.SentId);
            Assert.Equal("The cat sat.", s.Text);
            Assert.Equal(4, s.Tokens.Count);
            Assert.Equal("DET", s.Tokens[0].Upos);
            Assert.Equal("nsubj", s.Tokens[1].Deprel);
            Assert.Equal("root", s.Tokens[2].Deprel);
            Assert.Equal("0", s.Tokens[2].Head);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_FeatsColumn_SplitsKeyValuePairs()
    {
        const string src = """
# sent_id = feats-1
1	zebras	zebra	NOUN	_	Animacy=Anim|Case=Nom|Number=Plur	0	root	_	_

""";
        string path = WriteTemp(src);
        try
        {
            UdSentenceRecord s = UdConllUParser.Parse(path).Single();
            UdTokenRecord t = s.Tokens.Single();
            Assert.Equal(3, t.Feats.Count);
            Assert.Contains(t.Feats, f => f.Key == "Animacy" && f.Value == "Anim");
            Assert.Contains(t.Feats, f => f.Key == "Case" && f.Value == "Nom");
            Assert.Contains(t.Feats, f => f.Key == "Number" && f.Value == "Plur");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_UnderscoreSentinel_BecomesNull()
    {
        const string src = """
# sent_id = null-1
1	_	_	_	_	_	_	_	_	_

""";
        string path = WriteTemp(src);
        try
        {
            UdTokenRecord t = UdConllUParser.Parse(path).Single().Tokens.Single();
            // FORM column is preserved as "_" literal (never null) per CoNLL-U contract;
            // all other nullable fields become null.
            Assert.Null(t.Lemma);
            Assert.Null(t.Upos);
            Assert.Null(t.Xpos);
            Assert.Null(t.Head);
            Assert.Null(t.Deprel);
            Assert.Null(t.Deps);
            Assert.Null(t.Misc);
            Assert.Empty(t.Feats);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_MultipleSentencesAndCommentsAndBlankLines_SplitsCorrectly()
    {
        const string src = """
# newdoc id = doc-1
# sent_id = s1
# text = Hi.
1	Hi	hi	INTJ	_	_	0	root	_	_
2	.	.	PUNCT	_	_	1	punct	_	_

# sent_id = s2
# text = Bye.
1	Bye	bye	INTJ	_	_	0	root	_	_
2	.	.	PUNCT	_	_	1	punct	_	_

""";
        string path = WriteTemp(src);
        try
        {
            List<UdSentenceRecord> sents = UdConllUParser.Parse(path).ToList();
            Assert.Equal(2, sents.Count);
            Assert.Equal("s1", sents[0].SentId);
            Assert.Equal("s2", sents[1].SentId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_NoTrailingBlankLine_StillYieldsFinalSentence()
    {
        const string src =
            "# sent_id = no-trailing\n" +
            "1\tEnd\tend\tNOUN\t_\t_\t0\troot\t_\t_";
        string path = WriteTemp(src);
        try
        {
            UdSentenceRecord s = UdConllUParser.Parse(path).Single();
            Assert.Equal("no-trailing", s.SentId);
            Assert.Single(s.Tokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_MissingSentIdComment_FallsBackToOrdinal()
    {
        const string src = """
1	Orphan	orphan	NOUN	_	_	0	root	_	_

""";
        string path = WriteTemp(src);
        try
        {
            UdSentenceRecord s = UdConllUParser.Parse(path).Single();
            Assert.StartsWith("ord-", s.SentId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
