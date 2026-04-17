namespace Hartonomous.Core.Compute;

/// <summary>
/// IoC-friendly entry point for all numerical compute. The single rule from
/// CLAUDE.md § "Compute Facade": every consumer of native compute talks to
/// this facade, never to MKL/Eigen/Spectra/native bindings directly. New
/// primitives are added on the facade interfaces, not exposed via side
/// channels. Per docs/specs/csharp/compute-facade.md.
/// </summary>
public interface IComputeFacade
{
    IIngestionCompute Ingestion { get; }

    ICommonCompute Common { get; }
}
