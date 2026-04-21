namespace Hartonomous.Decomposers.Safetensors;

public sealed record TensorClassification(
    TensorRole Role,
    int? LayerIndex,
    int? ExpertIndex);
