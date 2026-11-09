namespace NPMC.Launcher.Core.Instances;

/// <summary>
/// 整合包自带的堆大小（<c>-Xmx</c>）与本机内存是否匹配。
///
/// 原则（2026-11-09 与用户确认）：
///  - **优先保留整合包作者给的 -Xmx** —— 作者按包的实际内容定的值通常比通用公式更准；
///  - 只有当它超过**本机极限值**（按物理内存自动算出：留 2GB 给系统、最多占 75%）
///    才"篡改"启动参数：把 -Xmx 夹到极限值，其它 JVM 参数原样保留；
///  - 夹紧值 = min(整合包要求, 本机极限值)，不会换成按模组数量估出来的自动值。
/// </summary>
public static class PackHeapPolicy
{
    /// <summary>留给系统与其它程序的余量（MB）。</summary>
    public const int ReserveMb = 2048;

    /// <summary>堆最多占物理内存的比例。</summary>
    public const double MaxShare = 0.75;

    public sealed record Decision(bool KeepPreset, int RecommendedHeapMb, string Reason);

    /// <summary>按物理内存算出本机极限值（MB）。</summary>
    public static int LimitationMb(int totalMb)
    {
        var ceiling = (int)Math.Min(totalMb - ReserveMb, totalMb * MaxShare);
        return Math.Max(ceiling, 512);
    }

    /// <summary>
    /// 判断整合包要求的堆大小能不能照用；不能则给出夹紧后的值。
    /// </summary>
    /// <param name="totalMb">物理内存总量（MB）。</param>
    /// <param name="availableMb">当前可用内存（MB，0 表示未知；只用于说明文字）。</param>
    /// <param name="packHeapMb">整合包要求的 -Xmx（MB）。</param>
    /// <param name="autoHeapMb">自动计算给出的值（MB），仅在整合包没指定 -Xmx 时使用。</param>
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

        var ceiling = LimitationMb(totalMb);

        if (packHeapMb <= ceiling)
        {
            return new Decision(true, packHeapMb,
                $"整合包要求的 {packHeapMb} MB 在本机极限值 {ceiling} MB 之内");
        }

        // 超过极限：夹到极限值本身（不是换成自动值 —— 作者的要求通常是下限，
        // 在能承受的范围内尽量给足）
        var clamped = Math.Min(packHeapMb, ceiling);

        var detail = availableMb > 0 && packHeapMb > availableMb
            ? $"（当前可用内存 {availableMb} MB）"
            : "";

        return new Decision(false, clamped,
            $"整合包要求的 {packHeapMb} MB 超过本机极限值 {ceiling} MB{detail}，已夹紧为 {clamped} MB");
    }
}
