using System;
using System.Collections.Generic;

namespace Hartonomous.Core;

public sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayEqualityComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        unchecked
        {
            int hash = 17;
            int len = Math.Min(obj.Length, 8);
            for (int i = 0; i < len; i++)
            {
                hash = (hash * 31) + obj[i];
            }
            return hash;
        }
    }
}
