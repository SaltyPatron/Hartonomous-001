namespace Hartonomous.Decomposers.Safetensors;

public sealed record SafetensorsTensorInfo(
    string Name,
    SafetensorsDtype Dtype,
    long[] Shape,
    long BeginByte,
    long EndByte,
    string FilePath)
{
    public long ElementCount
    {
        get
        {
            long n = 1;
            foreach (long d in Shape)
            {
                n *= d;
            }
            return n;
        }
    }

    public int BytesPerElement => Dtype switch
    {
        SafetensorsDtype.F64 or SafetensorsDtype.I64 or SafetensorsDtype.U64 => 8,
        SafetensorsDtype.F32 or SafetensorsDtype.I32 or SafetensorsDtype.U32 => 4,
        SafetensorsDtype.F16 or SafetensorsDtype.BF16 or SafetensorsDtype.I16 or SafetensorsDtype.U16 => 2,
        SafetensorsDtype.I8 or SafetensorsDtype.U8 or SafetensorsDtype.Bool
            or SafetensorsDtype.F8E4M3 or SafetensorsDtype.F8E5M2 => 1,
        _ => throw new InvalidOperationException($"Unknown dtype {Dtype}"),
    };
}
