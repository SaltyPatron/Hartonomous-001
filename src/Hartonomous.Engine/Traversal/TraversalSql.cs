using System.Reflection;
using Hartonomous.Core.Data;

namespace Hartonomous.Engine.Traversal;

internal static class TraversalSql
{
    public static string NativeAstar { get; } = Read("native_astar.sql");

    private static string Read(string fileName)
        => EmbeddedSqlResource.Read(typeof(TraversalSql).Assembly, fileName, "traversal");
}
