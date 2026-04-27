using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for classification heads. Each row of a
/// CLASS_HEAD tensor is the projection direction for one class label
/// (cosine direction in residual-stream that selects this class).
/// Hashed by f64-canonical row content. Per A0.
/// </summary>
internal sealed partial class ClassHeadPass : IModelAnalysisPass
{
    public string PassId => "model.class_projections";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public ClassHeadPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.ClassHead)
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "clsp",
                entityTypeCode: "class_projection",
                edgeTypeCode: "has_class_projection",
                sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                ct);

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[class-head {ModelId}] complete — {TotalEmitted} class projections, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
