namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 整合包自带的堆大小（<c>-Xmx</c>）与本机内存是否匹配。
///
/// 原则（2026-11-09 与用户确认）：**优先保留整合包作者给的 -Xmx** —— 作者按包的实际
/// 内容定的值通常比通用公式更准；只有当用户的物理内存明显不够（例如 8GB 机器装 ATM10）
/// 才强制退回自动计算，否则会直接 OOM / 卡到磁盘交换。
/// </summary>
public static class PackHeapPolicy
{
    /// <summary>留给系统与其它程序的余量（MB）。</summary>
    public const int ReserveMb = 2048;

    /// <summary>堆最多占物理内存的比例。</summary>
    public const double MaxShare = 0.75;

    public sealed record Decision(bool KeepPreset, int RecommendedHeapMb, string Reason);

    /// <summary>
    /// 判断整合包要求的堆大小能不能照用。
    /// </summary>
    /// <param name="totalMb">物理内存总量（MB）。</param>
    /// <param name="availableMb">当前可用内存（MB，0 表示未知）。</param>
    /// <param name="packHeapMb">整合包要求的 -Xmx（MB）。</param>
    /// <param name="autoHeapMb">自动计算给出的值（MB），用于"照用不了"时兜底。</param>
    public static Decision Evaluate(int totalMb, int availableMb, int packHeapMb, int autoHeapMb)
    {
        if (packHeapMb <= 0)
        {
            return new Decision(false, autoHeapMb, "整合包没有指定 -Xmx，使用自动内存");
        }

        if (totalMb <= 0)
        {
            // 读不到物理内存：不冒险，按自动值走
            return new Decision(false, Math.Min(autoHeapMb, packHeapMb), "读不到物理内存，使用自动内存");
        }

        var ceiling = (int)Math.Min(totalMb - ReserveMb, totalMb * MaxShare);
        if (ceiling < 512) ceiling = 512;

        // 当前可用内存不够也放行不了：留 1GB 给 JVM 自身与原生库
        if (availableMb > 0 && packHeapMb + 1024 > availableMb)
        {
            var clampedValue = Math.Min(autoHeapMb, ceiling);
            return new Decision(false, clampedValue,
                $"整合包要求的 {packHeapMb} MB 超过当前可用内存 {availableMb} MB，已夹紧为 {clampedValue} MB");
        }

        if (packHeapMb <= ceiling)
        {
            return new Decision(true, packHeapMb, $"整合包要求的 {packHeapMb} MB 在本机可行（上限 {ceiling} MB）");
        }

        // 物理内存明显不够：把 -Xmx 夹紧到上限，而不是整个丢掉
        var clamped = Math.Min(autoHeapMb, ceiling);

        var reason = availableMb > 0 && packHeapMb > availableMb
            ? $"整合包要求的 {packHeapMb} MB 超过当前可用内存 {availableMb} MB，已夹紧为 {clamped} MB"
            : $"整合包要求的 {packHeapMb} MB 超过本机安全上限 {ceiling} MB，已夹紧为 {clamped} MB";

        return new Decision(false, clamped, reason);
    }
}
