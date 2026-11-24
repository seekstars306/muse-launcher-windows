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

    /// <summary>release 构建（DEBUG == false）不响应调试/破坏性参数。</summary>
    public static bool Enabled(bool debugBuild) => debugBuild;

    /// <summary>
    /// Release 构建安全允许的只读导航参数（供账号桌面快捷方式使用）：
    /// 仅接受 --start_account / --account（正数 ID）和 --fullscreen（true/false），忽略一切修改或删除参数。
    /// </summary>
    public static LaunchCommands ParseSafeNavigation(IReadOnlyList<string>? args)
    {
        var values = new Dictionary<string, object>();
        if (args == null) return new LaunchCommands(values);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.IsNullOrEmpty(argument) || !argument.StartsWith('-')) continue;
            var option = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : argument[1..];
            var equals = option.IndexOf('=');
            var key = equals < 0 ? option : option[..equals];
            if (key == "account") key = "start_account";
            if (key is not ("start_account" or "fullscreen")) continue;
            string? value = equals < 0 ? null : option[(equals + 1)..];
            if (value == null && index + 1 < args.Count && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                var next = args[index + 1];
                if (key == "start_account" || bool.TryParse(next, out _)) { value = next; index++; }
            }
            if (key == "fullscreen")
                values[key] = value == null || bool.TryParse(value, out var flag) && flag;
            else if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                values[key] = id;
        }
        return new LaunchCommands(values);
    }

    /// <summary>命令按键的类型解析，文本值不自动变成布尔或数字，也不吞下一个选项。</summary>
    public static LaunchCommands Parse(IReadOnlyList<string>? args)
    {
        var values = new Dictionary<string, object>();
        var booleans = new HashSet<string>(FlagKeys) { "fullscreen" };
        var identifiers = new HashSet<string> { "start_account", "open_config", "del_account" };
        var strings = new HashSet<string> { "search_query", "proxy_preset" };
        if (args == null) return new LaunchCommands(values);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.IsNullOrEmpty(argument) || !argument.StartsWith('-')) continue;
            var option = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : argument[1..];
            var equals = option.IndexOf('=');
            var key = equals < 0 ? option : option[..equals];
            if (!booleans.Contains(key) && !identifiers.Contains(key) && !strings.Contains(key)) continue;
            string? value = equals < 0 ? null : option[(equals + 1)..];
            if (value == null && index + 1 < args.Count && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                var next = args[index + 1];
                if (!booleans.Contains(key) || bool.TryParse(next, out _)) { value = next; index++; }
            }
            if (booleans.Contains(key))
                values[key] = value == null || bool.TryParse(value, out var flag) && flag;
            else if (identifiers.Contains(key))
                values[key] = long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 ? id : -1L;
            else if (value != null) values[key] = value;
        }
        return new LaunchCommands(values);
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
