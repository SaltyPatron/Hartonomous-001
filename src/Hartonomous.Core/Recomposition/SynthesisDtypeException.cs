using System;

namespace Hartonomous.Core.Recomposition;

public sealed class SynthesisDtypeException : Exception
{
    public SynthesisDtypeException(string message) : base(message) { }
}
