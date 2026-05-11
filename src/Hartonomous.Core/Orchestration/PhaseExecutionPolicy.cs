namespace Hartonomous.Core.Orchestration;

public static class PhaseExecutionPolicy
{
    public static bool AllowsNoRegisteredDecomposer(Phase phase)
        => phase == Phase.CoreAlgebra;
}
