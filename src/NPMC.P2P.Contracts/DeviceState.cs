namespace NPMC.P2P.Contracts;

/// <summary>
/// 设备状态，与设计文档 §2.4 的 8 个状态一一对应。
/// 注意：只有服务器确认的 <see cref="TpmVerified"/> 才允许 P2P。
/// </summary>
public enum DeviceState
{
    NotChecked,
    LocalChecking,
    LocalFailed,
    LocalPassed,
    Attesting,
    TpmVerified,
    AttestationFailed,
    Revoked,
}

/// <summary>
/// 设备状态机（纯逻辑，便于单元测试与服务端复用）。
/// 任何未声明的迁移都必须抛异常，不允许“顺手放行”。
/// </summary>
public static class DeviceStateMachine
{
    private static readonly IReadOnlyDictionary<DeviceState, DeviceState[]> Allowed =
        new Dictionary<DeviceState, DeviceState[]>
        {
            [DeviceState.NotChecked] = new[] { DeviceState.LocalChecking },
            [DeviceState.LocalChecking] = new[] { DeviceState.LocalPassed, DeviceState.LocalFailed },
            [DeviceState.LocalFailed] = new[] { DeviceState.LocalChecking, DeviceState.Revoked },
            [DeviceState.LocalPassed] = new[] { DeviceState.Attesting, DeviceState.Revoked },
            [DeviceState.Attesting] = new[] { DeviceState.TpmVerified, DeviceState.AttestationFailed },
            [DeviceState.AttestationFailed] = new[] { DeviceState.Attesting, DeviceState.Revoked },
            [DeviceState.TpmVerified] = new[] { DeviceState.Attesting, DeviceState.Revoked },
            [DeviceState.Revoked] = Array.Empty<DeviceState>(),
        };

    public static IReadOnlyList<DeviceState> AllStates { get; } =
        Enum.GetValues<DeviceState>();

    public static bool CanTransition(DeviceState from, DeviceState to)
    {
        return Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;
    }

    /// <summary>执行迁移；非法迁移抛 <see cref="InvalidOperationException"/>。</summary>
    public static DeviceState Transition(DeviceState from, DeviceState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"非法的设备状态迁移：{from} -> {to}");
        }

        return to;
    }

    /// <summary>只有服务器确认的 TPM_VERIFIED 才允许 P2P。</summary>
    public static bool IsP2PAllowed(DeviceState state) => state == DeviceState.TpmVerified;

    /// <summary>失败关闭：除 TPM_VERIFIED 外一律禁用 P2P，不做任何降级。</summary>
    public static bool MustDisableP2P(DeviceState state) => !IsP2PAllowed(state);

    /// <summary>线上/日志使用的稳定名称（与设计文档一致）。</summary>
    public static string ToWireName(DeviceState state) => state switch
    {
        DeviceState.NotChecked => "NOT_CHECKED",
        DeviceState.LocalChecking => "LOCAL_CHECKING",
        DeviceState.LocalFailed => "LOCAL_FAILED",
        DeviceState.LocalPassed => "LOCAL_PASSED",
        DeviceState.Attesting => "ATTESTING",
        DeviceState.TpmVerified => "TPM_VERIFIED",
        DeviceState.AttestationFailed => "ATTESTATION_FAILED",
        DeviceState.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static bool TryParseWireName(string? value, out DeviceState state)
    {
        foreach (var candidate in AllStates)
        {
            if (string.Equals(ToWireName(candidate), value, StringComparison.Ordinal))
            {
                state = candidate;
                return true;
            }
        }

        state = default;
        return false;
    }
}
