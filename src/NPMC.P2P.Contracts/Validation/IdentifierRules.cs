namespace NPMC.P2P.Contracts.Validation;

/// <summary>
/// 标识符规则：room/node/profile/game-session 等 ID 只允许安全字符，
/// 避免换行、等号、控制字符破坏规范化签名声明。
/// </summary>
public static class IdentifierRules
{
    public const int MaxLength = 128;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = (c >= 'A' && c <= 'Z') ||
                     (c >= 'a' && c <= 'z') ||
                     (c >= '0' && c <= '9') ||
                     c is '.' or '_' or ':' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public static void Validate(string value, string paramName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"标识符不合法（长度 1-{MaxLength}，仅允许 A-Z a-z 0-9 . _ : -）：{value}",
                paramName);
        }
    }
}
