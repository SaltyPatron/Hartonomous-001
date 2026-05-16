namespace Hartonomous.Recomposers.Synthesizers;

public sealed class FfnMatrices
{
    public required int HiddenDim { get; init; }
    public required int IntermediateDim { get; init; }
    public required float[]? GateProj { get; init; }   // [interSize × hidden] when SwiGLU, else null
    public required float[] UpProj { get; init; }      // [interSize × hidden]
    public required float[] DownProj { get; init; }    // [hidden × interSize]
    public required bool UseSwiGlu { get; init; }
    public required bool DerivedFromSubstrate { get; init; }
    public required int RitzSlotsUsed { get; init; }
}
