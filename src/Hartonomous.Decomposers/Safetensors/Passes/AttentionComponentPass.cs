using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for attention transformation tensors:
/// AttentionQuery, AttentionKey, AttentionValue, AttentionOutput.
///
/// Each row is one output direction of the attention projection. Emitted
/// as one <c>attention_component</c> entity per row, hashed by f64-
/// canonical row content. Same row across models → ONE entity → cross-
/// model Glicko-2 corroboration on attention components. The recomposer
/// reads these at distillation to materialize target Q/K/V/O matrices.
///
/// Distinct from AttentionArchetypePass: that pass produces a 10-feature
/// SIGNATURE per attention head (a retrieval index for "which heads compute
/// similar patterns"). THIS pass produces FULL ROW CONTENT for distillation.
///
/// Per docs/specs/decomposers/analysis-passes.md A4 and
/// .claude/rules/35-inference-and-godel.md.
/// </summary>
internal sealed partial class AttentionComponentPass : IModelAnalysisPass
{
    public string PassId => "model.attention_components";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public AttentionComponentPass(ILogger logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        long totalEmitted = 0;
        long totalSkipped = 0;

        foreach (TensorHandle t in context.Tensors)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsAttentionRole(t.Classification.Role))
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "atnc",
                entityTypeCode: "attention_pattern",
                edgeTypeCode: "has_attention_component",
                sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                ct);

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static bool IsAttentionRole(TensorRole role) => role switch
    {
        TensorRole.AttentionQuery => true,
        TensorRole.AttentionKey => true,
        TensorRole.AttentionValue => true,
        TensorRole.AttentionOutput => true,
        TensorRole.CrossAttention => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[attention-component {ModelId}] complete — {TotalEmitted} entities, {TotalSkipped} rows skipped sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
