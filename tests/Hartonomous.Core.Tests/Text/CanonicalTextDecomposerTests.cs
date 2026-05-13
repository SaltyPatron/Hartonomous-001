using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Segmentation;
using Xunit;

namespace Hartonomous.Core.Tests.Text;

/// <summary>
/// Acceptance tests for <see cref="CanonicalTextDecomposer"/> per the gates
/// in <c>docs/specs/text-decomposer-unification.md</c> §13:
/// <list type="bullet">
///   <item>G1 — determinism: same input produces byte-equal substrate state on
///     repeat decompose</item>
///   <item>G2 — cross-caller equality: same content under different
///     <c>ProvenanceCode</c> / <c>TopEntityType</c> produces the same root hash</item>
///   <item>G3 — substructure completeness: every layer (codepoint / grapheme /
///     word_form / composition) is emitted with entities, composition metadata,
///     physicality rows, and significance rows</item>
/// </list>
/// </summary>
public sealed class CanonicalTextDecomposerTests
{
    private static FakeCodepointProperties AsciiLetterProps()
    {
        // Set up minimal UAX #29 properties for ASCII letters d/o/g/c/a/t/r/s
        // and a space. Letters → ALetter (AlphaNumeric word formation).
        // Space → WSegSpace (word separator).
        FakeCodepointProperties p = new();
        foreach (int cp in new[] { 'd', 'o', 'g', 'c', 'a', 't', 'r', 's', 'h', 'e', 'i', 'n', 'p', 'k' })
        {
            p.WithWb(cp, WordBreak.ALetter);
        }
        p.WithWb(' ', WordBreak.WSegSpace);
        return p;
    }

    [Fact]
    public void G1_repeat_decompose_produces_identical_state()
    {
        FakeCodepointProperties props = AsciiLetterProps();
        TextDecomposeOptions opts = new(
            ProvenanceCode: "tatoeba",
            TopEntityType: "text_composition",
            TrustMu: 1200.0);

        RecordingBatch batch1 = new();
        TextDecomposeResult r1 = CanonicalTextDecomposer.Emit(
            batch1, "dog"u8, props, opts);

        RecordingBatch batch2 = new();
        TextDecomposeResult r2 = CanonicalTextDecomposer.Emit(
            batch2, "dog"u8, props, opts);

        Assert.Equal(r1.RootHash, r2.RootHash);
        Assert.Equal(r1.EntitiesEmitted, r2.EntitiesEmitted);
        Assert.Equal(r1.CompositionChildrenEmitted, r2.CompositionChildrenEmitted);
        Assert.Equal(r1.PhysicalityRowsEmitted, r2.PhysicalityRowsEmitted);
        Assert.Equal(r1.SignificanceRowsEmitted, r2.SignificanceRowsEmitted);
        Assert.True(batch1.Equals(batch2),
            $"Repeat decompose differs:\n--- run1 ---\n{batch1}\n--- run2 ---\n{batch2}");
    }

    [Fact]
    public void G2_cross_caller_same_content_yields_same_hash()
    {
        FakeCodepointProperties props = AsciiLetterProps();

        // Two callers of different decomposer flavors. Same UTF-8 content
        // ("dog"), different ProvenanceCode and TrustMu. The root hash MUST
        // match because hash IS content (entity_type and provenance are
        // metadata about how the content is used, not part of identity).
        TextDecomposeOptions tatoebaOpts = new("tatoeba", "text_composition", 1200.0);
        TextDecomposeOptions wordnetOpts = new("princeton_wordnet", "lemma", 1800.0);

        RecordingBatch tBatch = new();
        TextDecomposeResult tResult = CanonicalTextDecomposer.Emit(
            tBatch, "dog"u8, props, tatoebaOpts);

        RecordingBatch wBatch = new();
        TextDecomposeResult wResult = CanonicalTextDecomposer.Emit(
            wBatch, "dog"u8, props, wordnetOpts);

        Assert.Equal(tResult.RootHash, wResult.RootHash);
    }

    [Fact]
    public void G3_substructure_completeness_dog()
    {
        FakeCodepointProperties props = AsciiLetterProps();
        TextDecomposeOptions opts = new("tatoeba", "text_composition", 1200.0);

        RecordingBatch batch = new();
        TextDecomposeResult r = CanonicalTextDecomposer.Emit(
            batch, "dog"u8, props, opts);

        // Native emits every tier through the callback. For "dog" this covers
        // codepoints, grapheme clusters, word-form layer, and top composition.
        Assert.Equal(8, batch.EntitiesAdded);

        // Composition child metadata: grapheme→codepoint at every layer (3
        // graphemes, each with 1 codepoint child = 3 rows),
        // word_form→grapheme (3 children of 1 word = 3 rows for unique),
        // composition→word_form (1 row).
        // RLE may collapse runs; for "dog" no run repeats, so:
        //   3 (graphemes' single codepoints) + 3 (word's three graphemes) + 1 (composition's one word)
        //   = 7 child metadata entries
        Assert.Equal(7, batch.CompositionChildrenAdded);

        // Physicality rows:
        //   - codepoint POINT4D × 3 unique (d, o, g)
        //   - grapheme, word_form, and composition levels emit contour
        //     LINESTRING4D rows carrying child metadata, even for one child.
        Assert.Equal(3, batch.PhysicalityPoint4dAdded);
        Assert.Equal(5, batch.PhysicalityLineString4dAdded);

        // Significance: every distinct hash gets one source_authority row.
        // codepoints (3) + graphemes (3) + word_form (1) + composition (1) = 8
        Assert.Equal(8, batch.SignificanceRowsAdded);
    }

