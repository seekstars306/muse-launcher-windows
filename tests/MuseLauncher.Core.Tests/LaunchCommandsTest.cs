using MuseLauncher.Core;
using Xunit;

/** 调试参数解析与门控契约——移植自安卓 LaunchCommandsTest，另加命令行形式用例 */
namespace MuseLauncher.Core.Tests;

public class LaunchCommandsTest
{
    [Fact]
    public void EmptyExtras_AllDefaults()
    {
        var c = LaunchCommands.Parse(new Dictionary<string, object>());
        Assert.False(c.ShowDrawer);
        Assert.Equal(-1L, c.StartAccount);
        Assert.False(c.HasFullscreen);
        Assert.False(c.Fullscreen);
        Assert.False(c.HasOpenConfig);
        Assert.Equal(-1L, c.OpenConfigId);
        Assert.Null(c.ProxyPreset);
        Assert.False(c.ProxyAutoTest);
        Assert.False(c.ProxySave);
        Assert.Null(c.SearchQuery);
        Assert.False(c.DumpAuth);
        Assert.False(c.AddAccount);
        Assert.False(c.HasDelAccount);
        Assert.Equal(-1L, c.DelAccountId);
    }

    [Fact]
    public void NullExtras_AllDefaults()
    {
        var c = LaunchCommands.Parse((Dictionary<string, object>?)null!);
        Assert.False(c.ShowDrawer);
        Assert.Equal(-1L, c.StartAccount);
    }

    [Fact]
    public void FullExtras_ParsedCorrectly()
    {
        var e = new Dictionary<string, object>
        {
            ["show_drawer"] = true,
            ["start_account"] = 5L,
            ["fullscreen"] = true,
            ["open_config"] = 8L,
            ["proxy_preset"] = "http://u:p@1.2.3.4:3129",
            ["proxy_autotest"] = true,
            ["proxy_save"] = true,
            ["search_query"] = "Muse",
            ["dump_auth"] = true,
            ["add_account"] = true,
            ["del_account"] = 9L,
        };

        var c = LaunchCommands.Parse(e);
        Assert.True(c.ShowDrawer);
        Assert.Equal(5L, c.StartAccount);
        Assert.True(c.HasFullscreen);
        Assert.True(c.Fullscreen);
        Assert.True(c.HasOpenConfig);
        Assert.Equal(8L, c.OpenConfigId);
        Assert.Equal("http://u:p@1.2.3.4:3129", c.ProxyPreset);
        Assert.True(c.ProxyAutoTest);
        Assert.True(c.ProxySave);
        Assert.Equal("Muse", c.SearchQuery);
        Assert.True(c.DumpAuth);
        Assert.True(c.AddAccount);
        Assert.True(c.HasDelAccount);
        Assert.Equal(9L, c.DelAccountId);
    }

    /// <summary>类型不匹配时必须安全回退到默认值而不是崩溃（对应安卓 am start 类型错配）</summary>
    [Fact]
    public void WrongTypedExtras_SafeDefaults()
    {
        var e = new Dictionary<string, object>
        {
            ["show_drawer"] = "true",      // 字符串冒充布尔
            ["start_account"] = "5",       // 字符串冒充 long
            ["proxy_preset"] = 123L,       // long 冒充字符串
            ["fullscreen"] = 1,            // int 冒充布尔
        };

        var c = LaunchCommands.Parse(e);
        Assert.False(c.ShowDrawer);
        Assert.Equal(-1L, c.StartAccount);
        Assert.Null(c.ProxyPreset);
        Assert.False(c.Fullscreen);
        // has* 语义保留：键存在即视为"带了参数"，但值取默认
        Assert.True(c.HasFullscreen);
    }

    [Fact]
    public void FalseBoolean_ExtraRecognizedButInactive()
    {
        var e = new Dictionary<string, object>
        {
            ["show_drawer"] = false,
            ["fullscreen"] = false,
        };
        var c = LaunchCommands.Parse(e);
        Assert.False(c.ShowDrawer);
        Assert.True(c.HasFullscreen);
        Assert.False(c.Fullscreen);
    }

    /// <summary>M1 门控契约：release 构建（debugBuild=false）必须禁用全部调试参数</summary>
    [Fact]
    public void Gate_DisabledOnReleaseBuild()
    {
        Assert.False(LaunchCommands.Enabled(false));
        Assert.True(LaunchCommands.Enabled(true));
    }

    // ---------- Windows 侧：命令行形式（字符串先转类型再走同一套解析） ----------

    [Fact]
    public void CliStyle_KeyEqualsValue_ParsedCorrectly()
    {
        var c = LaunchCommands.Parse(new[]
        {
            "--show_drawer", "--start_account=5", "--fullscreen=true",
            "--open_config=8", "--proxy_preset=http://u:p@1.2.3.4:3129",
            "--search_query=Muse", "--dump_auth", "--add_account", "--del_account=9",
        });
        Assert.True(c.ShowDrawer);
        Assert.Equal(5L, c.StartAccount);
        Assert.True(c.HasFullscreen);
        Assert.True(c.Fullscreen);
        Assert.True(c.HasOpenConfig);
        Assert.Equal(8L, c.OpenConfigId);
        Assert.Equal("http://u:p@1.2.3.4:3129", c.ProxyPreset);
        Assert.Equal("Muse", c.SearchQuery);
        Assert.True(c.DumpAuth);
        Assert.True(c.AddAccount);
        Assert.True(c.HasDelAccount);
        Assert.Equal(9L, c.DelAccountId);
    }

    [Fact]
    public void CliStyle_SpaceSeparatedValues_ParsedCorrectly()
    {
        var c = LaunchCommands.Parse(new[]
        {
            "--start_account", "7", "--fullscreen", "false", "--search_query", "干杂活",
        });
        Assert.Equal(7L, c.StartAccount);
        Assert.True(c.HasFullscreen);
        Assert.False(c.Fullscreen);
        Assert.Equal("干杂活", c.SearchQuery);
    }

    [Fact]
    public void CliStyle_BareOpenConfig_MeansCurrentAccount()
    {
        var c = LaunchCommands.Parse(new[] { "--open_config" });
        Assert.True(c.HasOpenConfig);
        Assert.Equal(-1L, c.OpenConfigId);
    }

    [Fact]
    public void CliStyle_GarbageValues_SafeDefaults()
    {
        var c = LaunchCommands.Parse(new[] { "--start_account=abc", "--del_account=x" });
        Assert.Equal(-1L, c.StartAccount);
        // 键存在但值非法：has* 语义保留，值取默认
        Assert.True(c.HasDelAccount);
        Assert.Equal(-1L, c.DelAccountId);
    }
}
