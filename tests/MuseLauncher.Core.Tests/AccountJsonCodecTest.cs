using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

/** 账号列表 JSON 编解码的容错——移植自安卓 AccountJsonCodecTest */
public class AccountJsonCodecTest
{
    private static List<Account> Sample()
    {
        var l = new List<Account>();
        var a = new Account(2, "干杂活")
        {
            Avatar = "干",
            Proxy = "http://u:p@1.2.3.4:3129",
        };
        a.Tags.Add("电商");
        a.Tags.Add("主力");
        l.Add(a);
        var b = new Account(5, "Muse 😊") { Avatar = "😊" };
        l.Add(b);
        return l;
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var json = AccountJsonCodec.ToJson(Sample());
        var back = AccountJsonCodec.FromJsonOrNull(json)!;
        Assert.Equal(2, back.Count);
        var a = back[0];
        Assert.Equal(2L, a.Id);
        Assert.Equal("干杂活", a.Name);
        Assert.Equal("干", a.Avatar);
        Assert.Equal("http://u:p@1.2.3.4:3129", a.Proxy);
        Assert.Equal(2, a.Tags.Count);
        Assert.Equal("电商", a.Tags[0]);
        Assert.Equal("主力", a.Tags[1]);
        Assert.Equal("😊", back[1].Avatar);
    }

    [Theory]
    [InlineData("[{\"id\":3,\"name\":\"legacy\",\"avatar\":\"旧\"}]")]
    [InlineData("[{\"id\":3,\"name\":\"legacy\",\"avatar\":\"旧\",\"avatarImage\":null}]")]
    [InlineData("[{\"id\":3,\"name\":\"legacy\",\"avatar\":\"旧\",\"avatarImage\":42}]")]
    public void AvatarImage_MissingOrInvalidOptionalField_PreservesAccount(string json)
    {
        var accounts = AccountJsonCodec.FromJsonOrNull(json, out var skipped);
        var account = Assert.Single(accounts!);
        Assert.False(skipped);
        Assert.Equal(3L, account.Id);
        Assert.Equal("legacy", account.Name);
        Assert.Equal("旧", account.Avatar);
        Assert.Equal("", account.AvatarImage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\synthetic avatars\头像 😊\image.png")]
    public void AvatarImage_RoundTrip_PreservesPathAndTextFallback(string path)
    {
        var account = new Account(7, "synthetic") { Avatar = "头像", AvatarImage = path };
        var json = AccountJsonCodec.ToJson(new[] { account });
        var restored = Assert.Single(AccountJsonCodec.FromJsonOrNull(json)!);
        Assert.Equal(path, restored.AvatarImage);
        Assert.Equal(account.Avatar, restored.Avatar);
    }

    [Fact]
    public void NullOrGarbage_ReturnsNull()
    {
        Assert.Null(AccountJsonCodec.FromJsonOrNull(null));
        Assert.Null(AccountJsonCodec.FromJsonOrNull("not json at all"));
        Assert.Null(AccountJsonCodec.FromJsonOrNull("{\"a\":1}"));   // 对象而非数组
        Assert.Null(AccountJsonCodec.FromJsonOrNull("[{broken"));
    }

    [Fact]
    public void EmptyArray_EmptyList()
    {
        var output = AccountJsonCodec.FromJsonOrNull("[]")!;
        Assert.Empty(output);
    }

    /// <summary>单条损坏：跳过该条，其余保留</summary>
    [Fact]
    public void PartialCorruption_SkipsBadEntryOnly()
    {
        var json = "[{\"id\":2,\"name\":\"好账号\"},"
                   + "{\"id\":\"不是数字\",\"name\":\"坏账号\"},"
                   + "{\"id\":5,\"name\":\"另一个好账号\",\"proxy\":\"\",\"tags\":[]}]";
        var output = AccountJsonCodec.FromJsonOrNull(json)!;
        Assert.Equal(2, output.Count);
        Assert.Equal("好账号", output[0].Name);
        Assert.Equal("另一个好账号", output[1].Name);
    }

    [Fact]
    public void MissingOptionalFields_Defaults()
    {
        var output = AccountJsonCodec.FromJsonOrNull("[{\"id\":3,\"name\":\"n\"}]")!;
        Assert.Equal(1, output.Count);
        Assert.Equal("m", output[0].Avatar);
        Assert.Equal("", output[0].Proxy);
        Assert.False(output[0].ForceProxy);
        Assert.Empty(output[0].Tags);
    }

    /// <summary>强制走代理开关需要持久化，否则重启后设置丢失、真实 IP 可能泄露。</summary>
    [Fact]
    public void ForceProxy_RoundTrips()
    {
        var a = new Account(7, "代理号") { Proxy = "http://u:p@1.2.3.4:3129", ForceProxy = true };
        var json = AccountJsonCodec.ToJson(new List<Account> { a })!;
        var back = AccountJsonCodec.FromJsonOrNull(json)![0];
        Assert.True(back.ForceProxy);
        Assert.Equal("http://u:p@1.2.3.4:3129", back.Proxy);
    }

    /// <summary>旧版本 accounts.json 没有 forceProxy 字段，必须能正常读入（向后兼容）。</summary>
    [Fact]
    public void LegacyJsonWithoutForceProxy_StillLoads()
    {
        var back = AccountJsonCodec.FromJsonOrNull(
            "[{\"id\":2,\"name\":\"老账号\",\"avatar\":\"m\",\"proxy\":\"127.0.0.1:7890\",\"tags\":[\"主力\"]}]")!;
        Assert.Single(back);
        Assert.False(back[0].ForceProxy);
        Assert.Equal("老账号", back[0].Name);
        Assert.Equal("主力", back[0].Tags[0]);
    }
}
