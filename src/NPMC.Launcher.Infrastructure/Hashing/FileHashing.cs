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

    /// <summary>
    /// 单次读取同时算 SHA1 与 SHA512（都是大写十六进制）。
    ///
    /// .mrpack 的文件表要求 sha1 + sha512；几百 MB 的整合包不该被读两遍。
    /// </summary>
    public static async Task<(string Sha1, string Sha512)> Sha1AndSha512Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            sha1.AppendData(buffer, 0, read);
            sha512.AppendData(buffer, 0, read);
        }

        return (Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(sha512.GetHashAndReset()));
    }
}
