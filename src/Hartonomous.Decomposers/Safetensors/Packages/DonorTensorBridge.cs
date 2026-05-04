using Hartonomous.Decomposers.Safetensors;

namespace Hartonomous.Decomposers.Safetensors.Packages;

/// <summary>
/// Bridge between TensorMetadata (canonical-string dtypes) produced by
/// IDonorPackageReader.EnumerateTensors() and SafetensorsTensorInfo
/// (typed-enum dtypes + BeginByte/EndByte/FilePath) consumed by the existing
/// SafetensorsDecomposer / ModelPassOrchestrator / IModelAnalysisPass surface.
///
/// For non-safetensors readers (pickle, multi-subdir) we synthesize a
/// donor:// FilePath via DonorReaderRegistry so the existing static
/// SafetensorsReader.StreamHash / StreamDecode helpers route through the
/// owning IDonorPackageReader rather than File.OpenRead'ing a path that
/// doesn't point at safetensors-format bytes.
///
/// BeginByte=0, EndByte=ByteLength because the donor:// stream the reader
/// returns is already positioned at the tensor's start and contains exactly
/// the tensor's bytes.
/// </summary>
public static class DonorTensorBridge
{
    public static SafetensorsTensorInfo ToSafetensorsTensorInfo(TensorMetadata md, int readerSlot)
    {
        long[] shape64 = new long[md.Shape.Length];
        for (int i = 0; i < md.Shape.Length; i++)
        {
            shape64[i] = md.Shape[i];
        }
        SafetensorsDtype dtype = MapDtype(md.Dtype);
        string filePath = DonorReaderRegistry.BuildPath(readerSlot, md.Name);
        return new SafetensorsTensorInfo(
            md.Name,
            dtype,
            shape64,
            BeginByte: 0,
            EndByte: md.ByteLength,
            FilePath: filePath);
    }

    public static SafetensorsDtype MapDtype(string canonical) => canonical switch
    {
        "F32" => SafetensorsDtype.F32,
        "F64" => SafetensorsDtype.F64,
        "F16" => SafetensorsDtype.F16,
        "BF16" => SafetensorsDtype.BF16,
        "I8"  => SafetensorsDtype.I8,
        "I16" => SafetensorsDtype.I16,
        "I32" => SafetensorsDtype.I32,
        "I64" => SafetensorsDtype.I64,
        "U8"  => SafetensorsDtype.U8,
        "U16" => SafetensorsDtype.U16,
        "U32" => SafetensorsDtype.U32,
        "U64" => SafetensorsDtype.U64,
        "BOOL" => SafetensorsDtype.Bool,
        "F8_E4M3" => SafetensorsDtype.F8E4M3,
        "F8_E5M2" => SafetensorsDtype.F8E5M2,
        _ => throw new NotSupportedException(
            $"DonorTensorBridge: unrecognized dtype '{canonical}' from donor reader. Add a mapping if this is a new dtype.")
    };
}
