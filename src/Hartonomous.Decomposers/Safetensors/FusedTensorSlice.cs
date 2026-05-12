namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// Slice descriptor for fused tensors. e.g. (Offset=hidden_dim*0, Length=hidden_dim, Axis=0)
/// for the Q half of a fused qkv tensor.
/// </summary>
public sealed record FusedTensorSlice(long Offset, long Length, int Axis);
