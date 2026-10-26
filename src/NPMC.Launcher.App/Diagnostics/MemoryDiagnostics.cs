using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace NPMC.Launcher.App.Diagnostics;

/// <summary>
/// 存活位图统计。
///
/// 只拿 <see cref="Bitmap"/> 的弱引用：GC 之后还能不能拿到，正好回答了
/// "内存没降是因为还被引用（真泄漏），还是只是还没回收"。
/// 像素字节按 宽 × 高 × 4 估算（BGRA8888），与 Skia 解码后的实际占用同一量级。
/// </summary>
internal static class LiveBitmapTracker
{
    private static readonly List<WeakReference<Bitmap>> Alive = new();
    private static readonly object Gate = new();

    private static int _created;
    private static long _createdBytes;

    public static void Track(Bitmap bitmap)
    {
        var bytes = EstimateBytes(bitmap);

        lock (Gate)
        {
            Alive.Add(new WeakReference<Bitmap>(bitmap));
            _created++;
            _createdBytes += bytes;
        }
    }

    /// <summary>统计还活着的位图。会顺手清掉已经被 GC 的弱引用。</summary>
    public static BitmapSummary Snapshot()
    {
        var live = 0;
        long bytes = 0;
        long largest = 0;
        var largestWidth = 0;
        var largestHeight = 0;

        lock (Gate)
        {
            for (var i = Alive.Count - 1; i >= 0; i--)
            {
                if (!Alive[i].TryGetTarget(out var bitmap))
                {
                    Alive.RemoveAt(i);
                    continue;
                }

                live++;

                var size = SafePixelSize(bitmap);
                var bitmapBytes = (long)size.Width * size.Height * 4;
                bytes += bitmapBytes;

                if (bitmapBytes > largest)
                {
                    largest = bitmapBytes;
                    largestWidth = size.Width;
                    largestHeight = size.Height;
                }
            }

            return new BitmapSummary(
                Live: live,
                Created: _created,
                PixelBytes: bytes,
                LargestWidth: largestWidth,
                LargestHeight: largestHeight);
        }
    }

    /// <summary>
    /// 诊断用：把所有还活着的位图 Dispose 掉，验证"这些内存到底是不是位图本身"。
    /// 只在探测流程末尾调用（此时界面已经切到没有图片的页面），不要放进正常路径。
    /// </summary>
    public static int DisposeAll()
    {
        var targets = new List<Bitmap>();

        lock (Gate)
        {
            foreach (var reference in Alive)
                if (reference.TryGetTarget(out var bitmap))
                    targets.Add(bitmap);

            Alive.Clear();
        }

        foreach (var bitmap in targets)
        {
            try { bitmap.Dispose(); } catch { /* 诊断清理失败不影响结论 */ }
        }

        return targets.Count;
    }

    private static long EstimateBytes(Bitmap bitmap) => SafePixelSize(bitmap) is var size
        ? (long)size.Width * size.Height * 4
        : 0;

    private static (int Width, int Height) SafePixelSize(Bitmap bitmap)
    {
        try
        {
            var size = bitmap.PixelSize;
            return (size.Width, size.Height);
        }
        catch
        {
            return (0, 0);
        }
    }
}

internal readonly record struct BitmapSummary(
    int Live,
    int Created,
    long PixelBytes,
    int LargestWidth,
    int LargestHeight)
{
    public double PixelMegabytes => PixelBytes / 1024d / 1024d;

    public string Describe() =>
        $"live={Live} pixel={PixelMegabytes:F1}MB created={Created}"
        + (LargestWidth > 0 ? $" largest={LargestWidth}x{LargestHeight}" : "");
}

/// <summary>一次内存快照：工作集 / 私有字节 / 托管堆 / GC 次数 / 存活位图。</summary>
internal readonly record struct MemorySnapshot(
    string Label,
    DateTime At,
    long WorkingSet,
    long PeakWorkingSet,
    long PrivateBytes,
    long ManagedBytes,
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    BitmapSummary Bitmaps,
    string Context)
{
    public static MemorySnapshot Capture(string label, string context)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new MemorySnapshot(
            Label: label,
            At: DateTime.Now,
            WorkingSet: process.WorkingSet64,
            PeakWorkingSet: process.PeakWorkingSet64,
            PrivateBytes: process.PrivateMemorySize64,
            ManagedBytes: GC.GetTotalMemory(forceFullCollection: false),
            AllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2),
            Bitmaps: LiveBitmapTracker.Snapshot(),
            Context: context);
    }

    public string ToLine()
    {
        static double Mb(long bytes) => bytes / 1024d / 1024d;

        var line =
            $"{At:HH:mm:ss.fff} [{Label}] ws={Mb(WorkingSet):F1}MB peak={Mb(PeakWorkingSet):F1}MB "
            + $"private={Mb(PrivateBytes):F1}MB managed={Mb(ManagedBytes):F1}MB alloc={AllocatedBytes / 1024d / 1024d:F1}MB "
            + $"gc={Gen0}/{Gen1}/{Gen2} | {Bitmaps.Describe()}";

        if (!string.IsNullOrWhiteSpace(Context)) line += " | " + Context;

        return line;
    }

    /// <summary>强制回收（含终结器），用来区分"还没回收"与"回收不掉"。</summary>
    public static void ForceCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    /// <summary>
    /// 把工作集还给系统。GC 收掉之后再 trim，如果工作集明显下降，
    /// 说明原来那部分是"已释放但没还给系统"，不是泄漏。
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 诊断辅助手段，失败不影响主流程
        }
    }
}

/// <summary>
/// 可选的运行期内存日志。设置 <c>NPMC_MEMORY_TRACE=1</c> 后追加写入
/// <c>&lt;root&gt;\logs\memory.log</c>，用于让用户在自己的操作节奏下复现问题。
/// </summary>
internal static class MemoryTrace
{
    private static readonly object Gate = new();
    private static string? _root;

    public static bool IsEnabled { get; private set; }

    public static void Configure(string root)
    {
        _root = root;
        IsEnabled = Environment.GetEnvironmentVariable("NPMC_MEMORY_TRACE") is "1" or "true" or "TRUE";
    }

    public static void Write(string label, string context = "")
    {
        if (!IsEnabled || _root is null) return;

        var line = MemorySnapshot.Capture(label, context).ToLine();

        lock (Gate)
        {
            try
            {
                var directory = Path.Combine(_root, "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "memory.log"), line + Environment.NewLine);
            }
            catch
            {
                // 诊断日志写入失败不影响功能
            }
        }
    }
}
