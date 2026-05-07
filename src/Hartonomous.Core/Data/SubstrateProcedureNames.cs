using System;
using System.Collections.Generic;

namespace Hartonomous.Core.Data;

public static class SubstrateProcedureNames
{
    public const string WriteCodepointProperties = "substrate.write_codepoint_properties";
    public const string WriteGlickoJunction = "substrate.write_glicko_junction";
    public const string WritePlainJunction = "substrate.write_plain_junction";

    public static readonly IReadOnlySet<string> Allowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        WriteCodepointProperties,
        WriteGlickoJunction,
        WritePlainJunction,
    };

    public static void AssertAllowlisted(string procedureName)
    {
        if (!Allowlist.Contains(procedureName))
        {
            throw new InvalidOperationException(
                $"Substrate procedure name '{procedureName}' is not in the allowlist. " +
                "Add it to SubstrateProcedureNames.Allowlist before calling.");
        }
    }
}