using Microsoft.AspNetCore.Http;

namespace Hartonomous.Api.Endpoints;

internal static class ApiHash
{
    internal static bool TryParse(string hash, out byte[]? bytes, out IResult? error)
    {
        bytes = null;
        error = null;
        if (hash.Length != 64)
        {
            error = Results.Problem(
                "Hash must be 64 hex characters (32 bytes)", statusCode: 400, type: "invalid-hash");
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hash);
            return true;
        }
        catch (FormatException)
        {
            error = Results.Problem("Invalid hex encoding", statusCode: 400, type: "invalid-hash");
            return false;
        }
    }

    internal static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
