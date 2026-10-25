namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 测试用的临时目录。
///
/// 为什么不用 Path.GetTempPath()：
///   在受限执行环境里（例如 DSH 沙箱进程是 Low 完整性令牌），
///   %TEMP% 可能指向一个当前令牌无法创建的目录 —— 普通沙箱模式下它是
///   低完整性友好的专用目录，一旦提权（撤掉沙箱）就变成真实的用户 Temp，
///   于是创建目录直接 UnauthorizedAccessException。
///   测试不应依赖这种环境差异，统一用「测试程序集自己所在的目录」下的子目录：
///   构建产物既然已经写进去了，说明该目录对测试进程必然可写。
/// </summary>
internal static class TestTemp
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "test-scratch");

    public static string NewDirectory(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
}
