using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Packages;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Adapters;

public abstract class BaseArchitectureAdapter : IArchitectureAdapter
{
    protected ILogger Logger { get; }

    protected BaseArchitectureAdapter(ILogger logger)
    {
        Logger = logger;
    }

    public abstract string ArchitectureClassCode { get; }

    public abstract bool CanHandle(IConfigSnapshot config);

    public virtual IReadOnlyList<string> RequiredConfigPaths => Array.Empty<string>();

    protected abstract (ModalityLobe Lobe, string Role)? ClassifyCore(string tensorName, int[] shape, string dtype);

    public (ModalityLobe Lobe, string Role) ClassifyTensor(string tensorName, int[] shape, string dtype)
    {
        (ModalityLobe Lobe, string Role)? result = ClassifyCore(tensorName, shape, dtype);
        if (result is null)
        {
            throw new InvalidOperationException(
                $"Adapter {GetType().Name} could not classify tensor '{tensorName}'.");
        }
        return result.Value;
    }

    public bool TryClassify(string tensorName, int[] shape, string dtype, out ModalityLobe lobe, out string role)
    {
        (ModalityLobe Lobe, string Role)? result = ClassifyCore(tensorName, shape, dtype);
        if (result is null)
        {
            lobe = default;
            role = string.Empty;
            return false;
        }
        lobe = result.Value.Lobe;
        role = result.Value.Role;
        return true;
    }
}
