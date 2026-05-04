using System.Text.Json.Serialization;
using Hartonomous.Core.Operations;

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

public sealed record DonorPackageFile
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("tensor_count")]
    public int TensorCount { get; init; }
}

public sealed record DonorTensor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("dtype")]
    public required string Dtype { get; init; }

    [JsonPropertyName("shape")]
    public required IReadOnlyList<int> Shape { get; init; }

    [JsonPropertyName("byte_length")]
    public long ByteLength { get; init; }

    [JsonPropertyName("component")]
    public string? Component { get; init; }

    [JsonPropertyName("lobe")]
    public required string Lobe { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }
}

public static class DonorManifestStatuses
{
    public const string Ingested = "ingested";
    public const string UnsupportedV1 = "unsupported_v1";
    public const string Rejected = "rejected";
    public const string DiscoveryFailed = "discovery_failed";
}
