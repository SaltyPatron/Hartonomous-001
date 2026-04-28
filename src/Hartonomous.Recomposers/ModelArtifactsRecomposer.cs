using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// Recomposes a model package's text artifacts (config.json, tokenizer.json,
/// tokenizer_config.json, special_tokens_map.json, merges.txt,
/// chat_template.jinja, generation_config.json, README.md) from the substrate.
///
/// Walks the typed structural edges emitted by ModelTextArtifactsPass during
/// safetensors ingestion (has_config_artifact, has_tokenizer_artifact, etc.)
/// from a model_architecture entity to its linked text_composition documents,
/// then uses the substrate's recompose_text path (or the C# tree walker) to
/// reconstitute exact original UTF-8 bytes.
///
/// Hash-as-PK throughout — addresses every entity by composite handle.
/// </summary>
public sealed class ModelArtifactsRecomposer : BaseRecomposer<ModelArtifactsPackage>
{
    private static readonly (string EdgeCode, string FileName)[] Artifacts =
    [
        ("has_config_artifact",             "config.json"),
        ("has_tokenizer_artifact",          "tokenizer.json"),
        ("has_tokenizer_config_artifact",   "tokenizer_config.json"),
        ("has_special_tokens_artifact",     "special_tokens_map.json"),
        ("has_merges_artifact",             "merges.txt"),
        ("has_chat_template_artifact",      "chat_template.jinja"),
        ("has_generation_config_artifact",  "generation_config.json"),
        ("has_readme_artifact",             "README.md"),
    ];

    public ModelArtifactsRecomposer(IEntityReader entityReader) : base(entityReader)
    {
    }

    public override Modality OutputModality => Modality.Text;

    public override async Task<ModelArtifactsPackage> RecomposeAsync(
        EntityHandle entity, RecompositionOptions options, CancellationToken ct)
    {
        Dictionary<string, string?> byEdgeCode = new(Artifacts.Length, System.StringComparer.Ordinal);

        foreach ((string edgeCode, string _) in Artifacts)
        {
            ct.ThrowIfCancellationRequested();
            string? text = await RecomposeFirstArtifactAsync(entity, edgeCode, options, ct);
            byEdgeCode[edgeCode] = text;
        }

        return new ModelArtifactsPackage(
            ConfigJson:           byEdgeCode["has_config_artifact"],
            TokenizerJson:        byEdgeCode["has_tokenizer_artifact"],
            TokenizerConfigJson:  byEdgeCode["has_tokenizer_config_artifact"],
            SpecialTokensMapJson: byEdgeCode["has_special_tokens_artifact"],
            MergesTxt:            byEdgeCode["has_merges_artifact"],
            ChatTemplateJinja:    byEdgeCode["has_chat_template_artifact"],
            GenerationConfigJson: byEdgeCode["has_generation_config_artifact"],
            Readme:               byEdgeCode["has_readme_artifact"]);
    }

    public override async Task RecomposeToStreamAsync(
        EntityHandle entity, RecompositionOptions options, Stream output, CancellationToken ct)
    {
        ModelArtifactsPackage pkg = await RecomposeAsync(entity, options, ct);

        JsonWriterOptions writerOptions = new() { Indented = true, SkipValidation = false };
        await using Utf8JsonWriter writer = new(output, writerOptions);
        writer.WriteStartObject();
        WriteIfPresent(writer, "config.json", pkg.ConfigJson);
        WriteIfPresent(writer, "tokenizer.json", pkg.TokenizerJson);
        WriteIfPresent(writer, "tokenizer_config.json", pkg.TokenizerConfigJson);
        WriteIfPresent(writer, "special_tokens_map.json", pkg.SpecialTokensMapJson);
        WriteIfPresent(writer, "merges.txt", pkg.MergesTxt);
        WriteIfPresent(writer, "chat_template.jinja", pkg.ChatTemplateJinja);
        WriteIfPresent(writer, "generation_config.json", pkg.GenerationConfigJson);
        WriteIfPresent(writer, "README.md", pkg.Readme);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private async Task<string?> RecomposeFirstArtifactAsync(
        EntityHandle modelEntity, string edgeCode, RecompositionOptions options, CancellationToken ct)
    {
        IReadOnlyList<EntityHandle> targets = await EntityReader.GetOutboundEdgeTargetsAsync(
            modelEntity, edgeCode, ct);
        if (targets.Count == 0)
        {
            return null;
        }

        EntityHandle documentEntity = targets[0];

        if (EntityReader is ITextRecompositionReader fastReader)
        {
            string? fastText = await fastReader.RecomposeTextAsync(
                documentEntity, options.MaxDepth, ct);
            if (fastText is not null)
            {
                return fastText;
            }
        }

        StringBuilder sb = new();
        await WalkDepthFirstAsync(documentEntity, options.MaxDepth, 0, sb, ct);
        return sb.ToString();
    }

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

    private static void WriteIfPresent(Utf8JsonWriter writer, string filename, string? content)
    {
        if (content is null)
        {
            return;
        }
        writer.WriteString(filename, content);
    }
}
