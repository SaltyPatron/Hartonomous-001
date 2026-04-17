namespace Hartonomous.Decomposers.Safetensors;

public sealed record ModelArchitecture(
    string ModelId,
    string ArchitectureClass,
    string ModelType,
    int HiddenSize,
    int NumLayers,
    int NumAttentionHeads,
    int VocabSize,
    int IntermediateSize,
    int MaxPositionEmbeddings);
