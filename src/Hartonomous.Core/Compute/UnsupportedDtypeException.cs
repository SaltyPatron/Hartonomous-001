namespace Hartonomous.Core.Compute;

public sealed class UnsupportedDtypeException : ComputeException
{
    public UnsupportedDtypeException(string message) : base(message) { }
}
