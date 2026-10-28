using System.Runtime.InteropServices;

namespace NPMC.Launcher.Core.Instances;

/// <summary>一次自动内存推荐的完整结果（给界面显示"为什么是这个数"）。</summary>
public sealed record MemoryRecommendation(int MemoryMb, string Reason, bool LimitedByFreeMemory);

/// <summary>
/// 「自动内存」：先按实例“需要多少”推荐，再用当前空闲内存兜底。
///
/// 需要多少：
///   - 有整合包推荐值的（ATM10 / ATM9 / GTNH / RLCraft 等，见
///     <see cref="ModpackMemoryRecommendations"/>）直接用推荐值；
///   - 否则按模组数量分档：≤20 → 4 GB，≤60 → 5 GB，≤120 → 6 GB，
///     ≤200 → 8 GB，≤300 → 10 GB，更多 → 12 GB。
///
/// 兜底：不超过当前空闲内存的 75%、物理内存的一半、16 GB；最低 2 GB，按 512 MB 对齐。
/// 每次启动都重新计算，启动器界面显示的只是"此刻的建议值"。
/// </summary>
public static class MemoryAutoCalculator
{
    public const int MinimumMb = 2048;
    public const int MaximumMb = 16384;
    public const int StepMb = 512;

    private const long Megabyte = 1024L * 1024L;
    private const double CeilingShare = 0.75; // 最多占空闲内存的 75%

    /// <summary>当前机器的物理内存与空闲物理内存（字节）。</summary>
    public static (long Total, long Available) GetMemoryInfo()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)status.TotalPhysical, (long)status.AvailablePhysical);
        }
        catch
        {
            // 取不到就退回 GC 的信息
        }

        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (total > 0) return (total, total / 2);
        }
        catch
        {
            // 忽略
        }

        return (8 * Megabyte * 1024L, 4 * Megabyte * 1024L);
    }

    /// <summary>纯按模组数量估算（不参考整合包推荐）。</summary>
    public static int ModTier(int modsCount) => modsCount switch
    {
        // 纯原版：2048 对 1.21 也够用；更老的版本 1024 虽然能跑，但高清材质/高渲染距离会吃紧
        0 => 2048,
        <= 20 => 4096,
        <= 60 => 5120,
        <= 120 => 6144,
        <= 200 => 8192,
        <= 300 => 10240,
        _ => 12288,
    };

    /// <summary>按当前机器状态推荐。</summary>
    public static MemoryRecommendation RecommendForCurrentMachine(int modsCount, string? modpackName)
    {
        var (total, available) = GetMemoryInfo();
        return Recommend(modsCount, modpackName, total, available);
    }

    public static MemoryRecommendation Recommend(
        int modsCount, string? modpackName, long totalPhysicalBytes, long availablePhysicalBytes)
    {
        var totalMb = Math.Max(1024, totalPhysicalBytes / Megabyte);
        var freeMb = availablePhysicalBytes > 0 ? availablePhysicalBytes / Megabyte : totalMb / 2;

        int need;
        string reason;

        if (ModpackMemoryRecommendations.Find(modpackName) is { } pack)
        {
            need = pack.MemoryMb;
            reason = $"{pack.Label} 推荐 {pack.MemoryMb} MB";
        }
        else
        {
            need = ModTier(modsCount);
            reason = modsCount > 0
                ? $"按 {modsCount} 个模组估算 {need} MB"
                : $"纯原版建议 {need} MB";
        }

        var capacity = Math.Min((long)(freeMb * CeilingShare), Math.Min(totalMb / 2, MaximumMb));
        capacity = Math.Max(capacity, MinimumMb);

        var limited = need > capacity;
        var memory = (int)Math.Min(need, capacity);
        memory = Math.Max(MinimumMb, memory / StepMb * StepMb);
        memory = (int)Math.Min(memory, capacity);

        if (limited) reason += $"（空闲内存不足，已降到 {memory} MB）";

        return new MemoryRecommendation(memory, reason, limited);
    }

    /// <summary>给界面显示用的一行说明。</summary>
    public static string Describe(MemoryRecommendation recommendation, long totalBytes, long availableBytes)
        => $"自动：{recommendation.MemoryMb} MB（{recommendation.Reason} · 空闲 {FormatGigabytes(availableBytes)} / 物理 {FormatGigabytes(totalBytes)}）";

    public static string FormatGigabytes(long bytes)
        => (bytes / (double)(1024L * 1024L * 1024L)).ToString("0.#") + " GB";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
