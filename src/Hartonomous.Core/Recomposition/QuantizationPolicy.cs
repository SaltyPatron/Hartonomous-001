namespace Hartonomous.Core.Recomposition;

public enum QuantizationPolicy
{
    Preserve,
    DequantizeToBf16,
    RequantizeTo,
}