    [Fact]
    public void G3_repeated_letters_apply_RLE()
    {
        // "aaa" -> one grapheme/codepoint entity repeated 3x. Composition
        // metadata stores the consecutive occurrences as one run at ordinal 1.
        FakeCodepointProperties props = AsciiLetterProps();
        TextDecomposeOptions opts = new("tatoeba", "text_composition", 1200.0);

        RecordingBatch batch = new();
        CanonicalTextDecomposer.Emit(batch, "aaa"u8, props, opts);

        Assert.Contains(batch.CompositionChildren, s => s.Ordinal == 1 && s.RleCount == 3);
        Assert.DoesNotContain(batch.CompositionChildren, s => s.Ordinal == 2);
        Assert.DoesNotContain(batch.CompositionChildren, s => s.Ordinal == 3);
    }

    [Fact]
    public void Empty_input_produces_stable_root_hash()
    {
        FakeCodepointProperties props = AsciiLetterProps();
        TextDecomposeOptions opts = new("tatoeba", "text_composition", 1200.0);

        RecordingBatch b1 = new();
        TextDecomposeResult r1 = CanonicalTextDecomposer.Emit(b1, ReadOnlySpan<byte>.Empty, props, opts);
        RecordingBatch b2 = new();
        TextDecomposeResult r2 = CanonicalTextDecomposer.Emit(b2, ReadOnlySpan<byte>.Empty, props, opts);

        Assert.Equal(r1.RootHash, r2.RootHash);
        Assert.Equal(0, b1.EntitiesAdded);   // native empty input is a no-op with a stable zero root hash
    }

    [Fact]
    public void Whitespace_only_input_does_not_crash_MeanCentroid()
    {
        // Regression: previously crashed in MeanCentroid([]) when input had
        // no word_forms (whitespace-only / punctuation-only). The fix
        // routes the empty wordRanges path to a default centroid + skips
        // physicality, so the composition entity still has identity even
        // when there's no geometric trajectory through word_forms.
        FakeCodepointProperties props = AsciiLetterProps();
        TextDecomposeOptions opts = new("tatoeba", "text_composition", 1200.0);

        RecordingBatch batch = new();
        TextDecomposeResult r = CanonicalTextDecomposer.Emit(batch, "   "u8, props, opts);

        // Composition entity exists with a stable hash.
        Assert.Equal(Hash32.Length, r.RootHash.ToByteArray().Length);
    }

    [Fact]
    public void Whitespace_keeping_emits_raw_span_children_for_recompose_fidelity()
    {
        // Regression: WordBoundaries.EnumerateWords used to skip Other ranges
        // (whitespace/punctuation), which meant the composition metadata
        // walked only word_forms. recompose_text dropped all spaces. The
        // fix keeps Other ranges; the canonical decomposer emits them as
        // text_composition children alongside word_forms; recompose_text
        // walks the full composition and reproduces input byte-for-byte.
        FakeCodepointProperties props = AsciiLetterProps();
        TextDecomposeOptions opts = new("tatoeba", "text_composition", 1200.0);

        RecordingBatch batch = new();
        CanonicalTextDecomposer.Emit(batch, "a b"u8, props, opts);

        // Composition metadata must include exactly 3 children:
        // word_form 'a', raw_span ' ', word_form 'b'. Without the fix,
        // only 2 children (word_forms) appeared.
        Hartonomous.Core.Ingestion.EntityHandle compositionRoot =
            new(batch.Entities[^1].Hash, batch.Entities[^1].Type);
        int compositionChildren = 0;
        foreach (var child in batch.CompositionChildren)
        {
            if (child.Parent.Hash.Equals(compositionRoot.Hash))
            {
                compositionChildren++;
            }
        }
        Assert.Equal(3, compositionChildren);
    }

