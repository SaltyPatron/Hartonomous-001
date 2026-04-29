using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// Recomposes text from the substrate without inventing separators.
/// Hash-as-PK throughout: addresses entities by composite handle.
/// Exact reconstruction requires the constituent graph itself to carry every
/// byte span in order, including whitespace and punctuation-only gaps.
/// </summary>
public sealed class TextRecomposer : BaseRecomposer<string>
{
    /// <summary>Entity type codes that are text atoms (leaf nodes).</summary>
    private static readonly HashSet<string> AtomTypes = new(System.StringComparer.Ordinal)
    {
        "codepoint", "grapheme_cluster", "word_form", "lemma", "morpheme",
    };

    public TextRecomposer(IEntityReader entityReader) : base(entityReader)
    {
    }

    public override Modality OutputModality => Modality.Text;

    public override async Task<string> RecomposeAsync(
        EntityHandle entity, RecompositionOptions options, CancellationToken ct)
    {
        if (EntityReader is ITextRecompositionReader fastReader)
        {
            string? fastText = await fastReader.RecomposeTextAsync(entity, options.MaxDepth, ct);
            if (fastText is not null)
            {
                return fastText;
            }
        }

        StringBuilder sb = new();
        await WalkDepthFirstAsync(entity, options.MaxDepth, 0, sb, ct);
        return sb.ToString();
    }

    public override async Task RecomposeToStreamAsync(
        EntityHandle entity, RecompositionOptions options, Stream output, CancellationToken ct)
    {
        string text = await RecomposeAsync(entity, options, ct);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await output.WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Depth-first walk of has_constituent edges. If the entity has a
    /// content label, append it; otherwise recurse into its children.
    /// </summary>
    private async Task WalkDepthFirstAsync(
        EntityHandle entity, int maxDepth, int currentDepth, StringBuilder sb, CancellationToken ct)
    {
        if (currentDepth > maxDepth)
        {
            return;
        }

        IReadOnlyDictionary<EntityHandle, EntityInfo> info =
            await GetEntityInfoAsync([entity], ct);

        if (!info.TryGetValue(entity, out EntityInfo? entityInfo))
        {
            return;
        }

        if (entityInfo.ContentLabel is not null)
        {
            sb.Append(entityInfo.ContentLabel);
            return;
        }

        if (AtomTypes.Contains(entityInfo.EntityTypeCode))
        {
            return;
        }

        IReadOnlyList<(EntityHandle Child, int Position)> children = await GetChildrenAsync(entity, ct);
        if (children.Count == 0)
        {
            return;
        }

        foreach ((EntityHandle child, int _) in children)
        {
            await WalkDepthFirstAsync(child, maxDepth, currentDepth + 1, sb, ct);
        }
    }
}
