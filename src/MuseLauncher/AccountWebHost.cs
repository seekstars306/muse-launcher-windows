using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>
/// 每个账号一个独立的 WebView2 实例（对应安卓的每账号 WebView + Profile）。
/// 隔离方式：每账号独立 UserDataFolder + 独立浏览器进程，登录数据落盘各自目录；
/// 代理通过环境创建参数 --proxy-server 实现，真正按账号生效。
/// </summary>
public sealed class AccountWebHost : IDisposable
{
    public const string StartUrl = "https://muse.ai/";

    /// <summary>muse.ai 登录会话 cookie（httpOnly）。匿名访问只会落下流程 cookie。</summary>
    public const string AuthCookieName = "hatch_sess";

    public long AccountId { get; }
    public WebView2 Control { get; }
    public CoreWebView2 Core => Control.CoreWebView2;

    public bool Loaded { get; set; }
    public bool Errored { get; set; }
    public long? LoadStartAt { get; set; }

    /// <summary>主框架导航开始（清错误标记的时机）</summary>
    public event Action<AccountWebHost>? NavigationStarting;

    /// <summary>主框架导航完成：isSuccess / 错误描述（null=无）</summary>
    public event Action<AccountWebHost, bool, string?>? NavigationCompleted;

    /// <summary>渲染进程崩溃/无响应（调用方负责销毁重建）</summary>
    public event Action<AccountWebHost>? RendererFailed;

    private AccountWebHost(long accountId, WebView2 control)
    {
        AccountId = accountId;
        Control = control;
    }

    /// <summary>账号的 WebView2 用户数据目录（对应安卓 profile 名 acct_N）。</summary>
    public static string ProfileDir(long id)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuseLauncher", "Profiles", "acct_" + id);
    }

    /// <summary>
    /// 创建账号环境并加载 muse.ai。attachControl 先把控件挂进可视化树再异步初始化，
    /// 确保底层 HWND 有父窗口；失败时抛异常并自动把控件从树上摘除。
    /// </summary>
    public static async Task<AccountWebHost> CreateAsync(Account a, Action<WebView2> attachControl)
    {
        var wv = new WebView2
        {
            // 注意：WPF 控件的 DefaultBackgroundColor 是 System.Drawing.Color
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x12, 0x13, 0x16),
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = ProfileDir(a.Id),
                // 代理是浏览器进程级参数；每账号独立 UserDataFolder = 独立进程 = 代理真正按账号隔离
                AdditionalBrowserArguments = string.Join(" ", ProxyUtil.BrowserArgsFor(a.Proxy)),
            },
        };

        attachControl(wv);
        try
        {
            await wv.EnsureCoreWebView2Async();
        }
        catch
        {
            if (wv.Parent is System.Windows.Controls.Panel p) p.Children.Remove(wv);
            throw;
        }

        var host = new AccountWebHost(a.Id, wv);
        var core = wv.CoreWebView2;

        // 窗口级弹窗（target=_blank）与安卓 setSupportMultipleWindows(false) 行为对齐：
        // 不开新窗口，改为当前视图内导航
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
        };

        // 主框架导航事件（CoreWebView2 级事件只针对主框架，天然等价 isForMainFrame）
        core.NavigationStarting += (_, _) => host.NavigationStarting?.Invoke(host);

        core.NavigationCompleted += (_, e) =>
        {
            string? errText = null;
            if (!e.IsSuccess) errText = DescribeError(e.WebErrorStatus);
            host.NavigationCompleted?.Invoke(host, e.IsSuccess, errText);
        };

        // 渲染进程崩溃：重建该账号 WebView（Profile 数据不受影响，登录态保留）
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
            {
                Log.I("MuseWebview", $"renderer failed id={a.Id} kind={e.ProcessFailedKind}");
                host.RendererFailed?.Invoke(host);
            }
        };

        // 非网页协议交给系统打开（mailto: 等），镜像安卓 shouldOverrideUrlLoading
        core.WebResourceRequested += (_, _) => { }; // 占位：无需拦截子资源

        // muse.ai 需要 JS/存储（默认已开），保持默认下载对话框与文件选择器
        wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

        wv.Source = new Uri(StartUrl);
        return host;
    }

    /// <summary>WebErrorStatus → 人类可读描述（镜像安卓 error.getDescription）。</summary>
    private static string DescribeError(CoreWebView2WebErrorStatus status)
    {
        return status switch
        {
            CoreWebView2WebErrorStatus.Unknown => "未知错误",
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => "证书域名不匹配",
            CoreWebView2WebErrorStatus.CertificateExpired => "证书已过期",
            CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => "客户端证书错误",
            CoreWebView2WebErrorStatus.CertificateRevoked => "证书已被吊销",
            CoreWebView2WebErrorStatus.CertificateIsInvalid => "证书无效",
            CoreWebView2WebErrorStatus.ServerUnreachable => "服务器无法访问",
            CoreWebView2WebErrorStatus.Timeout => "连接超时",
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "服务器响应无效",
            CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中止",
            CoreWebView2WebErrorStatus.ConnectionReset => "连接被重置",
            CoreWebView2WebErrorStatus.Disconnected => "连接已断开",
            CoreWebView2WebErrorStatus.CannotConnect => "无法建立连接",
            CoreWebView2WebErrorStatus.HostNameNotResolved => "无法解析主机名（检查网络或代理）",
            CoreWebView2WebErrorStatus.OperationCanceled => "操作已取消",
            CoreWebView2WebErrorStatus.RedirectFailed => "重定向失败",
            CoreWebView2WebErrorStatus.UnexpectedError => "意外错误",
            CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired => "需要有效的身份凭证（检查代理账密）",
            CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired => "需要有效的代理凭证",
            _ => status.ToString(),
        };
    }

    public void SetVisible(bool visible)
    {
        Control.Visibility = visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public void Reload() => Core.Reload();

    public void Navigate(string url) => Core.Navigate(url);

    public bool CanGoBack => Core.CanGoBack;

    public void GoBack() => Core.GoBack();

    /// <summary>读取 muse.ai 的 cookie 名列表（用于登录态探测）。</summary>
    public async Task<List<string>> GetMuseCookieNamesAsync()
    {
        var cookies = await Core.CookieManager.GetCookiesAsync("https://muse.ai/");
        return cookies.Select(c => c.Name).ToList();
    }

    /// <summary>执行 JS 取 localStorage 键与 document.cookie（调试 dump_auth 用）。</summary>
    public async Task<string> EvalAuthStateAsync()
    {
        return await Core.ExecuteScriptAsync(
            "(function(){try{return 'LS:'+(Object.keys(localStorage).join(',')||'-')"
            + "+'|CK:'+document.cookie}catch(e){return 'ERR:'+e}})()");
    }

    public void Dispose()
    {
        try
        {
            Control.Dispose();
        }
        catch (Exception ex)
        {
            Log.I("MuseWebview", $"dispose error id={AccountId}: {ex.Message}");
        }
    }
}
