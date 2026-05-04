using Hartonomous.Decomposers.Safetensors.Packages;

namespace Hartonomous.Decomposers.Safetensors.Config;

public interface IConfigParser
{
    bool CanParse(string packageRoot);

    Task<IConfigSnapshot> ParseAsync(string packageRoot, CancellationToken ct);
}
