using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public interface IDonorPackageReader : IAsyncDisposable
{
    string PackageRoot { get; }

    string PackageFormat { get; }

    IReadOnlyList<string> AdditionalArtifacts { get; }

    IReadOnlyList<TensorMetadata> EnumerateTensors();

    Task<ReadOnlyMemory<byte>> ReadTensorAsync(string name, CancellationToken ct);

    IConfigSnapshot ReadConfig();
}
