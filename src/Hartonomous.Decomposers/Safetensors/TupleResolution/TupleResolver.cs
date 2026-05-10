using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Hartonomous.Decomposers.Safetensors.Passes;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §III + §VI. Walks the model's
/// tensor list, runs each tensor name through the matching architecture
/// profile(s), accumulates per-tensor TensorClassification, then groups
/// classifications into ResolvedTuples by (Tuple, LayerIdx, HeadIdx,
/// ExpertIdx, AdapterName) for downstream TuplePass dispatch.
///
/// PEFT LoRA wrap is auto-detected by tensor-name pattern (presence of
/// .lora_A. / .lora_B. / .base_layer.); when detected, the LoRA profile
/// composes with the base architecture profile.
/// </summary>
public sealed class TupleResolver
{
    private readonly IReadOnlyList<IArchitectureProfile> _profiles;
    private readonly PeftLoraArchitectureProfile _peft = new();

    public TupleResolver()
        : this(new IArchitectureProfile[]
        {
            new BertArchitectureProfile(),
            new LlamaArchitectureProfile(),
            new Qwen3MoeArchitectureProfile(),
        })
    {
    }

    public TupleResolver(IReadOnlyList<IArchitectureProfile> profiles)
    {
        _profiles = profiles;
    }

    /// <summary>
    /// Resolve the model's tensor list into per-tensor classifications and
    /// per-tuple groupings. Returns (per-tensor classifications keyed by
    /// tensor handle, ordered list of resolved tuples).
    /// </summary>
    public (IReadOnlyDictionary<TensorHandle, TensorClassification> Classifications,
            IReadOnlyList<ResolvedTuple> Tuples)
        Resolve(string architectureClass, IReadOnlyList<TensorHandle> tensors)
    {
        IArchitectureProfile? baseProfile = null;
        foreach (IArchitectureProfile p in _profiles)
        {
            if (p.Matches(architectureClass)) { baseProfile = p; }
            // Note: Qwen3MoE.Matches and Llama.Matches have overlap — Qwen3MoE
            // takes precedence because it appears later in the profile list.
            // For composed dispatch (Qwen3MoE inherits Llama monolith parts),
            // we run BOTH below.
        }

        bool peftWrapped = DetectPeftWrap(tensors);

        // Build the active profile chain: base profile + Llama (if Qwen3MoE)
        // + PEFT wrap (if detected).
        List<IArchitectureProfile> active = new();
        if (baseProfile is Qwen3MoeArchitectureProfile)
        {
            // MoE inherits Llama monolith attention/embedding/lm_head; run both.
            active.Add(new LlamaArchitectureProfile());
            active.Add(baseProfile);
        }
        else if (baseProfile is not null)
        {
            active.Add(baseProfile);
        }
        if (peftWrapped) { active.Add(_peft); }

        Dictionary<TensorHandle, TensorClassification> classifications = new();
        Dictionary<string, List<TupleMember>> tupleBuckets = new();
        Dictionary<string, (ArchetypeTuple Tuple, ModalityHint Modality, int? L, int? H, int? E)> tupleMeta = new();

        foreach (TensorHandle t in tensors)
        {
            string name = t.Info.Name;
            // PEFT prefix peel for inner-rule matching.
            string innerName = name;
            if (peftWrapped && innerName.StartsWith(_peft.PrefixToStrip!, StringComparison.Ordinal))
            {
                innerName = innerName.Substring(_peft.PrefixToStrip!.Length);
            }

            TensorClassification? classification = ResolveTensor(t, name, innerName, active);
            if (classification is null) { continue; }
            classifications[t] = classification;

            string tupleId = BuildTupleId(classification);
            if (!tupleBuckets.TryGetValue(tupleId, out List<TupleMember>? bucket))
            {
                bucket = new List<TupleMember>();
                tupleBuckets[tupleId] = bucket;
                tupleMeta[tupleId] = (classification.Tuple, classification.Modality,
                    classification.LayerIndex, classification.HeadIndex, classification.ExpertIndex);
            }
            bucket.Add(new TupleMember(classification.Slot, t, FusedSplit: null));
        }

        List<ResolvedTuple> tuples = new(tupleBuckets.Count);
        foreach ((string id, List<TupleMember> members) in tupleBuckets)
        {
            (ArchetypeTuple tuple, ModalityHint modality, int? l, int? h, int? e) = tupleMeta[id];
            tuples.Add(new ResolvedTuple(id, tuple, modality, SecondaryModality: null, l, h, e, members));
        }

        return (classifications, tuples);
    }

    private static TensorClassification? ResolveTensor(
        TensorHandle handle, string outerName, string innerName,
        IReadOnlyList<IArchitectureProfile> activeProfiles)
    {
        // Try each profile's rules; first match wins. Order matters: more-specific
        // profiles (Qwen3MoE) run after their parents (Llama) and override the
        // base classification when a more-specific rule matches the same name.
        TensorClassification? best = null;
        foreach (IArchitectureProfile profile in activeProfiles)
        {
            string matchName = (profile.PrefixToStrip is not null && outerName.StartsWith(profile.PrefixToStrip, StringComparison.Ordinal))
                ? outerName.Substring(profile.PrefixToStrip.Length)
                : (profile is PeftLoraArchitectureProfile ? outerName : innerName);

            foreach (NamePatternRule rule in profile.Rules)
            {
                Match m = rule.Pattern.Match(matchName);
                if (!m.Success) { continue; }
                int? layerIdx = ExtractInt(m, rule.LayerGroupName);
                int? headIdx = ExtractInt(m, rule.HeadGroupName);
                int? expertIdx = ExtractInt(m, rule.ExpertGroupName);
                best = new TensorClassification(
                    rule.Primitive, rule.Tuple, rule.Slot,
                    layerIdx, headIdx, expertIdx,
                    rule.Modality, AdaptationOf: null);
                break;  // first-match-wins within profile
            }
        }
        return best;
    }

    private static int? ExtractInt(Match m, string? groupName)
    {
        if (groupName is null) { return null; }
        Group g = m.Groups[groupName];
        if (!g.Success || string.IsNullOrEmpty(g.Value)) { return null; }
        return int.TryParse(g.Value, out int v) ? v : null;
    }

    private static string BuildTupleId(TensorClassification c)
    {
        // Same tuple at the same placement and modality gets the same ID.
        // Members of one attention block bucket together; per-expert FFNs
        // bucket per (layer, expert). Modality is part of the key so that
        // tuples whose only distinguishing axis is modality (e.g. distinct
        // EmbeddingLookup tables for text vs position vs codebook) do not
        // collide into one bucket and hand the downstream pass a Table member
        // from the wrong modality. The original BERT bug was three embedding
        // tables (word/position/token_type) all bucketing into one tuple and
        // the pass picking position_embeddings (vocab=512) instead of
        // word_embeddings (vocab=30522) by alphabetical tensor order.
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        string layer = c.LayerIndex?.ToString(inv) ?? "_";
        string head = c.HeadIndex?.ToString(inv) ?? "_";
        string expert = c.ExpertIndex?.ToString(inv) ?? "_";
        return $"{c.Tuple}:L{layer}:H{head}:E{expert}:M{c.Modality}";
    }

    private static bool DetectPeftWrap(IReadOnlyList<TensorHandle> tensors)
    {
        foreach (TensorHandle t in tensors)
        {
            string n = t.Info.Name;
            if (n.Contains(".lora_A.", StringComparison.Ordinal)
                || n.Contains(".lora_B.", StringComparison.Ordinal)
                || n.Contains(".base_layer.", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
