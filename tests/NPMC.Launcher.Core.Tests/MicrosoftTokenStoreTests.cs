using NPMC.Launcher.Infrastructure.Accounts;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class MicrosoftTokenStoreTests
{
    [Fact]
    public void DeleteAll_removes_only_token_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "npmc-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "a.bin"), "x");
            File.WriteAllText(Path.Combine(directory, "b.bin"), "y");
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "z");

            var store = new MicrosoftTokenStore(directory);
            var deleted = store.DeleteAll();

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(Path.Combine(directory, "a.bin")));
            Assert.False(File.Exists(Path.Combine(directory, "b.bin")));
            Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Protect_keeps_existing_token_files_readable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "npmc-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tokenPath = Path.Combine(directory, "a.bin");
        File.WriteAllText(tokenPath, "token");

        try
        {
            _ = new MicrosoftTokenStore(directory);

            // 回归：icacls /T + (OI)(CI) 曾把文件 ACE 标成 inherit-only，
            // 结果当前用户自己都读不了 token。
            Assert.Equal("token", File.ReadAllText(tokenPath));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Constructor_marks_token_directory_hidden_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "npmc-tokens-" + Guid.NewGuid().ToString("N"));

        try
        {
            _ = new MicrosoftTokenStore(directory);

            var attributes = File.GetAttributes(directory);
            Assert.True(attributes.HasFlag(FileAttributes.Hidden));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeleteAll_on_missing_directory_returns_zero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "npmc-tokens-" + Guid.NewGuid().ToString("N"));

        var count = new MicrosoftTokenStore(directory).DeleteAll();

        Assert.Equal(0, count);
        // 构造函数会创建目录并尝试收紧 ACL；这里只确认不抛异常
        Assert.True(Directory.Exists(directory));
        try { Directory.Delete(directory, recursive: true); } catch { }
    }
}
