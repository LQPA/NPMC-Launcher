namespace NPMC.P2P.Contracts;

/// <summary>
/// P2P 协议常量。所有线上格式都从这里取版本号，避免各子系统各写一份。
/// </summary>
public static class P2PProtocol
{
    /// <summary>当前协议版本。任何不兼容变更都必须递增，并保留旧版本拒绝逻辑。</summary>
    public const int Version = 1;

    /// <summary>规范化签名声明的固定首行。</summary>
    public const string StatementPrefix = "nmpc-p2p/v1";

    /// <summary>Minecraft 登录阶段挑战签名（Gateway 发起 nonce）。</summary>
    public const string AuthChallengeType = "auth-challenge";

    /// <summary>设备证明用的挑战签名（服务器 nonce）。</summary>
    public const string AttestationType = "attest";
}
