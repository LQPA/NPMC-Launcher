namespace NPMC.Launcher.Core.Content;

/// <summary>
/// 一个已安装的内容条目（模组 / 资源包 / 光影）。
///
/// <see cref="IsEnabled"/> 的判定来自文件名后缀，不是读文件内容 —— 这是业界通行做法：
/// 游戏只会加载 &lt;name&gt;.jar，把文件重命名成 &lt;name&gt;.jar.disabled 就等于禁用，
/// 既不需要改存档，也不需要维护额外的清单文件。
/// </summary>
public sealed record ContentEntry
{
    /// <summary>实际磁盘路径。</summary>
    public required string Path { get; init; }

    /// <summary>显示用名称（已去掉 .jar / .zip / .disabled）。</summary>
    public required string Name { get; init; }

    /// <summary>磁盘上的真实文件名。</summary>
    public required string FileName { get; init; }

    public required ContentKind Kind { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>是文件夹形式的包（资源包/光影常见），不能按文件大小处理。</summary>
    public required bool IsDirectory { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }
}
