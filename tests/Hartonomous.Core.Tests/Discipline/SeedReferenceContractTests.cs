using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Hartonomous.Core.Tests.Discipline;

/// <summary>
/// Contract test: every attestation_type / edge_type / entity_type code referenced by
/// any C# source under src/ must exist in the corresponding seed file under
/// sql/schema/seed/. Catches the "decomposer emits a code that no longer exists in
/// seed" failure mode at test time instead of runtime — which is what bit us when
/// the §IX.1 seed cleanup deleted codes some pass code still referenced.
///
/// Lookup pattern: extracts string literals matching the code-naming conventions
/// (model_*, corpus_*, lexical_*, inference_*, has_*, model_attention_pattern,
/// word_form, etc.) from src/**/*.cs. For each, verifies it appears in the
/// appropriate seed file.
/// </summary>
public sealed class SeedReferenceContractTests
{
    [Fact]
    public void EveryAttestationTypeCodeInSrc_ExistsInSeed()
    {
        HashSet<string> seedCodes = ExtractCodesFromSeed("attestation_type.sql");
        HashSet<string> srcCodes = ExtractCodesFromSrc(AttestationTypeCallSites);
        AssertEverySrcCodeInSeed("attestation_type", srcCodes, seedCodes);
    }

    [Fact]
    public void EveryEdgeTypeCodeInSrc_ExistsInSeed()
    {
        HashSet<string> seedCodes = ExtractCodesFromSeed("edge_type.sql");
        HashSet<string> srcCodes = ExtractCodesFromSrc(EdgeTypeCallSites);
        AssertEverySrcCodeInSeed("edge_type", srcCodes, seedCodes);
    }

    [Fact]
    public void EveryEntityTypeCodeInSrc_ExistsInSeed()
    {
        HashSet<string> seedCodes = ExtractCodesFromSeed("entity_type.sql");
        HashSet<string> srcCodes = ExtractCodesFromSrc(EntityTypeCallSites);
        AssertEverySrcCodeInSeed("entity_type", srcCodes, seedCodes);
    }

    private static void AssertEverySrcCodeInSeed(string kind, HashSet<string> srcCodes, HashSet<string> seedCodes)
    {
        List<string> missing = srcCodes.Where(c => !seedCodes.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            string lines = string.Join('\n', missing.Select(c => $"  '{c}' referenced in src/ but not in sql/schema/seed/{kind}.sql"));
            Assert.Fail($"Seed reference contract violation — {missing.Count} {kind} code(s) missing from seed:\n{lines}");
        }
    }

    private static HashSet<string> ExtractCodesFromSeed(string seedFileName)
    {
        string repoRoot = FindRepoRoot();
        string seedPath = Path.Combine(repoRoot, "sql", "schema", "seed", seedFileName);
        Assert.True(File.Exists(seedPath), $"Expected seed file at {seedPath}");
        string text = File.ReadAllText(seedPath);
        // Extract single-quoted code values from VALUES tuples / INSERT statements.
        // Pattern: ('code_value', ... or ('code_value')
        HashSet<string> codes = new(StringComparer.Ordinal);
        Regex r = new(@"\('([a-z][a-z0-9_]+)'", RegexOptions.Compiled);
        foreach (Match m in r.Matches(text))
        {
            codes.Add(m.Groups[1].Value);
        }
        return codes;
    }

    /// <summary>
    /// Patterns in src/ that take an attestation_type code. Conservative — only
    /// matches usage where a code is being passed to a known sink, to avoid
    /// false positives on string literals used for other purposes.
    /// </summary>
    private static IReadOnlyList<Regex> AttestationTypeCallSites { get; } =
    [
        // EdgeSignificanceSpec(arena, attestation_type, mu) — attestation_type is the SECOND arg
        new Regex(@"new\s+EdgeSignificanceSpec\s*\(\s*""[^""]+""\s*,\s*""([a-z][a-z0-9_]+)""", RegexOptions.Compiled),
        // AddSignificance(entity, arena, mu, attestation_type) — attestation_type is the FOURTH arg
        new Regex(@"AddSignificance\s*\([^)]*?,\s*""[^""]+""\s*,\s*[^,)]+,\s*""([a-z][a-z0-9_]+)""", RegexOptions.Compiled),
    ];

    private static IReadOnlyList<Regex> EdgeTypeCallSites { get; } =
    [
        // AddEdge(edge_type_code, ...)
        new Regex(@"\.AddEdge\s*\(\s*""([a-z][a-z0-9_]+)""", RegexOptions.Compiled),
    ];

    private static IReadOnlyList<Regex> EntityTypeCallSites { get; } =
    [
        // AddEntity(hash, entity_type_code) — entity_type_code is the SECOND arg
        new Regex(@"AddEntity\s*\([^,)]+,\s*""([a-z][a-z0-9_]+)""", RegexOptions.Compiled),
        // new EntityHandle(hash, entity_type_code)
        new Regex(@"new\s+EntityHandle\s*\([^,)]+,\s*""([a-z][a-z0-9_]+)""", RegexOptions.Compiled),
        // EmitStatic(..., TopEntityType: "entity_type_code", ...)
        new Regex(@"TopEntityType\s*:\s*""([a-z][a-z0-9_]+)""", RegexOptions.Compiled),
    ];

    private static HashSet<string> ExtractCodesFromSrc(IReadOnlyList<Regex> patterns)
    {
        string srcRoot = Path.Combine(FindRepoRoot(), "src");
        Assert.True(Directory.Exists(srcRoot), $"Expected src/ at {srcRoot}");

        HashSet<string> codes = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated artifacts.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            string text = File.ReadAllText(file);
            foreach (Regex r in patterns)
            {
                foreach (Match m in r.Matches(text))
                {
                    codes.Add(m.Groups[1].Value);
                }
            }
        }
        return codes;
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "sql", "schema", "seed")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return dir;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) { break; }
            dir = parent;
        }
        throw new InvalidOperationException("Couldn't find repo root from " + AppContext.BaseDirectory);
    }
}
