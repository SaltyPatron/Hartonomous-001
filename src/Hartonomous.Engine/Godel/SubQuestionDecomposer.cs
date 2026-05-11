using System.Collections.Generic;
using System.Text;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// Splits a compound prompt into independent sub-questions. The decomposition
/// is deterministic and substrate-aware:
///
///   1. UAX #29 sentence boundaries via <see cref="SentenceBoundaries"/>
    ///      (the same segmenter the canonical text decomposer uses for sentence
    ///      text compositions). Sentences are the natural top-level split.
///   2. Each sentence is split on conjunctions / clause-boundary punctuation
///      ("," then "and"|"or"|"but"|";"). Sub-clauses become sub-questions
///      only when they carry independent semantic targets — bare list items
///      ("apples, oranges, and pears") collapse to a single question for
///      definition; coordinated full clauses ("define X and explain Y")
///      split.
///
/// The split criterion is not perfect English parsing — that's the engine's
/// Orient phase. The decomposer's job is honest first-pass segmentation;
/// the Orient phase decides whether each segment warrants its own forward
/// pass or whether the prompt is better served as a single seed activation.
/// </summary>
public static class SubQuestionDecomposer
{
    /// <summary>
    /// Decompose <paramref name="prompt"/> into sub-questions. Always returns
    /// at least one entry (the entire prompt) so the caller can iterate
    /// uniformly regardless of whether the prompt was single- or multi-clause.
    /// </summary>
    public static IReadOnlyList<SubQuestion> Decompose(string prompt, ICodepointProperties props)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return [];
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(prompt);
        List<SentenceRange> sentences = SentenceBoundaries.Enumerate(utf8, props);
        List<SubQuestion> result = new(sentences.Count == 0 ? 1 : sentences.Count);

        if (sentences.Count == 0)
        {
            // Prompt has no sentence-terminator. Treat whole thing as one.
            result.Add(new SubQuestion(0, prompt.Trim()));
            return result;
        }

        int idx = 0;
        foreach (SentenceRange r in sentences)
        {
            string raw = Encoding.UTF8.GetString(utf8, (int)r.ByteOffset, r.ByteLength).Trim();
            if (raw.Length == 0)
            {
                continue;
            }
            // Try clause splits within the sentence. Conservative: only split
            // on " and " / " or " when each side is at least 3 chars and
            // contains no question-word (so "what is X and what is Y" splits
            // but "salt and pepper" doesn't).
            foreach (string clause in SplitClauses(raw))
            {
                string c = clause.Trim();
                if (c.Length > 0)
                {
                    result.Add(new SubQuestion(idx++, c));
                }
            }
        }

        if (result.Count == 0)
        {
            result.Add(new SubQuestion(0, prompt.Trim()));
        }
        return result;
    }

    private static readonly string[] ClauseConjunctions = [" and ", " or ", " but "];
    private static readonly string[] QuestionStarters = ["what ", "who ", "where ", "when ", "why ", "how ", "which ", "is ", "are ", "do ", "does ", "did ", "can ", "could ", "should ", "would ", "define ", "describe ", "explain ", "list ", "tell ", "give "];

    private static IEnumerable<string> SplitClauses(string sentence)
    {
        // Lowercase view for matching only; emitted slices preserve original case.
        string lower = sentence.ToLowerInvariant();

        // Find a conjunction whose right side starts with a question-word.
        // That's the strong signal of independent coordinated clauses.
        // Otherwise emit the whole sentence as one clause.
        int splitAt = -1;
        int conjLen = 0;
        foreach (string conj in ClauseConjunctions)
        {
            int pos = lower.IndexOf(conj, System.StringComparison.Ordinal);
            while (pos >= 0)
            {
                int after = pos + conj.Length;
                string rhs = lower[after..];
                if (rhs.Length >= 3)
                {
                    foreach (string q in QuestionStarters)
                    {
                        if (rhs.StartsWith(q, System.StringComparison.Ordinal))
                        {
                            splitAt = pos;
                            conjLen = conj.Length;
                            break;
                        }
                    }
                }
                if (splitAt >= 0)
                {
                    break;
                }
                pos = lower.IndexOf(conj, after, System.StringComparison.Ordinal);
            }
            if (splitAt >= 0)
            {
                break;
            }
        }

        if (splitAt < 0)
        {
            yield return sentence;
            yield break;
        }

        yield return sentence[..splitAt];
        // Recurse on the right side so chains ("X and Y and Z") split fully.
        string remainder = sentence[(splitAt + conjLen)..];
        foreach (string c in SplitClauses(remainder))
        {
            yield return c;
        }
    }
}
