using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Hartonomous.Decomposers.Safetensors;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Generates a real safetensors file in temp dir with known tensor data so
/// SafetensorsReader.ReadTensorAsDouble (which opens a real file stream)
/// works in unit tests. Disposable: deletes the temp file on Dispose.
/// </summary>
internal sealed class TinySafetensorsFile : IDisposable
{
    public string Path { get; }
    public SafetensorsTensorInfo Tensor { get; }

    private TinySafetensorsFile(string path, SafetensorsTensorInfo tensor)
    {
        Path = path;
        Tensor = tensor;
    }

    /// <summary>F32 safetensors with one tensor of given name + shape + values.</summary>
    public static TinySafetensorsFile CreateF32(string tensorName, long[] shape, float[] values)
    {
        return CreateF32Multi(System.IO.Path.GetTempPath(), [(tensorName, shape, values)]);
    }

    /// <summary>F32 safetensors with multiple tensors. Returns helper around the FIRST tensor;
    /// use AllTensors to access the rest. Files are written into directory.</summary>
    public static TinySafetensorsFile CreateF32Multi(string directory, IReadOnlyList<(string Name, long[] Shape, float[] Values)> tensors)
    {
        string path = System.IO.Path.Combine(directory, $"hartonomous-{Guid.NewGuid():N}.safetensors");

        // Build header JSON with offsets accumulated in insertion order.
        long offset = 0;
        StringBuilder sb = new();
        sb.Append('{');
        for (int i = 0; i < tensors.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            (string name, long[] shape, float[] values) = tensors[i];
            long len = values.Length * sizeof(float);
            string shapeJson = string.Join(',', shape);
            sb.Append('"').Append(name).Append("\":{\"dtype\":\"F32\",\"shape\":[")
              .Append(shapeJson).Append("],\"data_offsets\":[")
              .Append(offset).Append(',').Append(offset + len).Append("]}");
            offset += len;
        }
        sb.Append('}');

        byte[] header = Encoding.UTF8.GetBytes(sb.ToString());
        int padding = (int)((8 - (header.Length % 8)) % 8);
        if (padding > 0)
        {
            Array.Resize(ref header, header.Length + padding);
            Array.Fill(header, (byte)' ', header.Length - padding, padding);
        }

        using (FileStream stream = File.Create(path))
        {
            Span<byte> headerLength = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(headerLength, (ulong)header.Length);
            stream.Write(headerLength);
            stream.Write(header);
            Span<byte> valBuf = stackalloc byte[4];
            foreach (var (_, _, values) in tensors)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(valBuf, values[i]);
                    stream.Write(valBuf);
                }
            }
        }

        SafetensorsTensorInfo[] all = [.. SafetensorsReader.ReadHeader(path)];
        TinySafetensorsFile file = new(path, all[0]) { _allTensors = all };
        return file;
    }

    private SafetensorsTensorInfo[]? _allTensors;
    public IReadOnlyList<SafetensorsTensorInfo> AllTensors => _allTensors ?? [Tensor];

    public void Dispose()
    {
        if (File.Exists(Path)) { File.Delete(Path); }
    }
}
