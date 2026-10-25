using System.Security.Cryptography;

namespace NPMC.Launcher.Infrastructure.Hashing;

public static class FileHashing
{
    /// <summary>计算文件的 SHA1（大写十六进制），与 Modrinth / Mojang 的表示一致。</summary>
    public static async Task<string> Sha1Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
