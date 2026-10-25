using System.Text;

namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>
/// 原子写入：先写 .tmp，再整体替换。
/// 避免断电/崩溃时留下半截 JSON 把配置读坏。
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";

        // UTF-8 无 BOM：避免手改配置时被编辑器当成带 BOM 的文件
        await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        // 同一卷内的 rename，NTFS 上是原子的；overwrite 语义可直接覆盖旧文件
        File.Move(tempPath, path, overwrite: true);
    }
}
