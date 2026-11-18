namespace MuseLauncher.Core;

/// <summary>
/// 账号元数据。登录数据由每账号独立的 WebView2 用户数据目录负责持久化。
/// </summary>
public class Account
{
    public Account(long id, string name)
    {
        Id = id;
        Name = name;
    }

    public long Id { get; }
    public string Name { get; set; } = "";

    /// <summary>头像：1-2 个字符或 emoji，默认 "m"</summary>
    public string Avatar { get; set; } = "m";

    /// <summary>可选头像图片的本地路径</summary>
    public string AvatarImage { get; set; } = "";

    /// <summary>代理地址，空 = 跟随系统网络</summary>
    public string Proxy { get; set; } = "";

    /// <summary>取消浏览器的本机地址代理绕过；不是系统级防直连。</summary>
    public bool ForceProxy { get; set; }

    /// <summary>标签（侧栏可按标签搜索）</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>登录态：运行时从 Cookie 探测，不持久化</summary>
    public bool Logged { get; set; }

    public Account Clone() => new(Id, Name)
    {
        Avatar = Avatar,
        AvatarImage = AvatarImage,
        Proxy = Proxy,
        ForceProxy = ForceProxy,
        Tags = new List<string>(Tags),
        Logged = Logged,
    };
}
