using System.Buffers;

namespace NPMC.Launcher.Core.Mods;

/// <summary>
/// CurseForge 的文件指纹算法。
///
/// 与 Modrinth 用 SHA1 不同，CurseForge 用的是 **MurmurHash2（32 位，seed = 1）**，
/// 而且计算前要**跳过所有空白字符**（空格、制表、换行、回车、换页、垂直制表）。
/// 这是社区通行的定义，CurseForge 的 /v1/fingerprints 端点就按它匹配。
///
/// 文件版本**流式两遍**计算：第一遍数过滤后的字节数，第二遍边读边混入。
/// 以前是 `File.ReadAllBytes` + 再复制一份过滤数组；495 个模组会把几百 MB
/// 的大对象塞进 LOH，GC 不常收，表现就是"打开模组页突然涨到 500MB、切页不降"。
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
        ///
        /// **已用真实文件对照 CurseForge 服务端确认**：拿 Sodium 的 jar 打
        /// /v1/fingerprints，Standard 变体命中、其余三种全不命中。
        /// </summary>
        Standard = 0,

        /// <summary>
        /// 在 Standard 基础上再跳过 垂直制表(0x0B) 与 换页(0x0C)。
        ///
        /// **实测是错的**：拿真实 Sodium jar 打 CurseForge 的 /v1/fingerprints，
        /// 只有 Standard 变体命中。保留它仅为诊断命令对比用。
        /// </summary>
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

        // 先数过滤后的长度（Murmur2 的初始 h 依赖长度），再逐字节混入，不分配副本。
        var length = 0;
        foreach (var value in data)
            if (!ShouldSkip(value, filter)) length++;

        var state = new Hasher(seed, length);

        foreach (var value in data)
            if (!ShouldSkip(value, filter)) state.Push(value);

        return state.Finish();
    }

    public static async Task<uint> ComputeFileAsync(
        string path,
        WhitespaceFilter filter = WhitespaceFilter.Standard,
        uint seed = DefaultSeed,
        CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            // 第一遍：过滤后的长度
            var length = 0;
            await using (var stream = Open(path))
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    for (var i = 0; i < read; i++)
                        if (!ShouldSkip(buffer[i], filter)) length++;
                }
            }

            // 第二遍：边读边算
            var state = new Hasher(seed, length);
            await using (var stream = Open(path))
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    for (var i = 0; i < read; i++)
                    {
                        var value = buffer[i];
                        if (!ShouldSkip(value, filter)) state.Push(value);
                    }
                }
            }

            return state.Finish();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream Open(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);

    private struct Hasher
    {
        private uint _h;
        private uint _block;
        private int _count;

        public Hasher(uint seed, int length)
        {
            _h = seed ^ (uint)length;
            _block = 0;
            _count = 0;
        }

        public void Push(byte value)
        {
            _block |= (uint)value << (8 * _count);

            if (++_count != 4) return;

            var k = _block;
            k *= M;
            k ^= k >> R;
            k *= M;

            _h *= M;
            _h ^= k;

            _block = 0;
            _count = 0;
        }

        public uint Finish()
        {
            var h = _h;

            if (_count >= 3) h ^= ((_block >> 16) & 0xFF) << 16;
            if (_count >= 2) h ^= ((_block >> 8) & 0xFF) << 8;
            if (_count >= 1)
            {
                h ^= _block & 0xFF;
                h *= M;
            }

            h ^= h >> 13;
            h *= M;
            h ^= h >> 15;

            return h;
        }
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
