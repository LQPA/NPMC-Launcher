using System.Text;
using NPMC.Launcher.Infrastructure.Translation;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 翻译缓存「只读查询」。（网络请求本身不在这里测：公共端点的可用性不该影响测试）
///
/// 背景：详情打开时不再自动请求翻译接口，先调 <see cref="TranslationClient.TryGetCached"/>，
/// 命中就直接显示译文，没命中的话由用户点「机翻」按钮才真正发请求。
/// </summary>
public class TranslationClientCacheTests
{
    [Fact]
    public void Cached_translation_is_returned_without_network()
    {
        var root = CreateTempRoot();
        try
        {
            var text = "This mod adds several useful keybinds.";
            var language = "zh-CN";

            // 先用缓存工具写一条（key = sha1(语言|原文)）
            var cache = new TranslationCache(root);
            cache.Store(TranslationCache.Sha1($"{language}|{text}"), language, text, "这个模组添加了几个有用的快捷键。");

            using var http = new HttpClient();
            var client = new TranslationClient(http, root);

            Assert.True(client.TryGetCached(text, language, out var translated));
            Assert.Equal("这个模组添加了几个有用的快捷键。", translated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Missing_entry_returns_false()
    {
        var root = CreateTempRoot();
        try
        {
            using var http = new HttpClient();
            var client = new TranslationClient(http, root);

            Assert.False(client.TryGetCached("never seen before", "zh-CN", out _));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Truncation_is_applied_consistently_when_looking_up()
    {
        var root = CreateTempRoot();
        try
        {
            var language = "zh-CN";
            var longText = new string('a', 7000);   // 超过 6000 字上限

            var cache = new TranslationCache(root);
            var storedText = longText[..6000];
            cache.Store(TranslationCache.Sha1($"{language}|{storedText}"), language, storedText, "译文");

            using var http = new HttpClient();
            var client = new TranslationClient(http, root);

            Assert.True(client.TryGetCached(longText, language, out var translated));
            Assert.Equal("译文", translated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-translation-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响结论
        }
    }
}
