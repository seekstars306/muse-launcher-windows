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
        Assert.Empty(output[0].Tags);
    }
}
