using System;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Stable, ordered byte serializer for entity content signatures. Per
/// docs/specs/decomposers/analysis-passes.md § "Canonical signatures":
/// every pass that creates an entity hashes via this builder. Forbids
/// string interpolation, <c>string.Join</c>, and any other ad-hoc encoding.
///
/// The builder prepends a 4-byte ASCII kind tag (e.g. <c>"tens"</c>, <c>"svds"</c>)
/// so signatures of different kinds can never collide on byte content.
///
/// Numeric encodings are fixed: integers little-endian, floats IEEE 754 with a
/// stable byte order, strings/bytes length-prefixed. <see cref="Finalize"/>
/// returns the BLAKE3 of the serialized bytes.
/// </summary>
public interface ICanonicalSignatureBuilder
{
    ICanonicalSignatureBuilder WriteInt32LE(int value);

    ICanonicalSignatureBuilder WriteInt64LE(long value);

    ICanonicalSignatureBuilder WriteDouble(double value);

    ICanonicalSignatureBuilder WriteUtf8(ReadOnlySpan<char> value);

    ICanonicalSignatureBuilder WriteBytes(ReadOnlySpan<byte> value);

    ICanonicalSignatureBuilder WriteHash(ReadOnlySpan<byte> blake32);

    byte[] Finalize();
}
