using System.Text;
using NPMC.Launcher.Infrastructure.Translation;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 翻译缓存的 CSV 存储。
///
/// 原来每条译文一个 sha1.txt（只有译文、没有原文，完全没法维护），
/// 现在换成「一个可读可手改的 CSV + 一个纯元数据 JSON」。
/// 这里的重点是 CSV 的转义/换行、重复 key、旧文件迁移与容量淘汰。
/// </summary>
public class TranslationCacheTests
{
    [Fact]
    public void Round_trips_text_with_commas_quotes_and_newlines()
    {
        var root = CreateTempRoot();
        try
        {
            var cache = new TranslationCache(root);
            var source = "Adds a card, \"Infinity Range\" booster.\nSecond line.";
            var translation = "添加一张卡，\"无限范围\" 助推器。\n第二行。";

            cache.Store("key-1", "zh-CN", source, translation);

            // 新实例重新读盘，确认能完整解析（多行字段不能把行拆坏）
            var reloaded = new TranslationCache(root);
            Assert.True(reloaded.TryGet("key-1", out var text));
            Assert.Equal(translation, text);

            var reloaded2 = new TranslationCache(root);
            Assert.Equal(1, reloaded2.Count);

            // CSV 里应当能看到原文与译文，方便人工校对
            var csv = File.ReadAllText(Path.Combine(root, "translations.csv"), Encoding.UTF8);
            Assert.Contains("key-1", csv);
            Assert.Contains("zh-CN", csv);
            Assert.Contains("Infinity Range", csv);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Stores_metadata_json_separately()
    {
        var root = CreateTempRoot();
        try
        {
            var cache = new TranslationCache(root);
            cache.Store("k", "zh-CN", "source", "译文");

            var metaPath = Path.Combine(root, "meta.json");
            Assert.True(File.Exists(metaPath));

            var meta = File.ReadAllText(metaPath, Encoding.UTF8);
            Assert.Contains("\"schema\": 1", meta.Replace("  ", ""));
            Assert.Contains("entries", meta);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Overwriting_a_key_keeps_one_entry()
    {
        var root = CreateTempRoot();
        try
        {
            var cache = new TranslationCache(root);
            cache.Store("k", "zh-CN", "source", "旧译文");
            cache.Store("k", "zh-CN", "source", "新译文");

            var reloaded = new TranslationCache(root);
            Assert.True(reloaded.TryGet("k", out var text));
            Assert.Equal("新译文", text);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Migrates_legacy_hash_files_lazily()
    {
        var root = CreateTempRoot();
        try
        {
            var legacy = Path.Combine(root, "deadbeef.txt");
            File.WriteAllText(legacy, "旧版缓存的译文");

            var cache = new TranslationCache(root);
            Assert.False(cache.TryGet("legacy-key", out _));   // 还没有新格式

            Assert.True(cache.TryImportLegacy(legacy, "legacy-key", "zh-CN", "原文", out var migrated));
            Assert.Equal("旧版缓存的译文", migrated);
            Assert.False(File.Exists(legacy));                 // 旧文件被删掉

            var reloaded = new TranslationCache(root);
            Assert.True(reloaded.TryGet("legacy-key", out var text));
            Assert.Equal("旧版缓存的译文", text);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Broken_csv_falls_back_to_empty_cache()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "translations.csv"), "\"未闭合的引号,,,");

            var cache = new TranslationCache(root);
            Assert.False(cache.TryGet("any", out _));

            cache.Store("k", "zh-CN", "src", "译文");
            var reloaded = new TranslationCache(root);
            Assert.True(reloaded.TryGet("k", out var text));
            Assert.Equal("译文", text);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nmpc-translation-tests", Guid.NewGuid().ToString("N"));
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
