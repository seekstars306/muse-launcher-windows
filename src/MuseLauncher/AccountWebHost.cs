using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>每账号独立的浏览器和本机代理桥接。初始化不导航，由调用方注册事件后开始加载。</summary>
public sealed class AccountWebHost : IAsyncDisposable
{
    public const string StartUrl = "https://muse.ai/";
    public const string AuthCookieName = "hatch_sess";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, Task> PendingProfileExits =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TaskCompletionSource<bool> browserExited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string profileDirectory;
    private CoreWebView2Environment? environment;
    private LoopbackProxyBridge? proxyBridge;
    private Task? disposeTask;
    private int? browserProcessId;
    private bool disposing;
    private bool failureReported;

    public long AccountId { get; }
    public WebView2 Control { get; }
    public bool Loaded { get; private set; }
    public bool Errored { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasNavigated { get; private set; }
    public long? LoadStartAt { get; private set; }
    public bool IsDisposed => disposing;

    public CoreWebView2 Core
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            return Control.CoreWebView2 ?? throw new InvalidOperationException("浏览器尚未初始化");
        }
    }

    public event Action<AccountWebHost>? NavigationStarting;
    public event Action<AccountWebHost, bool, string?>? NavigationCompleted;
    public event Action<AccountWebHost>? RendererFailed;

    internal AccountWebHost(long accountId, string profileDirectory, WebView2 control)
    {
        AccountId = accountId;
        this.profileDirectory = profileDirectory;
        Control = control;
    }

    public static string ProfilesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuseLauncher", "Profiles");

    public static string ProfileDir(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return Path.Combine(ProfilesRoot, "acct_" + id);
    }

    public static async Task<AccountWebHost> CreateAsync(
        long accountId,
        BrowserSessionConfiguration configuration,
        Action<WebView2> attachControl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proxy = ProxyConfiguration.Parse(configuration.Proxy);
        if (configuration.ForceProxy && proxy == null)
            throw new FormatException("启用代理绕过控制时必须填写有效代理，不能回退为系统网络");

        var profile = Path.GetFullPath(configuration.ProfileDirectory ?? ProfileDir(accountId));
        if (PendingProfileExits.TryGetValue(profile, out var pendingExit))
        {
            try { await pendingExit.WaitAsync(ExitTimeout, cancellationToken); }
            catch (TimeoutException)
            {
                throw new IOException("旧浏览器仍在使用该账号目录。请稍后重试，或关闭其他使用此目录的启动器。");
            }
            PendingProfileExits.TryRemove(profile, out _);
        }

        var control = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x12, 0x13, 0x16),
            Visibility = Visibility.Hidden,
        };
        var host = new AccountWebHost(accountId, profile, control);
        try
        {
            if (proxy != null)
                host.proxyBridge = await LoopbackProxyBridge.StartAsync(proxy, cancellationToken);

            var arguments = new List<string>();
            if (host.proxyBridge != null)
                arguments.Add("--proxy-server=" + host.proxyBridge.Address.GetLeftPart(UriPartial.Authority));
            if (configuration.ForceProxy)
                arguments.Add("--proxy-bypass-list=<-loopback>");

            var options = new CoreWebView2EnvironmentOptions(string.Join(" ", arguments))
            {
                ExclusiveUserDataFolderAccess = true,
            };
            host.environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            host.environment.BrowserProcessExited += host.OnBrowserProcessExited;
            cancellationToken.ThrowIfCancellationRequested();

            attachControl(control);
            // WebView2 的初始化不能真正取消；始终等待结束后清理，避免同一目录的初始化重叠。
            await control.EnsureCoreWebView2Async(host.environment);
            host.browserProcessId = (int)control.CoreWebView2.BrowserProcessId;
            cancellationToken.ThrowIfCancellationRequested();
            host.AttachEvents();
            Log.I("MuseWebview", $"created id={accountId} proxy={(proxy == null ? "system" : proxy.DisplayAddress)}");
            return host;
        }
        catch
        {
            if (host.browserProcessId == null && control.CoreWebView2 != null)
            {
                try { host.browserProcessId = (int)control.CoreWebView2.BrowserProcessId; } catch { }
            }
            await host.DisposeAsync();
            throw;
        }
    }

    private void AttachEvents()
    {
        var core = Core;
        core.BasicAuthenticationRequested += (_, e) =>
        {
            if (disposing || proxyBridge == null) return;
            // 只给本机桥接完整 origin 的固定 realm 提供随机本地凭证，绝不向 WebView2 提交上游密码。
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var challenge)
                || challenge.Scheme != proxyBridge.Address.Scheme
                || !string.Equals(challenge.Host, proxyBridge.Address.Host, StringComparison.OrdinalIgnoreCase)
                || challenge.Port != proxyBridge.Address.Port
                || !string.Equals(e.Challenge.Trim(),
                    "Basic realm=\"" + LoopbackProxyBridge.AuthenticationRealm + "\"", StringComparison.OrdinalIgnoreCase))
                return;

            e.Response.UserName = proxyBridge.Username;
            e.Response.Password = proxyBridge.Password;
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!disposing && IsWebUri(e.Uri)) core.Navigate(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (disposing) { e.Cancel = true; return; }
            if (!IsWebUri(e.Uri) && e.Uri != "about:blank") { e.Cancel = true; return; }
            HasNavigated = true;
            Errored = false;
            ErrorMessage = null;
            LoadStartAt = Environment.TickCount64;
            NavigationStarting?.Invoke(this);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (disposing || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            var success = e.IsSuccess && e.HttpStatusCode < 400;
            Errored = !success;
            Loaded |= success;
            ErrorMessage = success ? null : e.IsSuccess
                ? "HTTP " + e.HttpStatusCode + "（检查代理认证或目标站点响应）"
                : DescribeError(e.WebErrorStatus);
            if (LoadStartAt is long started)
            {
                Log.I("MusePerf", $"load end id={AccountId} cost={Environment.TickCount64 - started}ms errored={!success}");
                LoadStartAt = null;
            }
            NavigationCompleted?.Invoke(this, success, ErrorMessage);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
            {
                Log.I("MuseWebview", $"process failed id={AccountId} kind={e.ProcessFailedKind}");
                ReportFailure();
            }
        };
        core.Settings.AreDefaultContextMenusEnabled = true;
    }

    private void ReportFailure()
    {
        if (disposing || failureReported) return;
        failureReported = true;
        // 不在 WebView2 的同步回调中销毁控件或创建新窗口。
        Control.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!disposing) RendererFailed?.Invoke(this);
        }));
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        if (browserProcessId is int expected && e.BrowserProcessId != expected) return;
        browserExited.TrySetResult(true);
        if (environment != null) environment.BrowserProcessExited -= OnBrowserProcessExited;
        if (!disposing) ReportFailure();
    }

    private static bool IsWebUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string DescribeError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => "证书域名不匹配",
        CoreWebView2WebErrorStatus.CertificateExpired => "证书已过期",
        CoreWebView2WebErrorStatus.CertificateIsInvalid => "证书无效",
        CoreWebView2WebErrorStatus.ServerUnreachable => "服务器无法访问",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中止",
        CoreWebView2WebErrorStatus.ConnectionReset => "连接被重置",
        CoreWebView2WebErrorStatus.CannotConnect => "无法建立连接（检查代理地址与协议）",
        CoreWebView2WebErrorStatus.HostNameNotResolved => "无法解析主机名",
        CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired => "本机代理认证失败",
        CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired => "目标站点需要身份认证",
        _ => status.ToString(),
    };

    public void SetVisible(bool visible)
    {
        if (!disposing) Control.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    public void Reload() => Core.Reload();
    public void Navigate(string url) => Core.Navigate(url);
    public bool CanGoBack => !disposing && Control.CoreWebView2?.CanGoBack == true;
    public void GoBack() => Core.GoBack();

    /// <summary>导出整个 profile 中尚有效的 muse.ai Cookie；不导航，也不记录 Cookie 内容。</summary>
    public async Task<List<CredentialCookie>> ExportMuseCookiesAsync()
    {
        try
        {
            var cookies = await Core.CookieManager.GetCookiesAsync(null);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            return cookies
                .Where(cookie => IsMuseCookieDomain(cookie.Domain))
                .Select(ToCredentialCookie)
                .Where(cookie => cookie.IsSession || cookie.ExpiresUnixSeconds > now)
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw new InvalidDataException("Cookie 导出失败");
        }
    }

    /// <summary>
    /// 使用归档的同一套规则校验后写入 Cookie，并从整个 profile 回读核对；不触发导航。
    /// 新建 Cookie 默认为 session；仅持久 Cookie 设置 Expires，保留浏览器报告的域表示。
    /// </summary>
    public async Task<bool> RestoreMuseCookiesAsync(IReadOnlyList<CredentialCookie> cookies)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(cookies);
            var sources = cookies.ToList();
            CredentialArchive.Validate(new CredentialBundle
            {
                Accounts = new() { new CredentialAccount { CookiesCaptured = true, Cookies = sources } },
            });

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            sources = sources.Where(cookie => cookie.IsSession || cookie.ExpiresUnixSeconds > now).ToList();
            if (!sources.Any(cookie => string.Equals(cookie.Name, AuthCookieName, StringComparison.Ordinal)))
                return false;

            var manager = Core.CookieManager;
            var validated = new List<(CredentialCookie Source, CoreWebView2Cookie Cookie)>();
            foreach (var source in sources)
            {
                var cookie = manager.CreateCookie(source.Name, source.Value, source.Domain, source.Path);
                cookie.IsSecure = source.IsSecure;
                cookie.IsHttpOnly = source.IsHttpOnly;
                cookie.SameSite = ParseSameSite(source.SameSite);
                if (!source.IsSession)
                    cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds(
                        (long)Math.Round(source.ExpiresUnixSeconds!.Value * 1000d)).UtcDateTime;
                validated.Add((source, cookie));
            }

            foreach (var item in validated) manager.AddOrUpdateCookie(item.Cookie);
            var stored = await manager.GetCookiesAsync(null);
            foreach (var item in validated)
            {
                var actual = stored.FirstOrDefault(cookie => CookieIdentityEquals(cookie, item.Source));
                if (actual == null)
                {
                    // Cookie may expire between validation and the asynchronous readback.
                    if (!item.Source.IsSession && item.Source.ExpiresUnixSeconds
                        <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d) continue;
                    if (item.Source.Name == AuthCookieName) continue;
                    throw new InvalidDataException("Cookie 恢复校验失败");
                }
                if (!CookieAttributesEqual(actual, item.Source))
                    throw new InvalidDataException("Cookie 恢复校验失败");
            }

            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            return sources.Where(source => source.Name == AuthCookieName)
                .Any(source => stored.Any(cookie => CookieIdentityEquals(cookie, source)
                    && CookieAttributesEqual(cookie, source)
                    && (cookie.IsSession || ToCredentialCookie(cookie).ExpiresUnixSeconds > now)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // COM and validation exceptions can contain Cookie values; never retain an inner exception.
            throw new InvalidDataException("Cookie 恢复失败");
        }
    }

    public async Task<List<string>> GetMuseCookieNamesAsync()
    {
        var cookies = await Core.CookieManager.GetCookiesAsync("https://muse.ai/");
        return cookies.Select(c => c.Name).ToList();
    }

    private static CredentialCookie ToCredentialCookie(CoreWebView2Cookie cookie)
    {
        var session = cookie.IsSession;
        double? expiry = session
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds() / 1000d;
        return new CredentialCookie
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            IsSecure = cookie.IsSecure,
            IsHttpOnly = cookie.IsHttpOnly,
            IsSession = session,
            SameSite = cookie.SameSite.ToString(),
            ExpiresUnixSeconds = expiry,
        };
    }

    private static CoreWebView2CookieSameSiteKind ParseSameSite(string sameSite)
    {
        if (!Enum.TryParse<CoreWebView2CookieSameSiteKind>(sameSite, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed) || parsed.ToString() != sameSite)
            throw new InvalidDataException("Cookie 数据无效");
        return parsed;
    }

    private static bool CookieIdentityEquals(CoreWebView2Cookie actual, CredentialCookie expected)
        => string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
        && string.Equals(actual.Domain, expected.Domain, StringComparison.OrdinalIgnoreCase)
        && string.Equals(actual.Path, expected.Path, StringComparison.Ordinal);

    private static bool CookieAttributesEqual(CoreWebView2Cookie actual, CredentialCookie expected)
    {
        var actualExpiry = actual.IsSession
            ? (double?)null
            : new DateTimeOffset(DateTime.SpecifyKind(actual.Expires, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds() / 1000d;
        return actual.Value == expected.Value
            && actual.IsSecure == expected.IsSecure
            && actual.IsHttpOnly == expected.IsHttpOnly
            && actual.IsSession == expected.IsSession
            && actual.SameSite.ToString() == expected.SameSite
            && ((actualExpiry is null && expected.ExpiresUnixSeconds is null)
                || (actualExpiry is double actualSeconds && expected.ExpiresUnixSeconds is double expectedSeconds
                    && Math.Abs(actualSeconds - expectedSeconds) < 0.001));
    }

    private static bool IsMuseCookieDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        var value = domain.StartsWith('.') ? domain[1..] : domain;
        return value.Equals("muse.ai", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".muse.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>诊断只输出 localStorage 键和 cookie 名，不记录 cookie 值。</summary>
    public Task<string> EvalAuthStateAsync() => Core.ExecuteScriptAsync(
        "(function(){try{return JSON.stringify({storageKeys:Object.keys(localStorage),"
        + "cookieNames:document.cookie.split(';').filter(Boolean).map(x=>x.trim().split('=')[0])})}"
        + "catch(e){return 'unavailable'}})()");

    public ValueTask DisposeAsync()
    {
        Control.Dispatcher.VerifyAccess();
        disposeTask ??= DisposeCoreAsync();
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        disposing = true;
        NavigationStarting = null;
        NavigationCompleted = null;
        RendererFailed = null;
        if (browserProcessId != null)
            PendingProfileExits[profileDirectory] = browserExited.Task;

        Exception? failure = null;
        try
        {
            if (Control.Parent is System.Windows.Controls.Panel panel) panel.Children.Remove(Control);
            Control.Dispose();
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            if (proxyBridge != null) await proxyBridge.DisposeAsync();
        }
        catch (Exception ex) { failure ??= ex; }

        if (browserProcessId != null)
        {
            try
            {
                await browserExited.Task.WaitAsync(ExitTimeout);
                PendingProfileExits.TryRemove(profileDirectory, out _);
            }
            catch (TimeoutException)
            {
                failure ??= new IOException("等待旧浏览器退出超时。账号数据未删除，请关闭其他使用此目录的窗口后重试。");
            }
        }
        else if (environment != null)
        {
            environment.BrowserProcessExited -= OnBrowserProcessExited;
        }
        if (failure != null) throw failure;
    }
}
