using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for bounding-box heads. Each row of a BBOX_HEAD
/// tensor is the projection direction for one (coord, bin) slot in the
/// localization output. Hashed by f64-canonical row content. Per A0.
/// </summary>
internal sealed partial class BboxHeadPass : IModelAnalysisPass
{
    public string PassId => "model.bbox_projections";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public BboxHeadPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.BboxHead)
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "bbxp",
                entityTypeCode: "bbox_projection",
                edgeTypeCode: "has_bbox_projection",
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[bbox-head {ModelId}] complete — {TotalEmitted} bbox projections, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
