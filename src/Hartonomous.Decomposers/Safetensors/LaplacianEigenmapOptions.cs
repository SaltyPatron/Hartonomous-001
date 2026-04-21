namespace Hartonomous.Decomposers.Safetensors;

public sealed record LaplacianEigenmapOptions(int K, int LanczosSteps, int Seed)
{
    public static LaplacianEigenmapOptions Default => new(K: 10, LanczosSteps: 80, Seed: 42);
}
