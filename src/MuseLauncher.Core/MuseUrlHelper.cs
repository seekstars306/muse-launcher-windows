namespace MuseLauncher.Core;

/// <summary>
/// Muse 页面 URL 判定、内部协议放行与可信权限来源判断（镜像安卓 MuseUrlHelper）。
/// </summary>
public static class MuseUrlHelper
{
    /// <summary>是否属于 muse.ai 主站或子域页面（http / https）。</summary>
    public static bool IsMusePage(string? url)
    {
        var host = ExtractHttpHost(url);
        return host != null && IsHostOrSubdomain(host, "muse.ai");
    }

    /// <summary>
    /// 是否已跳转到非 muse.ai 的外部网页（需要显示「返回 Muse」导航栏）。
    /// 空串、about:blank、blob/data 等非 http(s) 协议不视为外部网页。
    /// </summary>
    public static bool IsExternalPage(string? url)
    {
        var host = ExtractHttpHost(url);
        return host != null && !IsHostOrSubdomain(host, "muse.ai");
    }

    /// <summary>
    /// 常见第三方登录 / 认证域名，点击弹窗时留在 WebView2 内完成登录而不是跳到外部浏览器。
    /// </summary>
    public static bool IsInAppAuthHost(string? url)
    {
        var host = ExtractHttpHost(url);
        if (host == null) return false;
        return IsHostOrSubdomain(host, "muse.ai")
            || IsHostOrSubdomain(host, "meta.ai")
            || IsHostOrSubdomain(host, "meta.com")
            || IsHostOrSubdomain(host, "facebook.com")
            || IsHostOrSubdomain(host, "instagram.com")
            || IsHostOrSubdomain(host, "accounts.google.com")
            || IsHostOrSubdomain(host, "appleid.apple.com");
    }

    /// <summary>
    /// WebView2 权限请求（麦克风 / 摄像头）可自动信任的 Muse 相关来源。
    /// </summary>
    public static bool IsTrustedWebPermissionOrigin(string? origin)
    {
        var host = ExtractHttpHost(origin);
        if (host == null) return false;
        return IsHostOrSubdomain(host, "muse.ai")
            || IsHostOrSubdomain(host, "meta.ai")
            || IsHostOrSubdomain(host, "metaaiusercontent.com")
            || IsHostOrSubdomain(host, "ecto1usercontent.com");
    }

    /// <summary>WebView2 导航允许的协议（放行 http/https/about/blob/data/javascript，兼容空间与预览）。</summary>
    public static bool IsAllowedNavigationUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.ToLowerInvariant() is "http" or "https" or "about" or "blob" or "data" or "javascript";
    }

    /// <summary>提取用于顶部外链栏展示的简洁主机名。</summary>
    public static string DisplayHost(string? url)
    {
        var host = ExtractHttpHost(url);
        return host ?? (url?.Trim() ?? "");
    }

    internal static string? ExtractHttpHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        return string.IsNullOrEmpty(host) ? null : host;
    }

    private static bool IsHostOrSubdomain(string host, string domain)
        => host.Equals(domain, StringComparison.Ordinal)
        || host.EndsWith("." + domain, StringComparison.Ordinal);
}
