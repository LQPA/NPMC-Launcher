namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 整合包安装时的内存策略：作者在 manifest 里给了推荐值（CF 的 recommendedRam）就用它，
/// 否则沿用全局默认（可能是自动内存）。推荐值会关掉自动内存，保证装完就是作者建议的数值；
/// 用户之后仍可在实例页改回自动或手动。
/// </summary>
public static class ModpackInstallMemory
{
    public const int MinimumMb = 2048;
    public const int MaximumMb = 32768;

    public static InstanceSettings Apply(InstanceSettings settings, int recommendedRamMb)
    {
        if (recommendedRamMb <= 0) return settings;

        return settings with
        {
            MemoryMb = Math.Clamp(recommendedRamMb, MinimumMb, MaximumMb),
            AutoMemory = false,
        };
    }
}
