namespace NPMC.P2P.Contracts.Signing;

/// <summary>挑战/nonce 的字节长度约束（base64url 编码后传入）。</summary>
public static class ChallengeRules
{
    public const int MinChallengeBytes = 32;
    public const int MaxChallengeBytes = 64;
    public const int NonceBytes = 32;

    public static bool IsValidChallenge(string? value)
    {
        return Base64Url.TryDecode(value, out var bytes) &&
               bytes.Length is >= MinChallengeBytes and <= MaxChallengeBytes;
    }

    public static bool IsValidNonce(string? value)
    {
        return Base64Url.TryDecode(value, out var bytes) && bytes.Length == NonceBytes;
    }

    public static void ValidateChallenge(string value, string paramName)
    {
        if (!IsValidChallenge(value))
        {
            throw new ArgumentException(
                $"挑战必须是 {MinChallengeBytes}-{MaxChallengeBytes} 字节的 base64url。",
                paramName);
        }
    }

    public static void ValidateNonce(string value, string paramName)
    {
        if (!IsValidNonce(value))
        {
            throw new ArgumentException($"nonce 必须是 {NonceBytes} 字节的 base64url。", paramName);
        }
    }
}
