using System.Security.Cryptography;
using System.Text;

namespace DeskBeam.Core.Security;

/// <summary>
/// Challenge/response password proof. The password itself never crosses the wire:
/// the client proves knowledge by HMAC-SHA256(password, nonce|clientName|version).
/// </summary>
public static class Auth
{
    public static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public static string CreateSessionToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public static string ComputeProof(string password, string nonce, string clientName, int version)
    {
        var material = Encoding.UTF8.GetBytes($"{nonce}|{clientName}|{version}");
        var key = Encoding.UTF8.GetBytes(password);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(material));
    }

    public static bool VerifyProof(string expected, string supplied)
    {
        if (expected.Length != supplied.Length)
            return false;

        try
        {
            var a = Convert.FromHexString(expected);
            var b = Convert.FromHexString(supplied);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
