using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for CONFORMER_LAYER tensors. Each outer-index
/// component (per-head, per-conv-channel slice) emits one
/// <c>conformer_component</c> entity hashed by f64-canonical content.
/// Per A0 / A8.
/// </summary>
internal sealed partial class ConformerComponentPass : IModelAnalysisPass
{
    public string PassId => "model.conformer_components";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public ConformerComponentPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.ConformerLayer)
            {
                continue;
            }

            (long emitted, long skipped) = t.Info.Shape.Length switch
            {
                2 => await PerRowContentPass.RunPerRowAsync(
                        context, session, t,
                        canonicalKindTag4: "cnfc",
                        entityTypeCode: "conformer_component",
                        edgeTypeCode: "has_conformer_component",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
                _ => await PerRowContentPass.RunPerOuterIndexAsync(
                        context, session, t,
                        canonicalKindTag4: "cnfc",
                        entityTypeCode: "conformer_component",
                        edgeTypeCode: "has_conformer_component",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
            };

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[conformer-component {ModelId}] complete — {TotalEmitted} components, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
