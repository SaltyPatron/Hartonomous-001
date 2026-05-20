using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Native;
using Hartonomous.Core.Text;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Substrate Synthesis knowledge selection: take user-supplied seed concepts
/// (e.g. <c>"Science"</c>, <c>"Mathematics"</c>, <c>"Physics"</c>), resolve them
/// to substrate word_form entities, then BFS-expand through edge_member by
/// arena-weighted edge mu to grow a coherent vocab subgraph that becomes
/// the bear's brain contents.
///
/// <para>
/// Replaces <see cref="VocabSelector"/>'s top-by-edge-degree arbitrary
/// selection (which fragments across content-addressing identity spaces
/// and produces incoherent V×V adjacency). Knowledge-selection chooses
/// vocab by SUBSTRATE COHERENCE — every vocab member was added because it
/// was connected (via an edge above mu threshold in the recipe's arenas)
/// to another vocab member.
/// </para>
///
/// <para>
/// Domain-specific bears fall out trivially:
///   <list type="bullet">
///     <item>Seed = {medicine, anatomy, biology} → medical bear</item>
///     <item>Seed = {programming, function, class} → code bear</item>
///     <item>Seed = {the, of, and, science, mathematics} → generic LM bear</item>
///   </list>
/// MoE experts are per-seed-set: one expert per concept domain, router
/// learns the edges between them.
/// </para>
/// </summary>
public static class KnowledgeSelector
{
    public static async Task<IReadOnlyList<VocabToken>> SelectFromConceptsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<string> seedConcepts,
        IReadOnlyDictionary<string, double> arenaWeights,
        int vocabBudget,
        int topKPerNode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedConcepts);
        ArgumentNullException.ThrowIfNull(arenaWeights);

        // Resolve each seed concept string to its canonical substrate hash via
        // the native UAX-29 kernel. SAME canonical hash regardless of surrounding
        // whitespace / case / NFC variation — the substrate's content-addressing
        // invariant. SubstrateTextDecomposer.ComputeCanonicalHash is the dry-run
        // entry point that does kernel decomposition without DB writes.
        List<byte[]> seedHashes = new(seedConcepts.Count);
        foreach (string concept in seedConcepts)
        {
            byte[]? hash = ComputeWordFormHashOrNull(concept);
            if (hash is not null)
            {
                seedHashes.Add(hash);
            }
        }

        if (seedHashes.Count == 0)
        {
            return Array.Empty<VocabToken>();
        }

        string[] arenaCodes = new string[arenaWeights.Count];
        double[] arenaValues = new double[arenaWeights.Count];
        int wi = 0;
        foreach ((string code, double weight) in arenaWeights)
        {
            arenaCodes[wi] = code;
            arenaValues[wi] = weight;
            wi++;
        }

        List<VocabToken> rows = new(vocabBudget);
        await using NpgsqlConnection conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_hash, discovery_round, edge_count "
            + "FROM substrate.select_knowledge_subgraph(@seeds, @arenas, @weights, @budget, @topk, 'word_form')",
            conn);
        cmd.CommandTimeout = 1800;
        cmd.Parameters.Add(new NpgsqlParameter("seeds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea)
            { Value = seedHashes.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("arenas", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            { Value = arenaCodes });
        cmd.Parameters.Add(new NpgsqlParameter("weights", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double)
            { Value = arenaValues });
        cmd.Parameters.Add(new NpgsqlParameter("budget", NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = vocabBudget });
        cmd.Parameters.Add(new NpgsqlParameter("topk", NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = topKPerNode });

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int idx = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] hash = (byte[])reader.GetValue(0);
            long edgeCount = reader.GetInt64(2);
            rows.Add(new VocabToken(
                Index: idx++,
                EntityHash: hash,
                TokenText: $"<wf_{System.Convert.ToHexString(hash).ToLowerInvariant().AsSpan(0, 16)}>",
                EdgeCount: edgeCount,
                CentroidX: 0, CentroidY: 0, CentroidZ: 0, CentroidM: 0));
        }
        return rows;
    }

    /// <summary>
    /// Computes the canonical word_form hash for an input string using the
    /// substrate's native UAX-29 kernel. Returns null if UCD blob isn't loaded
    /// (cold start) or the input doesn't contain a UAX-29 word.
    /// </summary>
    public static byte[]? ComputeWordFormHashOrNull(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }

        try
        {
            SubstrateTextDecomposer.EnsureUcdLoaded();
        }
        catch (InvalidOperationException)  // BOUNDARY: native UCD blob load. Skip unresolvable seeds rather than abort the whole BFS.
        {
            return null;
        }

        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] rootHash = new byte[32];
        double[] centroid = new double[4];
        System.Runtime.InteropServices.GCHandle utf8Pin =
            System.Runtime.InteropServices.GCHandle.Alloc(utf8, System.Runtime.InteropServices.GCHandleType.Pinned);
        System.Runtime.InteropServices.GCHandle hashPin =
            System.Runtime.InteropServices.GCHandle.Alloc(rootHash, System.Runtime.InteropServices.GCHandleType.Pinned);
        System.Runtime.InteropServices.GCHandle centroidPin =
            System.Runtime.InteropServices.GCHandle.Alloc(centroid, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            int rc = TextDecomposeNative.TextDecompose(
                utf8Pin.AddrOfPinnedObject(),
                (nuint)utf8.Length,
                TextDecomposeNative.KindWordForm,
                50000.0,  // arbitrary trust mu — not stored, this is dry-run
                (System.IntPtr _, ref TextDecomposeRecord _) => 0,  // discard records
                System.IntPtr.Zero,
                hashPin.AddrOfPinnedObject(),
                out _,
                centroidPin.AddrOfPinnedObject());
            return rc == 0 ? rootHash : null;
        }
        finally
        {
            utf8Pin.Free();
            hashPin.Free();
            centroidPin.Free();
        }
    }
}
