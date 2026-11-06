using System.Net;
using System.Net.Sockets;

namespace MuseLauncher.Core;

/// <summary>代理地址工具（镜像安卓 ProxyHelper / AccountConfigActivity 的纯函数部分）。</summary>
public static class ProxyUtil
{
    /// <summary>Clash 默认端口：7890 混合(HTTP+SOCKS) / 7891 纯 HTTP / 7892 纯 SOCKS</summary>
    public static readonly int[] ClashPorts = { 7890, 7891, 7892 };

    /// <summary>统一代理地址格式：补全 scheme（默认 http）。</summary>
    public static string Normalize(string p)
    {
        var lower = p.ToLowerInvariant();
        if (lower.StartsWith("http://") || lower.StartsWith("https://")
            || lower.StartsWith("socks5://") || lower.StartsWith("socks4://"))
        {
            return p;
        }
        return "http://" + p;
    }

    /// <summary>从代理地址解析 [user, pass]，无凭证返回 null。</summary>
    public static string[]? ParseProxyCreds(string p)
    {
        var at = p.IndexOf('@');
        if (at < 0) return null;
        var head = p.Substring(0, at);
        var si = head.IndexOf("://", StringComparison.Ordinal);
        var creds = si >= 0 ? head[(si + 3)..] : head;
        if (creds.Length == 0) return null;
        var c = creds.IndexOf(':');
        if (c < 0) return new[] { creds, "" };
        return new[] { creds[..c], creds[(c + 1)..] };
    }

    /// <summary>7892 是纯 SOCKS，其余按 HTTP 混合端口处理。</summary>
    public static string ProxyUrlForPort(int port)
    {
        return (port == 7892 ? "socks5://" : "http://") + "127.0.0.1:" + port;
    }

    /// <summary>探测本机 Clash 默认端口，返回首个在监听的端口，未找到返回 0。</summary>
    public static int ProbeLocalProxy()
    {
        foreach (var p in ClashPorts)
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var ar = s.BeginConnect(new IPEndPoint(IPAddress.Loopback, p), null, null);
                if (ar.AsyncWaitHandle.WaitOne(400) && s.Connected) return p;
            }
            catch
            {
                // 端口不通，尝试下一个
            }
        }
        return 0;
    }

    /// <summary>
    /// WebView2 环境的代理启动参数。空代理返回空列表（跟随系统网络）。
    /// 注意 Chromium 代理参数不支持 URL 内嵌账密，如需鉴权代理请配合本地转发或系统代理。
    /// </summary>
    public static List<string> BrowserArgsFor(string? proxy)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            args.Add("--proxy-server=" + Normalize(proxy.Trim()));
        }
        return args;
    }
}
