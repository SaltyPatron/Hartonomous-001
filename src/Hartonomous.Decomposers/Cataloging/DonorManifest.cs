using System.Text.Json.Serialization;

namespace Hartonomous.Decomposers.Cataloging;

public sealed record DonorManifest
{
    [JsonPropertyName("vendor")]
    public required string Vendor { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("package_root")]
    public required string PackageRoot { get; init; }

    [JsonPropertyName("package_format")]
    public required string PackageFormat { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("architecture_class")]
    public string? ArchitectureClass { get; init; }

    [JsonPropertyName("required_adapter")]
    public string? RequiredAdapter { get; init; }

    [JsonPropertyName("rejection_reason")]
    public string? RejectionReason { get; init; }

    [JsonPropertyName("package_files")]
    public IReadOnlyList<DonorPackageFile> PackageFiles { get; init; } = [];

    [JsonPropertyName("architectures")]
    public IReadOnlyList<string> Architectures { get; init; } = [];

    [JsonPropertyName("config_summary")]
    public IReadOnlyDictionary<string, object?> ConfigSummary { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    [JsonPropertyName("tensor_count")]
    public int TensorCount { get; init; }

    [JsonPropertyName("tensors")]
    public IReadOnlyList<DonorTensor> Tensors { get; init; } = [];

    [JsonPropertyName("unclassified_tensors")]
    public IReadOnlyList<string> UnclassifiedTensors { get; init; } = [];

    [JsonPropertyName("modality_summary")]
    public IReadOnlyDictionary<string, int> ModalitySummary { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("additional_artifacts")]
    public IReadOnlyList<string> AdditionalArtifacts { get; init; } = [];
}

