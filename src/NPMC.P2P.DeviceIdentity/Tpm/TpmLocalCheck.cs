using System.Security.Cryptography;
using NPMC.P2P.Contracts;
using NPMC.P2P.DeviceIdentity.Windows;

namespace NPMC.P2P.DeviceIdentity.Tpm;

public enum TpmCheckStepCode
{
    TpmPresent,
    Tpm20,
    ProviderAvailable,
    KeyCreated,
    PrivateKeyNonExportable,
    SignVerify,
}

public sealed record TpmCheckStep(TpmCheckStepCode Code, bool Passed, string Detail);

/// <summary>TPM 本地检查报告。任何一步失败 → LOCAL_FAILED，P2P 必须禁用。</summary>
public sealed class TpmLocalCheckReport
{
    public TpmLocalCheckReport(
        IReadOnlyList<TpmCheckStep> steps,
        DeviceState state,
        TpmDeviceInfo? deviceInfo,
        string? keyName,
        string? deviceId,
        string? keyId)
    {
        Steps = steps;
        State = state;
        DeviceInfo = deviceInfo;
        KeyName = keyName;
        DeviceId = deviceId;
        KeyId = keyId;
    }

    public IReadOnlyList<TpmCheckStep> Steps { get; }

    public DeviceState State { get; }

    public TpmDeviceInfo? DeviceInfo { get; }

    public string? KeyName { get; }

    public string? DeviceId { get; }

    public string? KeyId { get; }

    public bool Passed => State == DeviceState.LocalPassed;

    public string Summary => Passed
        ? "TPM 本地检查通过（仅代表本地硬件密钥可用，不代表服务器已验证）。"
        : "TPM 本地检查失败：P2P 必须禁用，且不得降级到软件密钥。";
}

/// <summary>
/// 设计文档 §2.2 的六步本地检查：
/// TPM 2.0 存在 → 可用（TBS 查询成功）→ Provider 可用 → 创建硬件签名密钥 →
/// 私钥不可导出 → 签名/验签通过。
/// </summary>
public static class TpmLocalCheck
{
    public const string DefaultKeyName = "NPMC-P2P-Device-v1";

