using NPMC.Launcher.Core.Mods;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// CurseForge 的 MurmurHash2 指纹。
///
/// 这里能验证的是"确定性"与"空白被忽略"这两个性质；
/// 与 CurseForge 服务器是否完全一致，只能拿真实文件 + 有效 API key 打
/// /v1/fingerprints 才能最终确认（见 mods check --curseforge）。
/// </summary>
public class MurmurHash2Tests
{
    [Fact]
    public void Same_input_gives_same_fingerprint()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7 };

        Assert.Equal(MurmurHash2.Compute(data), MurmurHash2.Compute(data));
    }

    [Fact]
    public void Different_input_gives_different_fingerprint()
    {
        Assert.NotEqual(
            MurmurHash2.Compute(new byte[] { 1, 2, 3 }),
            MurmurHash2.Compute(new byte[] { 1, 2, 4 }));
    }

    [Fact]
    public void Whitespace_is_ignored()
    {
        var tight = new byte[] { (byte)'a', (byte)'b', (byte)'c' };
        var spaced = new byte[] { (byte)'a', 0x20, (byte)'b', 0x09, (byte)'c', 0x0D, 0x0A };

        Assert.Equal(MurmurHash2.Compute(tight), MurmurHash2.Compute(spaced));
    }

    [Fact]
    public void Non_ascii_space_byte_is_not_treated_as_whitespace()
    {
        // 0xA0（不换行空格）不是 CurseForge 忽略的字符，必须参与哈希
        Assert.NotEqual(
            MurmurHash2.Compute(new byte[] { (byte)'a', (byte)'b' }),
            MurmurHash2.Compute(new byte[] { (byte)'a', 0xA0, (byte)'b' }));
    }

    [Fact]
    public void Empty_input_is_stable_and_deterministic()
    {
        var first = MurmurHash2.Compute(ReadOnlySpan<byte>.Empty);
        var second = MurmurHash2.Compute(new byte[] { 0x20, 0x0A, 0x09 });   // 全是空白

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task File_and_memory_versions_agree()
    {
        var directory = TestTemp.NewDirectory("murmur");
        try
        {
            var path = Path.Combine(directory, "sample.bin");
            var payload = new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0x0A };
            await File.WriteAllBytesAsync(path, payload);

            Assert.Equal(MurmurHash2.Compute(payload), await MurmurHash2.ComputeFileAsync(path));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }
}
