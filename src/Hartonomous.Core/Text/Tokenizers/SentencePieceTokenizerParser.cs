using System.Text;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Parses a SentencePiece <c>.model</c> protobuf without taking a protobuf
/// package dependency. The format is a top-level <c>ModelProto</c> with
/// repeated <c>pieces</c> (field 1) — each piece is a <c>SentencePiece</c>
/// message with string <c>piece</c> (field 1), float <c>score</c> (field 2),
/// and optional enum <c>type</c> (field 3). Additional top-level fields
/// (trainer_spec / normalizer_spec) are skipped — we read enough to build a
/// deterministic substrate representation; the full config bytes go into the
/// hash so identity still covers the skipped portions.
/// </summary>
public static class SentencePieceTokenizerParser
{
    private const int FieldPieces = 1;
    private const int FieldNormalizerSpec = 4;
    private const int PieceField = 1;
    private const int ScoreField = 2;
    private const int TypeField = 3;

    public static TokenizerModel Parse(ReadOnlySpan<byte> spModelBytes)
    {
        if (spModelBytes.IsEmpty)
        {
            throw new ArgumentException("SentencePiece .model payload is empty.", nameof(spModelBytes));
        }

        Dictionary<int, VocabularyEntry> vocab = new();
        List<Normalizer> normalizers = new();
        int? unkId = null;
        int? bosId = null;
        int? eosId = null;
        int? padId = null;
        int pieceIndex = 0;

        int idx = 0;
        while (idx < spModelBytes.Length)
        {
            if (!ReadVarint(spModelBytes, ref idx, out ulong tag))
            {
                break;
            }
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNumber == FieldPieces && wireType == 2)
            {
                if (!ReadLengthDelimited(spModelBytes, ref idx, out ReadOnlySpan<byte> piece))
                {
                    break;
                }
                (byte[] pieceBytes, int type) = ParsePiece(piece);
                bool special = type is 1 or 2 or 3 or 4 or 5 or 6;
                vocab[pieceIndex] = new VocabularyEntry(pieceIndex, pieceBytes, special);
                switch (type)
                {
                    case 2: unkId ??= pieceIndex; break;
                    case 3: bosId ??= pieceIndex; break;
                    case 4: eosId ??= pieceIndex; break;
                    case 5: padId ??= pieceIndex; break;
                }
                pieceIndex++;
            }
            else if (fieldNumber == FieldNormalizerSpec && wireType == 2)
            {
                if (!ReadLengthDelimited(spModelBytes, ref idx, out ReadOnlySpan<byte> norm))
                {
                    break;
                }
                string? name = ParseNormalizerName(norm);
                if (!string.IsNullOrEmpty(name))
                {
                    normalizers.Add(new Normalizer(name!, new Dictionary<string, string>()));
                }
            }
            else if (!SkipField(spModelBytes, ref idx, wireType))
            {
                break;
            }
        }

        byte[] configHash = Blake3.Hash(spModelBytes);

        return new TokenizerModel(
            TokenizerKind.SentencePiece,
            configHash,
            normalizers,
            new List<PreTokenizer> { new("Metaspace", new Dictionary<string, string> { ["replacement"] = "\u2581" }) },
            Array.Empty<PostProcessor>(),
            vocab,
            Array.Empty<MergeRule>(),
            new SpecialTokens(bosId, eosId, padId, unkId, null, Array.Empty<int>()));
    }

    private static (byte[] PieceBytes, int Type) ParsePiece(ReadOnlySpan<byte> msg)
    {
        byte[] pieceBytes = Array.Empty<byte>();
        int type = 0;
        int idx = 0;
        while (idx < msg.Length)
        {
            if (!ReadVarint(msg, ref idx, out ulong tag))
            {
                break;
            }
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNumber == PieceField && wireType == 2)
            {
                if (!ReadLengthDelimited(msg, ref idx, out ReadOnlySpan<byte> s))
                {
                    break;
                }
                pieceBytes = s.ToArray();
            }
            else if (fieldNumber == ScoreField && wireType == 5)
            {
                idx += 4;
            }
            else if (fieldNumber == TypeField && wireType == 0)
            {
                if (!ReadVarint(msg, ref idx, out ulong t))
                {
                    break;
                }
                type = (int)t;
            }
            else if (!SkipField(msg, ref idx, wireType))
            {
                break;
            }
        }
        return (pieceBytes, type);
    }

    private static string? ParseNormalizerName(ReadOnlySpan<byte> msg)
    {
        int idx = 0;
        while (idx < msg.Length)
        {
            if (!ReadVarint(msg, ref idx, out ulong tag))
            {
                break;
            }
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNumber == 1 && wireType == 2)
            {
                if (!ReadLengthDelimited(msg, ref idx, out ReadOnlySpan<byte> s))
                {
                    break;
                }
                return Encoding.UTF8.GetString(s);
            }
            if (!SkipField(msg, ref idx, wireType))
            {
                break;
            }
        }
        return null;
    }

    private static bool ReadVarint(ReadOnlySpan<byte> bytes, ref int idx, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (idx < bytes.Length)
        {
            byte b = bytes[idx++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
            if (shift >= 64)
            {
                return false;
            }
        }
        return false;
    }

    private static bool ReadLengthDelimited(ReadOnlySpan<byte> bytes, ref int idx, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!ReadVarint(bytes, ref idx, out ulong length))
        {
            return false;
        }
        if (idx + (int)length > bytes.Length)
        {
            return false;
        }
        slice = bytes.Slice(idx, (int)length);
        idx += (int)length;
        return true;
    }

    private static bool SkipField(ReadOnlySpan<byte> bytes, ref int idx, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return ReadVarint(bytes, ref idx, out _);
            case 1:
                if (idx + 8 > bytes.Length)
                {
                    return false;
                }
                idx += 8;
                return true;
            case 2:
                return ReadLengthDelimited(bytes, ref idx, out _);
            case 5:
                if (idx + 4 > bytes.Length)
                {
                    return false;
                }
                idx += 4;
                return true;
            default:
                return false;
        }
    }
}
