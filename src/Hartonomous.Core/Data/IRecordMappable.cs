using Npgsql;

namespace Hartonomous.Core.Data;

/// <summary>
/// Static-abstract mapping contract for records produced by substrate function
/// calls. Implementations declare a static MapFrom that reads one row from an
/// <see cref="NpgsqlDataReader"/> using the function's RETURNS TABLE column
/// order. The base repository invokes the static method generically — no
/// reflection, no Dapper, no per-row delegate allocation.
/// </summary>
/// <typeparam name="TSelf">CRTP — the implementing record type.</typeparam>
public interface IRecordMappable<TSelf> where TSelf : IRecordMappable<TSelf>
{
    static abstract TSelf MapFrom(NpgsqlDataReader reader);
}
