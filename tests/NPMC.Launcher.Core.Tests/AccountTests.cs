using NPMC.Launcher.Core.Accounts;
using NPMC.Launcher.Core.Launch;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

public class PlayerAccountTests
{
    [Fact]
    public void OfflineUuid_matches_the_java_name_uuid_rule()
    {
        // Java: UUID.nameUUIDFromBytes("OfflinePlayer:Steve".getBytes(UTF_8))
        Assert.Equal("5627dd98e6be3c21b8a8e92344183641", PlayerAccount.OfflineUuid("Steve"));
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", PlayerAccount.OfflineUuid("Notch"));
    }

    [Fact]
    public void OfflineUuid_is_deterministic_and_case_sensitive()
    {
        Assert.Equal(PlayerAccount.OfflineUuid("Steve"), PlayerAccount.OfflineUuid("Steve"));
        Assert.NotEqual(PlayerAccount.OfflineUuid("Steve"), PlayerAccount.OfflineUuid("steve"));
    }

    [Fact]
    public void OfflineUuid_sets_version_3_and_rfc4122_variant()
    {
        var uuid = PlayerAccount.OfflineUuid("Steve");
        var version = Convert.ToInt32(uuid.Substring(12, 1), 16);
        var variant = Convert.ToInt32(uuid.Substring(16, 1), 16);

        Assert.Equal(3, version);
        Assert.InRange(variant, 8, 11);
    }

    [Fact]
    public void CreateOffline_keeps_the_name_and_derives_the_uuid()
    {
        var account = PlayerAccount.CreateOffline("  Alex  ");

        Assert.Equal("Alex", account.Username);
        Assert.Equal(PlayerAccount.OfflineUuid("Alex"), account.Uuid);
        Assert.Equal(AccountKind.Offline, account.Kind);
        Assert.Equal(32, account.Uuid.Length);
        Assert.Equal("离线", account.KindText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("名字太长太长了名字太长太长了名字太长太长了")]
    [InlineData("bad name")]
    [InlineData("bad-name")]
    [InlineData("bad.name")]
    public void Invalid_usernames_are_rejected(string username)
    {
        Assert.False(PlayerAccount.IsValidUsername(username));
        Assert.Throws<ArgumentException>(() => PlayerAccount.CreateOffline(username));
    }

    [Fact]
    public void LaunchContext_offline_carries_username_and_uuid()
    {
        var account = PlayerAccount.CreateOffline("Steve");
        var context = LaunchContext.Offline(account.Username, account.Uuid);

        Assert.Equal("Steve", context.PlayerName);
        Assert.Equal(account.Uuid, context.Uuid);
        Assert.Equal("legacy", context.UserType);
    }
}

public class AccountCatalogTests
{
    [Fact]
    public void Add_offline_rejects_duplicate_usernames_ignoring_case()
    {
        var catalog = new AccountCatalog();
        catalog.AddOffline("Steve");

        var error = Assert.Throws<InvalidOperationException>(() => catalog.AddOffline("steve"));
        Assert.Contains("Steve", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rename_recomputes_the_offline_uuid()
    {
        var catalog = new AccountCatalog();
        var account = catalog.AddOffline("Steve");

        var renamed = catalog.Rename(account.Id, "Alex");

        Assert.NotNull(renamed);
        Assert.Equal("Alex", renamed!.Username);
        Assert.Equal(PlayerAccount.OfflineUuid("Alex"), renamed.Uuid);
        Assert.Equal(account.Id, renamed.Id);
    }

    [Fact]
    public void Remove_and_find_work_by_id_and_username()
    {
        var catalog = new AccountCatalog();
        var account = catalog.AddOffline("Steve");

        Assert.NotNull(catalog.FindByUsername("steve"));
        Assert.True(catalog.Remove(account.Id));
        Assert.Null(catalog.Find(account.Id));
        Assert.Empty(catalog.Accounts);
    }

    [Fact]
    public void Validate_detects_duplicate_ids_and_names()
    {
        var catalog = new AccountCatalog(new[]
        {
            new PlayerAccount("id", "Steve", PlayerAccount.OfflineUuid("Steve"), AccountKind.Offline, DateTimeOffset.UtcNow),
            new PlayerAccount("id", "steve", PlayerAccount.OfflineUuid("steve"), AccountKind.Offline, DateTimeOffset.UtcNow),
        });

        var problems = catalog.Validate();

        Assert.Contains(problems, problem => problem.Contains("重复"));
    }
}