    /// <summary>
    /// Records every batch emission in order so tests can assert structural
    /// equality between two decompose runs.
    /// </summary>
    private sealed class RecordingBatch : IIngestionBatch, IEquatable<RecordingBatch>
    {
        public string ProvenanceCode { get; init; } = "test";
        public List<(Hash32 Hash, string Type)> Entities { get; } = new();
        public List<(EntityHandle Parent, int Ordinal, EntityHandle Child, int RleCount)> CompositionChildren { get; } = new();
        public List<(EntityHandle Entity, string Type, double X, double Y, double Z, double M)> Points4d { get; } = new();
        public List<(EntityHandle Entity, string Type, int VertexCount)> LineStrings4d { get; } = new();
        public List<(EntityHandle Entity, string Context, double Mu)> Significances { get; } = new();

        public int EntitiesAdded => Entities.Count;
        public int CompositionChildrenAdded => CompositionChildren.Count;
        public int PhysicalityPoint4dAdded => Points4d.Count;
        public int PhysicalityLineString4dAdded => LineStrings4d.Count;
        public int SignificanceRowsAdded => Significances.Count;

        public int EntityCount => Entities.Count;
        public int EdgeCount => 0;

        public EntityHandle AddEntity(Hash32 hash, string entityTypeCode)
        {
            Entities.Add((hash, entityTypeCode));
            return new EntityHandle(hash, entityTypeCode);
        }
        public void AddCompositionChild(EntityHandle parent, int ordinal, EntityHandle child, int rleCount = 1)
            => CompositionChildren.Add((parent, ordinal, child, rleCount));
        public void AddPhysicalityPoint4d(EntityHandle e, string t, double x, double y, double z, double m)
            => Points4d.Add((e, t, x, y, z, m));
        public void AddPhysicalityLineString4d(EntityHandle e, string t,
            ReadOnlySpan<(double X1, double X2, double X3, double X4)> verts)
            => LineStrings4d.Add((e, t, verts.Length));
        public void AddSignificance(EntityHandle e, string c, double mu, string attestationTypeCode = "provenance_authority_corroboration")
            => Significances.Add((e, c, mu));

        public void AddEdge(string c, string p, ReadOnlySpan<EdgeMemberSpec> m)
            => throw new NotSupportedException("Canonical text decomposer should not emit edges.");
        public void AddJunction(string t, EntityHandle e, int r, double? mu = null, string attestationTypeCode = "lexical_curated_relation")
            => throw new NotSupportedException("Canonical text decomposer should not emit junctions.");
        public void AddPhysicality(EntityHandle e, string t, byte[] g)
        {
            if (g.Length < 1)
            {
                throw new InvalidOperationException("Expected native geometry4d payload.");
            }

            if (g[0] == 1)
            {
                Points4d.Add((e, t, 0, 0, 0, 0));
                return;
            }

            if (g[0] == 2)
            {
                int vertexCount = (int) BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(1, 4));
                LineStrings4d.Add((e, t, vertexCount));
                return;
            }

            throw new InvalidOperationException($"Unexpected geometry4d tag {g[0]}.");
        }
        public void AddEntityModelSource(EntityHandle e, long m)
            => throw new NotSupportedException();

        public bool Equals(RecordingBatch? other)
        {
            if (other is null) { return false; }
            if (Entities.Count != other.Entities.Count) { return false; }
            if (CompositionChildren.Count != other.CompositionChildren.Count) { return false; }
            if (Points4d.Count != other.Points4d.Count) { return false; }
            if (LineStrings4d.Count != other.LineStrings4d.Count) { return false; }
            if (Significances.Count != other.Significances.Count) { return false; }
            for (int i = 0; i < Entities.Count; i++)
            {
                if (!Entities[i].Hash.Equals(other.Entities[i].Hash)) { return false; }
                if (Entities[i].Type != other.Entities[i].Type) { return false; }
            }
            for (int i = 0; i < CompositionChildren.Count; i++)
            {
                var a = CompositionChildren[i]; var b = other.CompositionChildren[i];
                if (a.Ordinal != b.Ordinal || a.RleCount != b.RleCount) { return false; }
                if (!a.Parent.Hash.Equals(b.Parent.Hash)) { return false; }
                if (!a.Child.Hash.Equals(b.Child.Hash)) { return false; }
            }
            return true;
        }
        public override bool Equals(object? obj) => obj is RecordingBatch rb && Equals(rb);
        public override int GetHashCode() => Entities.Count ^ CompositionChildren.Count ^ Points4d.Count;

        public override string ToString()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            StringBuilder sb = new();
            sb.AppendLine(ic, $"Entities ({Entities.Count}):");
            foreach (var e in Entities) { sb.AppendLine(ic, $"  {e.Type} {e.Hash.ToHexString()[..16]}"); }
            sb.AppendLine(ic, $"Composition children ({CompositionChildren.Count}):");
            foreach (var s in CompositionChildren) { sb.AppendLine(ic, $"  ord={s.Ordinal} rle={s.RleCount}"); }
            return sb.ToString();
        }
    }
}
