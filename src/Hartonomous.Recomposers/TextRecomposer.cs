using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// Recomposes text from the substrate without inventing separators.
/// Exact reconstruction requires the sequence graph itself to carry every byte span
/// in order, including whitespace and punctuation-only gaps.
/// </summary>
public sealed class TextRecomposer : BaseRecomposer<string>
{
    /// <summary>Entity type codes that are text atoms (leaf nodes).</summary>
    private static readonly HashSet<string> AtomTypes = new(StringComparer.Ordinal)
    {
        "codepoint", "grapheme_cluster", "word_form", "lemma", "morpheme",
        "bpe_token", "ud_token",
    };

    public TextRecomposer(IEntityReader entityReader) : base(entityReader)
    {
    }

    public override Modality OutputModality => Modality.Text;

    public override async Task<string> RecomposeAsync(
        long entityId, RecompositionOptions options, CancellationToken ct)
    {
        if (EntityReader is ITextRecompositionReader fastReader)
        {
            string? fastText = await fastReader.RecomposeTextAsync(entityId, options.MaxDepth, ct);
            if (fastText is not null)
            {
                return fastText;
            }
        }

        StringBuilder sb = new();
        await WalkDepthFirstAsync(entityId, options.MaxDepth, 0, sb, ct);
        return sb.ToString();
    }

    public override async Task RecomposeToStreamAsync(
        long entityId, RecompositionOptions options, Stream output, CancellationToken ct)
    {
        string text = await RecomposeAsync(entityId, options, ct);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await output.WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Depth-first walk of the sequence table. If the entity is an atom,
    /// append its content label. If it's a composition, recurse into children.
    /// </summary>
    private async Task WalkDepthFirstAsync(
        long entityId, int maxDepth, int currentDepth, StringBuilder sb, CancellationToken ct)
    {
        if (currentDepth > maxDepth)
        {
            return;
        }

        // Get entity info for this node.
        IReadOnlyDictionary<long, EntityInfo> info =
            await GetEntityInfoAsync([entityId], ct);

        if (!info.TryGetValue(entityId, out EntityInfo? entity))
        {
            return;
        }

        // If the database can label this entity directly, that label is already
        // the exact textual value of the subtree rooted here.
        if (entity.ContentLabel is not null)
        {
            sb.Append(entity.ContentLabel);
            return;
        }

        // If atom type has no label, we can't render it.
        if (AtomTypes.Contains(entity.EntityTypeCode))
        {
            return;
        }

        // Composition: get children in sequence order and recurse.
        IReadOnlyList<(long ChildEntityId, int Position)> children =
            await GetChildrenAsync(entityId, ct);

        if (children.Count == 0)
        {
            return;
        }

        for (int i = 0; i < children.Count; i++)
        {
            await WalkDepthFirstAsync(
                children[i].ChildEntityId, maxDepth, currentDepth + 1, sb, ct);
        }
    }

}
