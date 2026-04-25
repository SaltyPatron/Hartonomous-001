using System.IO;
using System.Text;
using System.Text.Json;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// Recomposes a model package's text artifacts (config.json, tokenizer.json,
/// tokenizer_config.json, special_tokens_map.json, merges.txt,
/// chat_template.jinja, generation_config.json, README.md) from the substrate.
///
/// Walks the typed structural edges emitted by <c>ModelTextArtifactsPass</c>
/// during safetensors ingestion (migration 0041:
/// <c>has_config_artifact</c>, <c>has_tokenizer_artifact</c>, etc.) from a
/// model_architecture entity to its linked text_composition documents, then
/// uses the substrate's <c>recompose_text</c> path (or the C# tree walker)
/// to reconstitute exact original UTF-8 bytes.
///
/// The output serializes to a JSON object whose keys are the original artifact
/// filenames; <see cref="RecomposeToStreamAsync"/> writes that JSON. The
/// in-memory <see cref="ModelArtifactsPackage"/> is also surfaced via
/// <see cref="RecomposeAsync"/> for callers (e.g. a SafetensorsRecomposer)
/// that want each artifact addressable by name.
/// </summary>
public sealed class ModelArtifactsRecomposer : BaseRecomposer<ModelArtifactsPackage>
{
    // Edge code → property selector. Order matches the ingestion pass for
    // determinism (Law #6: same substrate state + same recomposer version
    // → same output bytes).
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
        long entityId, RecompositionOptions options, CancellationToken ct)
    {
        Dictionary<string, string?> byEdgeCode = new(Artifacts.Length, StringComparer.Ordinal);

        foreach ((string edgeCode, string _) in Artifacts)
        {
            ct.ThrowIfCancellationRequested();
            string? text = await RecomposeFirstArtifactAsync(
                entityId, edgeCode, options, ct);
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
        long entityId, RecompositionOptions options, Stream output, CancellationToken ct)
    {
        ModelArtifactsPackage pkg = await RecomposeAsync(entityId, options, ct);

        // Emit a stable JSON map: filename → recomposed text. Null artifacts
        // are omitted so consumers can distinguish "shipped but empty" from
        // "never shipped". UTF-8, no BOM, indented for readability.
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
        long modelEntityId, string edgeCode, RecompositionOptions options, CancellationToken ct)
    {
        IReadOnlyList<long> targets = await EntityReader.GetOutboundEdgeTargetsAsync(
            modelEntityId, edgeCode, ct);
        if (targets.Count == 0)
        {
            return null;
        }

        // The substrate enforces (hash, edge_type_id) uniqueness on edges, so
        // multiple has_X_artifact edges from one model entity to one document
        // entity collapse to one row. Multiple targets only happen if the model
        // somehow shipped two distinct artifacts with the same edge type — take
        // the first by edge id (stable order from the SQL).
        long documentEntityId = targets[0];

        if (EntityReader is ITextRecompositionReader fastReader)
        {
            string? fastText = await fastReader.RecomposeTextAsync(
                documentEntityId, options.MaxDepth, ct);
            if (fastText is not null)
            {
                return fastText;
            }
        }

        // Fallback: walk the sequence DAG via the BaseRecomposer helpers.
        // (TextRecomposer's logic, inlined here so this recomposer is
        // self-contained and doesn't reach into the text-modality recomposer.)
        StringBuilder sb = new();
        await WalkDepthFirstAsync(documentEntityId, options.MaxDepth, 0, sb, ct);
        return sb.ToString();
    }

    private async Task WalkDepthFirstAsync(
        long entityId, int maxDepth, int currentDepth, StringBuilder sb, CancellationToken ct)
    {
        if (currentDepth > maxDepth)
        {
            return;
        }

        IReadOnlyDictionary<long, Hartonomous.Core.Engine.EntityInfo> info =
            await GetEntityInfoAsync([entityId], ct);
        if (!info.TryGetValue(entityId, out Hartonomous.Core.Engine.EntityInfo? entity))
        {
            return;
        }

        if (entity.ContentLabel is not null)
        {
            sb.Append(entity.ContentLabel);
            return;
        }

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

    private static void WriteIfPresent(Utf8JsonWriter writer, string filename, string? content)
    {
        if (content is null)
        {
            return;
        }
        writer.WriteString(filename, content);
    }
}