    public static TpmLocalCheckReport Run(string keyName = DefaultKeyName)
    {
        var steps = new List<TpmCheckStep>(6);
        TpmDeviceInfo? deviceInfo = null;
        TpmDeviceKey? key = null;
        var keyExistedBefore = false;

        try
        {
            // 1. TPM 2.0 存在（TBS 能返回设备信息）
            if (!Tbs.TryQuery(out var info, out var tbsStatus))
            {
                steps.Add(new TpmCheckStep(
                    TpmCheckStepCode.TpmPresent,
                    false,
                    $"TBS 查询失败（0x{tbsStatus:X8}）：无 TPM 或被策略禁用。"));
                return Fail(steps, deviceInfo, keyName);
            }

            deviceInfo = info;
            steps.Add(new TpmCheckStep(
                TpmCheckStepCode.TpmPresent,
                true,
                $"TBS 返回 {info.VersionText}，接口类型 {info.InterfaceType}。"));

            // 2. 必须为 TPM 2.0（TPM 1.2 不支持本方案的 CNG 密钥模型）
            if (!info.IsTpm20)
            {
                steps.Add(new TpmCheckStep(
                    TpmCheckStepCode.Tpm20,
                    false,
                    $"检测到 {info.VersionText}，本方案要求 TPM 2.0。"));
                return Fail(steps, deviceInfo, keyName);
            }

            steps.Add(new TpmCheckStep(TpmCheckStepCode.Tpm20, true, "TPM 版本为 2.0。"));

            // 3. Platform Crypto Provider 可用
            if (!PlatformCryptoProvider.TryOpen(out var providerStatus))
            {
                steps.Add(new TpmCheckStep(
                    TpmCheckStepCode.ProviderAvailable,
                    false,
                    $"无法打开 {PlatformCryptoProvider.ProviderName}（NCrypt 0x{providerStatus:X8}）。"));
                return Fail(steps, deviceInfo, keyName);
            }

            steps.Add(new TpmCheckStep(
                TpmCheckStepCode.ProviderAvailable,
                true,
                $"{PlatformCryptoProvider.ProviderName} 可用。"));

            // 4. 创建/打开持久化硬件签名密钥
            keyExistedBefore = CngKey.Exists(keyName, new CngProvider(PlatformCryptoProvider.ProviderName));
            key = TpmDeviceKey.OpenOrCreate(keyName);

            if (!key.UsesPlatformCryptoProvider)
            {
                steps.Add(new TpmCheckStep(
                    TpmCheckStepCode.KeyCreated,
                    false,
                    $"密钥 Provider 不是 {PlatformCryptoProvider.ProviderName}：{key.Key.Provider?.Provider}"));
                CleanupFailedKey(key, keyName, keyExistedBefore);
                key = null;
                return Fail(steps, deviceInfo, keyName);
            }

            steps.Add(new TpmCheckStep(
                TpmCheckStepCode.KeyCreated,
                true,
                $"{(keyExistedBefore ? "打开" : "创建")} ECDSA P-256 密钥 {keyName}，device_id={key.DeviceId}。"));

            // 5. 私钥不可导出（属性 + 实际导出尝试）
            if (!key.PrivateKeyExportBlocked || key.Key.ExportPolicy != CngExportPolicies.None)
            {
                steps.Add(new TpmCheckStep(
                    TpmCheckStepCode.PrivateKeyNonExportable,
                    false,
                    $"私钥可导出或策略不是 None（ExportPolicy={key.Key.ExportPolicy}）。"));
                CleanupFailedKey(key, keyName, keyExistedBefore);
                key = null;
                return Fail(steps, deviceInfo, keyName, null, null);
            }

            steps.Add(new TpmCheckStep(
                TpmCheckStepCode.PrivateKeyNonExportable,
                true,
                "私钥导出被拒（ExportPolicy=None），密钥留在 TPM。"));

            // 6. 签名 / 验签（并确认篡改后的签名会被拒绝）
            var data = RandomNumberGenerator.GetBytes(32);
            var signature = key.Sign(data);
            var verified = key.Verify(data, signature);

            data[0] ^= 0xFF;
            var tamperedAccepted = key.Verify(data, signature);

            if (!verified || tamperedAccepted)
            {
                steps.Add(new TpmCheckStep(
                    TpmCheckStepCode.SignVerify,
                    false,
                    $"签名/验签异常（verify={verified}, tamperedAccepted={tamperedAccepted}）。"));
                CleanupFailedKey(key, keyName, keyExistedBefore);
                key = null;
                return Fail(steps, deviceInfo, keyName, null, null);
            }

            steps.Add(new TpmCheckStep(
                TpmCheckStepCode.SignVerify,
                true,
                "签名/验签通过，篡改数据被拒绝。"));

            var report = new TpmLocalCheckReport(
                steps,
                DeviceState.LocalPassed,
                deviceInfo,
                keyName,
                key.DeviceId,
                key.KeyId);
            key.Dispose();
            key = null;
            return report;
        }
        catch (Exception ex)
        {
            steps.Add(new TpmCheckStep(
                key is null ? TpmCheckStepCode.KeyCreated : TpmCheckStepCode.SignVerify,
                false,
                $"异常：{ex.GetType().Name}: {ex.Message}"));
            CleanupFailedKey(key, keyName, keyExistedBefore);
            key = null;
            return Fail(steps, deviceInfo, keyName, null, null);
        }
    }

    private static void CleanupFailedKey(TpmDeviceKey? key, string keyName, bool existedBefore)
    {
        if (key is not null)
        {
            try
            {
                if (!existedBefore)
                {
                    key.DeletePersistedKey();
                }
            }
            catch
            {
                // 清理失败不能掩盖原始失败原因；测试/人工可后续清理。
            }
            finally
            {
                key.Dispose();
            }
        }
        else if (!existedBefore)
        {
            try
            {
                TpmDeviceKey.DeletePersisted(keyName);
            }
            catch
            {
            }
        }
    }

    private static TpmLocalCheckReport Fail(
        List<TpmCheckStep> steps,
        TpmDeviceInfo? deviceInfo,
        string keyName,
        string? deviceId = null,
        string? keyId = null)
    {
        return new TpmLocalCheckReport(
            steps,
            DeviceState.LocalFailed,
            deviceInfo,
            keyName,
            deviceId,
            keyId);
    }
}
