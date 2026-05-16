namespace Hartonomous.Recomposers.Synthesizers;

public sealed class AttentionMatrices
{
    public required int HiddenDim { get; init; }
    public required int NumHeads { get; init; }
    public required int HeadDim { get; init; }
    public required float[] Wq { get; init; }   // [numHeads × headDim, hiddenDim] row-major
    public required float[] Wk { get; init; }   // [numHeads × headDim, hiddenDim]
    public required float[] Wv { get; init; }   // [numHeads × headDim, hiddenDim]
    public required float[] Wo { get; init; }   // [hiddenDim, hiddenDim]
    public required bool DerivedFromSubstrate { get; init; }
    public required int RitzPairsUsed { get; init; }
}
