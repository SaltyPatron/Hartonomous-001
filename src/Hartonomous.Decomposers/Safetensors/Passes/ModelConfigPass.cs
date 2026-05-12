using System.Text;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-config-key parametric edges from the orchestrator-detected
/// <see cref="ModelArchitecture"/> record. Spec §V.4 ModelConfigDecomposer:
/// every architectural parameter that the synthesizer needs to walk the
/// stored model tree at recompose time lands as a typed substrate edge with
/// a content-addressed text_composition target.
///
/// Why typed edges and not just text artifacts: the synthesizer (Phase C)
/// reads architecture parameters from substrate via SQL — querying a
/// well-known edge_type is microsecond fast; parsing a JSON blob stored as
/// a text artifact at query time is not. Both layers exist in parallel:
/// <see cref="ModelTextArtifactsPass"/> records the raw config.json content
/// for provenance and full-fidelity replay; this pass records the parsed
/// values as queryable structural edges.
///
/// Edges emitted (model_architecture → text_composition with integer encoded
/// as decimal UTF-8):
/// <list type="bullet">
/// <item><c>has_hidden_size</c></item>
/// <item><c>has_num_layers</c></item>
/// <item><c>has_num_attention_heads</c></item>
/// <item><c>has_vocab_size</c></item>
/// </list>
///
/// Cross-model dedup: the text_composition for "4096" is content-addressed,
/// so every model with hidden_size=4096 attaches a has_hidden_size edge to
/// the SAME text_composition entity. Queries like "all models with
/// hidden_size=4096" become an indexed traversal from that one
/// text_composition outward via reverse-edge lookup.
///
/// IntermediateSize and MaxPositionEmbeddings are also captured by the
/// detector but their corresponding edge_types are not seeded yet — when
/// the synthesizer needs them they get added to <c>edge_type.sql</c> and
/// emitted here. Out-of-scope for this pass until then.
/// </summary>
internal sealed partial class ModelConfigPass : IMetadataDecomposerPass
{
    public string PassId => "model.config_parametric_edges";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    public IReadOnlyList<string> AcceptedFilePatterns =>
        ["config.json", "generation_config.json"];

    private const double ModelDerivedTrustMu = 60_000.0;

    private readonly ILogger _logger;

    public ModelConfigPass(ILogger logger)
    {
        _logger = logger;
    }

    public Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        ModelArchitecture arch = context.Architecture.Architecture;
        EntityHandle modelEntity = session.ModelEntity;
        IIngestionBatch batch = session.Batch;
        string provenance = context.ProvenanceCode;

        int emitted = 0;

        if (arch.HiddenSize > 0)
        {
            EmitParametricEdge(batch, modelEntity, "has_hidden_size", arch.HiddenSize, provenance);
            emitted++;
        }
        if (arch.NumLayers > 0)
        {
            EmitParametricEdge(batch, modelEntity, "has_num_layers", arch.NumLayers, provenance);
            emitted++;
        }
        if (arch.NumAttentionHeads > 0)
        {
            EmitParametricEdge(batch, modelEntity, "has_num_attention_heads", arch.NumAttentionHeads, provenance);
            emitted++;
        }
        if (arch.VocabSize > 0)
        {
            EmitParametricEdge(batch, modelEntity, "has_vocab_size", arch.VocabSize, provenance);
            emitted++;
        }

        Log.PassComplete(_logger, context.Source.ModelId, emitted);
        return Task.CompletedTask;
    }

    private static void EmitParametricEdge(
        IIngestionBatch batch,
        EntityHandle modelEntity,
        string edgeCode,
        int value,
        string provenanceCode)
    {
        // Content-addressed integer text: same value across models → same hash → same entity.
        byte[] valueBytes = Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        TextDecomposeResult result = SubstrateTextDecomposer.EmitStatic(
            batch,
            valueBytes,
            new TextDecomposeOptions(
                ProvenanceCode: provenanceCode,
                TopEntityType: "text_composition",
                TrustMu: ModelDerivedTrustMu));

        batch.AddEdge(edgeCode, provenanceCode,
        [
            new EdgeMemberSpec(modelEntity, "source", 0),
            new EdgeMemberSpec(result.RootHandle, "target", 1),
        ]);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[model-config {ModelId}] complete — {Edges} parametric edges emitted")]
        public static partial void PassComplete(ILogger logger, string modelId, int edges);
    }
}
