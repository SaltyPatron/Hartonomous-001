using System;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// Cheap deterministic classifier for prompt intent. Pattern-matches on
/// lead words / shape; no model. Wrong classifications fall back to
/// <see cref="PromptIntent.Lookup"/> which uses uniform arena weighting,
/// so a misclassification can never silently bias the substrate's response
/// — at worst it loses a small amount of focus.
///
/// The right long-term home for this is a learned Glicko-2 weighting per
/// arena per detected pattern, fed by the OutcomeRecorder. The classifier
/// is the fast first pass; the arena ratings provide the calibration.
/// </summary>
public static class PromptIntentClassifier
{
    public static PromptIntent Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PromptIntent.Lookup;
        }

        string s = text.Trim().ToLowerInvariant();

        if (s.StartsWith("translate ", StringComparison.Ordinal)
            || s.Contains(" in french", StringComparison.Ordinal)
            || s.Contains(" in spanish", StringComparison.Ordinal)
            || s.Contains(" in german", StringComparison.Ordinal)
            || s.Contains(" translate to ", StringComparison.Ordinal))
        {
            return PromptIntent.Translation;
        }

        if (s.StartsWith("how ", StringComparison.Ordinal)
            || s.StartsWith("explain ", StringComparison.Ordinal)
            || s.StartsWith("describe how ", StringComparison.Ordinal))
        {
            return PromptIntent.HowTo;
        }

        if (s.StartsWith("is ", StringComparison.Ordinal)
            || s.StartsWith("are ", StringComparison.Ordinal)
            || s.StartsWith("do ", StringComparison.Ordinal)
            || s.StartsWith("does ", StringComparison.Ordinal)
            || s.StartsWith("did ", StringComparison.Ordinal)
            || s.StartsWith("can ", StringComparison.Ordinal)
            || s.StartsWith("could ", StringComparison.Ordinal)
            || s.StartsWith("should ", StringComparison.Ordinal)
            || s.StartsWith("would ", StringComparison.Ordinal))
        {
            return PromptIntent.YesNo;
        }

        if (s.StartsWith("list ", StringComparison.Ordinal)
            || s.StartsWith("name all ", StringComparison.Ordinal)
            || s.StartsWith("what are the ", StringComparison.Ordinal)
            || s.StartsWith("which are ", StringComparison.Ordinal)
            || s.StartsWith("enumerate ", StringComparison.Ordinal))
        {
            return PromptIntent.Enumeration;
        }

        if (s.StartsWith("what is ", StringComparison.Ordinal)
            || s.StartsWith("what's ", StringComparison.Ordinal)
            || s.StartsWith("what does ", StringComparison.Ordinal)
            || s.StartsWith("what do ", StringComparison.Ordinal)
            || s.StartsWith("define ", StringComparison.Ordinal)
            || s.StartsWith("describe ", StringComparison.Ordinal)
            || s.StartsWith("tell me about ", StringComparison.Ordinal)
            || s.StartsWith("what was ", StringComparison.Ordinal)
            || s.StartsWith("what were ", StringComparison.Ordinal)
            || s.StartsWith("who is ", StringComparison.Ordinal)
            || s.StartsWith("who was ", StringComparison.Ordinal))
        {
            return PromptIntent.Definition;
        }

        // Bare term ("dog", "highrise", "minute") falls through to Lookup.
        return PromptIntent.Lookup;
    }
}
