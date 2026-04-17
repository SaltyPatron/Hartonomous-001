using System.Text.Json;

namespace Hartonomous.Decomposers.Safetensors;

public static class ArchitectureDetector
{
    public static ModelArchitecture DetectFromConfig(string configPath, string modelId)
    {
        using FileStream fs = File.OpenRead(configPath);
        using JsonDocument doc = JsonDocument.Parse(fs);
        JsonElement root = doc.RootElement;

        string architectureClass = "Unknown";
        if (root.TryGetProperty("architectures", out JsonElement archArr) &&
            archArr.ValueKind == JsonValueKind.Array &&
            archArr.GetArrayLength() > 0)
        {
            architectureClass = archArr[0].GetString() ?? "Unknown";
        }

        string modelType = root.TryGetProperty("model_type", out JsonElement mt)
            ? mt.GetString() ?? "unknown"
            : "unknown";

        int hidden = ReadIntOrDefault(root, "hidden_size", 0);
        int layers = ReadIntOrDefault(root, "num_hidden_layers", 0);
        int heads = ReadIntOrDefault(root, "num_attention_heads", 0);
        int vocab = ReadIntOrDefault(root, "vocab_size", 0);
        int intermediate = ReadIntOrDefault(root, "intermediate_size", 0);
        int maxPos = ReadIntOrDefault(root, "max_position_embeddings", 0);

        return new ModelArchitecture(
            ModelId: modelId,
            ArchitectureClass: architectureClass,
            ModelType: modelType,
            HiddenSize: hidden,
            NumLayers: layers,
            NumAttentionHeads: heads,
            VocabSize: vocab,
            IntermediateSize: intermediate,
            MaxPositionEmbeddings: maxPos);
    }

    private static int ReadIntOrDefault(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number)
        {
            return e.GetInt32();
        }
        return fallback;
    }
}
