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
    private const uint M = 0x5bd1e995;
    private const int R = 24;

    /// <summary>计算前要跳过哪些字节。不同实现对"空白"的定义不一致，见各成员说明。</summary>
    public enum WhitespaceFilter
    {
        /// <summary>
        /// 只跳过 制表(0x09) / 换行(0x0A) / 回车(0x0D) / 空格(0x20)。
        /// 这是多数第三方实现采用的定义，也是官方文档 "tabs, spaces, newlines" 的字面含义。
        /// </summary>
        Standard = 0,

        /// <summary>在 Standard 基础上再跳过 垂直制表(0x0B) 与 换页(0x0C)。</summary>
        Extended = 1,

        /// <summary>完全不过滤（原样哈希）。</summary>
        None = 2,
    }

    public const uint DefaultSeed = 1;

    /// <summary>按 Standard 定义计算（seed = 1）。</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
        => Compute(data, WhitespaceFilter.Standard, DefaultSeed);

    public static uint Compute(ReadOnlySpan<byte> data, WhitespaceFilter filter, uint seed = DefaultSeed)
    {
        if (filter == WhitespaceFilter.None) return ComputeHashed(data, seed);

        var filtered = new byte[data.Length];
        var count = 0;

        foreach (var value in data)
        {
            if (ShouldSkip(value, filter)) continue;
            filtered[count++] = value;
        }

        return ComputeHashed(filtered.AsSpan(0, count), seed);
    }

    public static async Task<uint> ComputeFileAsync(
        string path,
        WhitespaceFilter filter = WhitespaceFilter.Standard,
        uint seed = DefaultSeed,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Compute(bytes, filter, seed);
    }

    private static uint ComputeHashed(ReadOnlySpan<byte> bytes, uint seed)
    {
        var length = bytes.Length;
        var h = seed ^ (uint)length;

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

    private static bool ShouldSkip(byte value, WhitespaceFilter filter) => value switch
    {
        0x09 or 0x0A or 0x0D or 0x20 => true,                       // 制表 / 换行 / 回车 / 空格
        0x0B or 0x0C => filter == WhitespaceFilter.Extended,        // 垂直制表 / 换页
        _ => false,
    };
}
