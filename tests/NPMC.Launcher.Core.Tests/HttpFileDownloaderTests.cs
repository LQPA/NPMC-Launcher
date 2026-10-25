using System.Security.Cryptography;
using NPMC.Launcher.Infrastructure.Http;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 候选地址回退。
/// 只用回环地址做失败场景，测试本身不依赖外网。
/// </summary>
public class HttpFileDownloaderTests
{
    [Fact]
    public async Task Skips_existing_file_when_sha1_matches_without_any_request()
    {
        var directory = TestTemp.NewDirectory("downloader");
        try
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var target = Path.Combine(directory, "payload.bin");
            await File.WriteAllBytesAsync(target, payload);

            var sha1 = Convert.ToHexString(SHA1.HashData(payload));
            using var http = HttpFileDownloader.CreateClient();
            var downloader = new HttpFileDownloader(http);

            var outcome = await downloader.EnsureFromAsync(
                new[] { "http://127.0.0.1:1/should-not-be-requested" }, target, sha1);

            Assert.False(outcome.Downloaded);
            Assert.Empty(outcome.Attempts);   // 一次都没尝试 → 确实没有联网
            Assert.Equal(string.Empty, outcome.Url);
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public async Task Throws_and_reports_every_attempt_when_all_candidates_fail()
    {
        var directory = TestTemp.NewDirectory("downloader");
        try
        {
            var target = Path.Combine(directory, "payload.bin");
            using var http = HttpFileDownloader.CreateClient();
            var downloader = new HttpFileDownloader(http);

            // 127.0.0.1:1 必然连接被拒，两个候选都会失败
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                downloader.EnsureFromAsync(
                    new[] { "http://127.0.0.1:1/a", "http://127.0.0.1:1/b" },
                    target, sha1: null));

            Assert.Contains("2 个候选地址都失败", exception.Message);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".tmp"));   // 失败后不留半截文件
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }

    [Fact]
    public async Task Rejects_an_empty_candidate_list()
    {
        var directory = TestTemp.NewDirectory("downloader");
        try
        {
            using var http = HttpFileDownloader.CreateClient();
            var downloader = new HttpFileDownloader(http);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                downloader.EnsureFromAsync(Array.Empty<string>(), Path.Combine(directory, "x"), null));
        }
        finally
        {
            TestTemp.TryDelete(directory);
        }
    }
}
