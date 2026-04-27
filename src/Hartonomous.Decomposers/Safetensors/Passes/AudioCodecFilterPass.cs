using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Per-role unit emission for AUDIO_CODEC_ENCODER and AUDIO_CODEC_DECODER
/// tensors. Each outer-index slice (per-stage, per-channel) emits one
/// <c>audio_codec_filter</c> entity hashed by f64-canonical content.
/// Per A0 / A5.
/// </summary>
internal sealed partial class AudioCodecFilterPass : IModelAnalysisPass
{
    public string PassId => "model.audio_codec_filters";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private readonly ILogger _logger;

    public AudioCodecFilterPass(ILogger logger)
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

            if (!IsCodecRole(t.Classification.Role))
            {
                continue;
            }

            (long emitted, long skipped) = t.Info.Shape.Length switch
            {
                2 => await PerRowContentPass.RunPerRowAsync(
                        context, session, t,
                        canonicalKindTag4: "acfl",
                        entityTypeCode: "audio_codec_filter",
                        edgeTypeCode: "has_codec_filter",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
                _ => await PerRowContentPass.RunPerOuterIndexAsync(
                        context, session, t,
                        canonicalKindTag4: "acfl",
                        entityTypeCode: "audio_codec_filter",
                        edgeTypeCode: "has_codec_filter",
                        sparsityThreshold: PerRowContentPass.DefaultSparsityThreshold,
                        flushThreshold: PerRowContentPass.DefaultFlushThreshold,
                        ct),
            };

            totalEmitted += emitted;
            totalSkipped += skipped;
        }

        Log.PassComplete(_logger, context.Source.ModelId, totalEmitted, totalSkipped);
    }

    private static bool IsCodecRole(TensorRole role) => role switch
    {
        TensorRole.AudioCodecEncoder => true,
        TensorRole.AudioCodecDecoder => true,
        _ => false,
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "[audio-codec {ModelId}] complete — {TotalEmitted} filters, {TotalSkipped} sparse")]
        public static partial void PassComplete(ILogger logger, string modelId, long totalEmitted, long totalSkipped);
    }
}
