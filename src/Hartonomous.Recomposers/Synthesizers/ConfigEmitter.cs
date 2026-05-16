using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Emit a HuggingFace-compatible <c>config.json</c> describing the target
/// architecture so that <c>AutoModel.from_pretrained(output_dir)</c> in
/// transformers (and downstream tools like llama.cpp's
/// convert-hf-to-gguf.py) can instantiate the right Python class against
/// the synthesized safetensors weights.
/// </summary>
public static class ConfigEmitter
{
    public static async Task WriteAsync(
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        string outputDir,
        CancellationToken ct)
    {
        string configJson = arch.Architecture.Contains("Llama", System.StringComparison.OrdinalIgnoreCase)
            ? BuildLlamaConfig(arch)
            : BuildBertConfig(arch);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "config.json"), configJson, ct)
            .ConfigureAwait(false);
    }

    private static string BuildLlamaConfig(TargetArchitectureSpec arch)
    {
        var ci = CultureInfo.InvariantCulture;
        return $$"""
        {
          "architectures": ["{{arch.Architecture}}"],
          "model_type": "llama",
          "vocab_size": {{arch.VocabSize.ToString(ci)}},
          "hidden_size": {{arch.HiddenDim.ToString(ci)}},
          "intermediate_size": {{arch.IntermediateSize.ToString(ci)}},
          "num_hidden_layers": {{arch.NumHiddenLayers.ToString(ci)}},
          "num_attention_heads": {{arch.NumAttentionHeads.ToString(ci)}},
          "num_key_value_heads": {{arch.EffectiveKvHeads.ToString(ci)}},
          "head_dim": {{arch.EffectiveHeadDim.ToString(ci)}},
          "max_position_embeddings": {{arch.MaxPositionEmbeddings.ToString(ci)}},
          "hidden_act": "{{arch.HiddenAct}}",
          "rms_norm_eps": {{arch.LayerNormEps.ToString("R", ci)}},
          "initializer_range": {{arch.InitializerRange.ToString("R", ci)}},
          "tie_word_embeddings": {{(arch.TieWordEmbeddings ? "true" : "false")}},
          "use_cache": {{(arch.UseCache ? "true" : "false")}},
          "torch_dtype": "float32",
          "transformers_version": "4.40.0",
          "hartonomous_substrate_derived": true
        }
        """;
    }

    private static string BuildBertConfig(TargetArchitectureSpec arch)
    {
        var ci = CultureInfo.InvariantCulture;
        return $$"""
        {
          "architectures": ["{{arch.Architecture}}"],
          "model_type": "bert",
          "vocab_size": {{arch.VocabSize.ToString(ci)}},
          "hidden_size": {{arch.HiddenDim.ToString(ci)}},
          "intermediate_size": {{arch.IntermediateSize.ToString(ci)}},
          "num_hidden_layers": {{arch.NumHiddenLayers.ToString(ci)}},
          "num_attention_heads": {{arch.NumAttentionHeads.ToString(ci)}},
          "max_position_embeddings": {{arch.MaxPositionEmbeddings.ToString(ci)}},
          "hidden_act": "{{arch.HiddenAct}}",
          "layer_norm_eps": {{arch.LayerNormEps.ToString("R", ci)}},
          "initializer_range": {{arch.InitializerRange.ToString("R", ci)}},
          "type_vocab_size": 2,
          "pad_token_id": 0,
          "hidden_dropout_prob": 0.0,
          "attention_probs_dropout_prob": 0.0,
          "torch_dtype": "float32",
          "transformers_version": "4.40.0",
          "hartonomous_substrate_derived": true
        }
        """;
    }
}
