using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for DETR-style object-query slot tables. Each
/// row of an OBJECT_QUERY tensor is one learned query slot — the
/// direction in residual-stream space that selects one object from a
/// scene. Hashed by f64-canonical row content. Per A0.
/// </summary>
internal sealed partial class ObjectQueryPass : IModelAnalysisPass
{
    public string PassId => "model.object_queries";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public ObjectQueryPass(ILogger logger)
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

            if (t.Classification.Role != TensorRole.ObjectQuery)
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerRowAsync(
                context, session, t,
                canonicalKindTag4: "objq",
                entityTypeCode: "object_query_slot",
                edgeTypeCode: "has_object_query",
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
        [LoggerMessage(Level = LogLevel.Information, Message = "[object-query {ModelId}] complete — {TotalEmitted} slots, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
