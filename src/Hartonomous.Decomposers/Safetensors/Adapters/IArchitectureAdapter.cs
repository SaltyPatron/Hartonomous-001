using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Packages;

namespace Hartonomous.Decomposers.Safetensors.Adapters;

public interface IArchitectureAdapter
{
    string ArchitectureClassCode { get; }

    bool CanHandle(IConfigSnapshot config);

    (ModalityLobe Lobe, string Role) ClassifyTensor(string tensorName, int[] shape, string dtype);

    bool TryClassify(string tensorName, int[] shape, string dtype, out ModalityLobe lobe, out string role);

    IReadOnlyList<string> RequiredConfigPaths { get; }
}
