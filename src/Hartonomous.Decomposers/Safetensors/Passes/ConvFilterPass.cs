using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for CONV_KERNEL and VAE_BLOCK tensors. A 4-D
/// conv weight has shape [out_c, in_c, kh, kw]; each output channel
/// (outer index) is one learned filter. The filter's full
/// [in_c, kh, kw] content is hashed (f64-canonical) into one
/// <c>conv_filter</c> entity. Same filter across models → ONE entity →
/// cross-model Glicko-2 corroboration on conv kernels.
///
/// 2-D and depthwise conv shapes also flow through here via the rank-N
/// outer-index helper — outer dim is treated as the channel axis.
/// Per A0 / A5.
/// </summary>
internal sealed partial class ConvFilterPass : IModelAnalysisPass
{
    public string PassId => "model.conv_filters";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public ConvFilterPass(ILogger logger)
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

            if (!IsConvRole(t.Classification.Role))
            {
                continue;
            }

            (long emitted, long skipped) = await PerRowContentPass.RunPerOuterIndexAsync(
                context, session, t,
                canonicalKindTag4: "cnvf",
                entityTypeCode: "conv_filter",
                edgeTypeCode: "has_conv_filter",
                sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                ct);

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static bool IsConvRole(TensorRole role) => role switch
    {
        TensorRole.ConvKernel => true,
        TensorRole.VaeBlock => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[conv-filter {ModelId}] complete — {TotalEmitted} filters, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
