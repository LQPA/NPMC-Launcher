using System.Text;

namespace NPMC.P2P.Contracts;

/// <summary>
/// base64url（无填充）编码，用于 nonce、挑战、签名和公钥摘要。
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Decode(string value)
    {
        if (!TryDecode(value, out var result))
        {
            throw new FormatException("不是合法的 base64url 字符串。");
        }

        return result;
    }

    public static bool TryDecode(string? value, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                     (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };

        try
        {
            result = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
