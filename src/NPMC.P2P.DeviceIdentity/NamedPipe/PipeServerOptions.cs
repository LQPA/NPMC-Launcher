using NPMC.P2P.DeviceIdentity.Identity;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

/// <summary>
/// Named Pipe 服务端策略。默认值面向生产：只接受启动器直接拉起的 java/javaw 进程。
/// </summary>
public sealed class PipeServerOptions
{
    public required GameSessionBinding Binding { get; init; }

    public string PipeName { get; init; } = $"nmpc-p2p-{Guid.NewGuid():N}";

    /// <summary>同一个管道名允许的并发服务实例数；游戏重试时不能互相占死。</summary>
    public int MaxServerInstances { get; init; } = 16;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxSignaturesPerMinute { get; init; } = 20;

    /// <summary>客户端父进程必须是启动器进程（默认当前进程）。</summary>
    public bool RequireLauncherParent { get; init; } = true;

    public IReadOnlyCollection<string> AllowedClientImages { get; init; } = new[] { "java.exe", "javaw.exe" };

    /// <summary>启动器 PID；为空时使用当前进程。</summary>
    public int? ExpectedLauncherProcessId { get; init; }
}
