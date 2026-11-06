namespace MuseLauncher.Core;

/// <summary>
/// 命令行调试/自动化参数的解析结果（对应安卓 am start extras，Windows 改为命令行传参）。
/// 这些参数只在 Debug 构建生效（App.xaml.cs 用 #if DEBUG 门控）。
/// 支持 --key=value、--key value、布尔开关裸写（--dump_auth）三种形式。
/// </summary>
public sealed class LaunchCommands
{
    private static readonly HashSet<string> FlagKeys = new()
    {
        "show_drawer", "dump_auth", "add_account", "proxy_autotest", "proxy_save",
    };

    public bool ShowDrawer { get; }
    public long StartAccount { get; }          // -1 = 不切换
    public bool HasFullscreen { get; }         // 是否带了 fullscreen 参数
    public bool Fullscreen { get; }
    public bool HasOpenConfig { get; }         // 是否带了 open_config 参数
    public long OpenConfigId { get; }          // -1 = 当前账号
    public string? ProxyPreset { get; }        // null = 不预填
    public bool ProxyAutoTest { get; }
    public bool ProxySave { get; }
    public string? SearchQuery { get; }        // null = 不设置
    public bool DumpAuth { get; }
    public bool AddAccount { get; }
    public bool HasDelAccount { get; }         // 是否带了 del_account 参数
    public long DelAccountId { get; }          // -1 = 无效

    private LaunchCommands(Dictionary<string, object> e)
    {
        ShowDrawer = Bool(e, "show_drawer");
        StartAccount = Lon(e, "start_account");
        HasFullscreen = e.ContainsKey("fullscreen");
        Fullscreen = Bool(e, "fullscreen");
        HasOpenConfig = e.ContainsKey("open_config");
        OpenConfigId = Lon(e, "open_config");
        ProxyPreset = Str(e, "proxy_preset");
        ProxyAutoTest = Bool(e, "proxy_autotest");
        ProxySave = Bool(e, "proxy_save");
        SearchQuery = Str(e, "search_query");
        DumpAuth = Bool(e, "dump_auth");
        AddAccount = Bool(e, "add_account");
        HasDelAccount = e.ContainsKey("del_account");
        DelAccountId = Lon(e, "del_account");
    }

    /// <summary>release 构建（DEBUG == false）不响应任何调试参数。</summary>
    public static bool Enabled(bool debugBuild) => debugBuild;

    /// <summary>从命令行参数解析（--key=value / --key value / 裸布尔开关）。</summary>
    public static LaunchCommands Parse(IReadOnlyList<string>? args)
    {
        var e = new Dictionary<string, object>();
        if (args != null)
        {
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (a.StartsWith("--")) a = a[2..];
                else if (a.StartsWith('-')) a = a[1..];
                var eq = a.IndexOf('=');
                if (eq >= 0)
                {
                    e[a[..eq].Trim()] = ConvertValue(a[(eq + 1)..].Trim());
                }
                else if (FlagKeys.Contains(a))
                {
                    e[a] = true;
                }
                else if (i + 1 < args.Count)
                {
                    e[a.Trim()] = ConvertValue(args[++i].Trim());
                }
                else
                {
                    e[a] = true;
                }
            }
        }
        return new LaunchCommands(e);
    }

    /// <summary>命令行值只有字符串一种形态，先转成 bool/long 再入库；其余保留字符串。</summary>
    private static object ConvertValue(string s)
    {
        if (bool.TryParse(s, out var b)) return b;
        if (long.TryParse(s, out var l)) return l;
        return s;
    }

    /// <summary>供单元测试：直接从键值表构造（模拟安卓 parse(Map)）。</summary>
    public static LaunchCommands Parse(Dictionary<string, object> extras)
    {
        return new LaunchCommands(extras ?? new Dictionary<string, object>());
    }

    // 类型不匹配时安全回退默认值（对应安卓的 instanceof 检查）。
    // 命令行入口 Parse(string[]) 已把字符串值先转成 bool/long，这里只认确切类型。
    private static bool Bool(Dictionary<string, object> e, string key)
    {
        return e.TryGetValue(key, out var v) && v is bool b && b;
    }

    private static long Lon(Dictionary<string, object> e, string key)
    {
        return e.TryGetValue(key, out var v) && v is long l ? l : -1L;
    }

    private static string? Str(Dictionary<string, object> e, string key)
    {
        return e.TryGetValue(key, out var v) && v is string s ? s : null;
    }
}
