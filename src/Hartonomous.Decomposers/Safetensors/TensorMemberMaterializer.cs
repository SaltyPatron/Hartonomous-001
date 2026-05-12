using System;

namespace Hartonomous.Decomposers.Safetensors;

public static class TensorMemberMaterializer
{
    public static long[] EffectiveShape(TupleMember member)
    {
        long[] shape = LinearizedShape(member.Tensor.Info.Shape);
        if (member.FusedSplit is FusedTensorSlice slice)
        {
            ValidateSlice(member.Tensor.Info.Name, shape, slice);
            shape[slice.Axis] = slice.Length;
        }
        return shape;
    }

    public static bool IsPointwiseLinearShape(long[] shape)
        => shape.Length == 4 && shape[2] == 1 && shape[3] == 1;

    public static long[] LinearizedShape(long[] shape)
        => IsPointwiseLinearShape(shape)
            ? [shape[0], shape[1]]
            : (long[])shape.Clone();

    public static double[] ReadAsDouble(TupleMember member)
    {
        double[] source = SafetensorsReader.ReadTensorAsDouble(member.Tensor.Info);
        if (member.FusedSplit is not FusedTensorSlice slice)
        {
            return source;
        }

        SafetensorsTensorInfo info = member.Tensor.Info;
        long[] sourceShape = LinearizedShape(info.Shape);
        ValidateSlice(info.Name, sourceShape, slice);
        long[] outputShape = EffectiveShape(member);
        long outputElements = Product(outputShape);
        if (outputElements > int.MaxValue)
        {
            throw new NotSupportedException(
                $"Tensor member {info.Name} slice has {outputElements} elements — exceeds int.MaxValue");
        }

        double[] output = new double[(int)outputElements];
        long[] sourceStrides = RowMajorStrides(sourceShape);
        long[] outputStrides = RowMajorStrides(outputShape);
        for (int i = 0; i < output.Length; i++)
        {
            long remainder = i;
            long sourceIndex = 0;
            for (int axis = 0; axis < outputShape.Length; axis++)
            {
                long coordinate = outputStrides[axis] == 0 ? 0 : remainder / outputStrides[axis];
                remainder -= coordinate * outputStrides[axis];
                if (axis == slice.Axis)
                {
                    coordinate += slice.Offset;
                }
                sourceIndex += coordinate * sourceStrides[axis];
            }
            output[i] = source[checked((int)sourceIndex)];
        }

        return output;
    }

    private static void ValidateSlice(string name, long[] shape, FusedTensorSlice slice)
    {
        if ((uint)slice.Axis >= (uint)shape.Length)
        {
            throw new InvalidOperationException(
                $"Fused tensor {name} cannot slice axis {slice.Axis}; rank is {shape.Length}.");
        }
        if (slice.Offset < 0 || slice.Length < 1 || slice.Offset + slice.Length > shape[slice.Axis])
        {
            throw new InvalidOperationException(
                $"Fused tensor {name} slice axis={slice.Axis} offset={slice.Offset} length={slice.Length} exceeds shape [{string.Join(",", shape)}].");
        }
    }

    private static long Product(long[] shape)
    {
        long product = 1;
        foreach (long dimension in shape)
        {
            product = checked(product * dimension);
        }
        return product;
    }

    private static long[] RowMajorStrides(long[] shape)
    {
        long[] strides = new long[shape.Length];
        long stride = 1;
        for (int axis = shape.Length - 1; axis >= 0; axis--)
        {
            strides[axis] = stride;
            stride = checked(stride * shape[axis]);
        }
        return strides;
    }
}
