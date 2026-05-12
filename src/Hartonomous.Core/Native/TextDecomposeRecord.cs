using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

/// <summary>
/// Native record passed to the emit callback. Pointer fields are valid only
/// for the duration of the callback.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TextDecomposeRecord
{
    public int Kind;
    public int Subkind;
    public IntPtr HashA;
    public IntPtr HashB;
    public int IntParam;
    public double DoubleParam;
    public IntPtr Wkb;
    public nuint WkbLen;
    public double CentroidX;
    public double CentroidY;
    public double CentroidZ;
    public double CentroidM;
}
