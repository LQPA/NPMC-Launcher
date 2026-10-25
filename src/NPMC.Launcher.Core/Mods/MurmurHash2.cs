namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// CurseForge 的文件指纹算法。
///
/// 与 Modrinth 用 SHA1 不同，CurseForge 用的是 **MurmurHash2（32 位，seed = 1）**，
/// 而且计算前要**跳过所有空白字符**（空格、制表、换行、回车、换页、垂直制表）。
/// 这是社区通行的定义，CurseForge 的 /v1/fingerprints 端点就按它匹配。
///
/// 注意：这里为了可读性先把文件读进内存再过滤。
/// 模组 jar 通常只有几 MB，这个代价可以接受；若将来要指纹化整合包级别的大文件，
/// 应改成流式增量计算。
/// </summary>
public static class MurmurHash2
{
    private const uint Seed = 1;
    private const uint M = 0x5bd1e995;
    private const int R = 24;

    /// <summary>计算字节序列的指纹（已过滤空白字符）。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        // 1) 过滤空白，得到参与哈希的有效字节
        var filtered = new byte[data.Length];
        var count = 0;
        foreach (var value in data)
        {
            if (IsIgnoredWhitespace(value)) continue;
            filtered[count++] = value;
        }

        var bytes = filtered.AsSpan(0, count);
        return ComputeFiltered(bytes);
    }

    public static async Task<uint> ComputeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Compute(bytes);
    }

    private static uint ComputeFiltered(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.Length;
        var h = Seed ^ (uint)length;

        var i = 0;
        while (length - i >= 4)
        {
            var k = (uint)(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24));

            k *= M;
            k ^= k >> R;
            k *= M;

            h *= M;
            h ^= k;

            i += 4;
        }

        var remaining = length - i;
        if (remaining >= 3) h ^= (uint)bytes[i + 2] << 16;
        if (remaining >= 2) h ^= (uint)bytes[i + 1] << 8;
        if (remaining >= 1)
        {
            h ^= bytes[i];
            h *= M;
        }

        h ^= h >> 13;
        h *= M;
        h ^= h >> 15;

        return h;
    }

    /// <summary>CurseForge 在计算前会忽略的空白字符。</summary>
    private static bool IsIgnoredWhitespace(byte value) => value switch
    {
        0x20    // 空格
        or 0x09 // 制表 \t
        or 0x0A // 换行 \n
        or 0x0D // 回车 \r
        or 0x0B // 垂直制表 \v
        or 0x0C // 换页 \f
        => true,
        _ => false,
    };
}
