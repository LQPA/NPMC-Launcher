using System.Diagnostics;

namespace NPMC.Launcher.App.Diagnostics;

/// <summary>
/// 启动耗时埋点。
///
/// 只在环境变量 <c>NPMC_TIMING=1</c> 时写日志：正常使用不产生噪声，
/// 排查"首屏为什么慢"时打开它，日志写在 <c>&lt;root&gt;\logs\launcher.log</c>。
///
/// 用法：
///   $env:NPMC_TIMING=1
///   NPMC.Launcher.App.exe --dump-instances out.txt --root E:\aaaa --no-remember
///   Get-Content E:\aaaa\logs\launcher.log
/// </summary>
internal sealed class LoadTiming
{
    private readonly string _root;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _phase = Stopwatch.StartNew();
    private readonly bool _enabled;

    public LoadTiming(string root)
    {
        _root = root;
        _enabled = Environment.GetEnvironmentVariable("NPMC_TIMING") is "1" or "true" or "TRUE";
    }

    public bool Enabled => _enabled;

    /// <summary>结束当前阶段并记录耗时（同一阶段名可以多次记录，便于看"第几次刷新"）。</summary>
    public void Mark(string phase, string? detail = null)
    {
        if (!_enabled) return;

        var elapsed = _phase.ElapsedMilliseconds;
        _phase.Restart();

        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"  {detail}";
        LauncherLog.Write(_root, $"[timing] {phase}: {elapsed} ms   (累计 {_total.ElapsedMilliseconds} ms){suffix}");
    }

    public void Done()
    {
        if (!_enabled) return;
        LauncherLog.Write(_root, $"[timing] 启动完成，合计 {_total.ElapsedMilliseconds} ms");
    }
}
