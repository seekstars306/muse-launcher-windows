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
    private bool isDarkTheme = true;
    private int activeDownloads;

    public long AccountId { get; }
    public WebView2 Control { get; }
    public bool Loaded { get; private set; }
    public bool Errored { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasNavigated { get; private set; }
    public long? LoadStartAt { get; private set; }
    public string CurrentUrl { get; private set; } = "";
    public bool IsNavigating { get; private set; }
    public bool IsSuspended { get; private set; }
    public long LastActiveAt { get; private set; } = Environment.TickCount64;
    public bool IsBusyInBackground => activeDownloads > 0 || (!disposing && Control.CoreWebView2?.IsDocumentPlayingAudio == true);
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
    public event Action<AccountWebHost, string>? CurrentUrlChanged;

    private AccountWebHost(long accountId, string profileDirectory, WebView2 control)
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
        CancellationToken cancellationToken = default,
        bool isDarkTheme = true,
        string? downloadDirectory = null)
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
            DefaultBackgroundColor = ThemePalette.For(isDarkTheme).WebViewBgColor,
            Visibility = Visibility.Hidden,
        };
        var host = new AccountWebHost(accountId, profile, control) { isDarkTheme = isDarkTheme };
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
            host.ApplyDownloadDirectory(downloadDirectory);
            await host.ApplyThemeAsync(isDarkTheme, syncDom: false);
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
            if (disposing || !IsWebUri(e.Uri)) return;
            // Muse 与第三方登录页在应用内打开；普通外部链接用系统默认浏览器打开，避免离开对话回不来
            if (MuseUrlHelper.IsInAppAuthHost(e.Uri)) core.Navigate(e.Uri);
            else App.TryOpenUrl(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (disposing) { e.Cancel = true; return; }
            if (!MuseUrlHelper.IsAllowedNavigationUri(e.Uri)) { e.Cancel = true; return; }
            HasNavigated = true;
            IsNavigating = true;
            Errored = false;
            ErrorMessage = null;
            LoadStartAt = Environment.TickCount64;
            NavigationStarting?.Invoke(this);
        };
        core.SourceChanged += (_, _) =>
        {
            if (disposing) return;
            var url = core.Source ?? "";
            if (!string.Equals(CurrentUrl, url, StringComparison.Ordinal))
            {
                CurrentUrl = url;
                CurrentUrlChanged?.Invoke(this, url);
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (disposing || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            IsNavigating = false;
            var url = core.Source ?? "";
            if (!string.Equals(CurrentUrl, url, StringComparison.Ordinal))
            {
                CurrentUrl = url;
                CurrentUrlChanged?.Invoke(this, url);
            }
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
            if (success && MuseUrlHelper.IsMusePage(url))
                _ = ApplyThemeAsync(isDarkTheme, syncDom: true);
            NavigationCompleted?.Invoke(this, success, ErrorMessage);
        };
        core.PermissionRequested += (_, e) =>
        {
            if (disposing) return;
            if (e.PermissionKind is CoreWebView2PermissionKind.Microphone
                or CoreWebView2PermissionKind.Camera
                or CoreWebView2PermissionKind.ClipboardRead
                && MuseUrlHelper.IsTrustedWebPermissionOrigin(e.Uri))
            {
                e.State = CoreWebView2PermissionState.Allow;
            }
        };
        core.DownloadStarting += (_, e) =>
        {
            if (disposing || e.DownloadOperation == null) return;
            Interlocked.Increment(ref activeDownloads);
            var op = e.DownloadOperation;
            void OnStateChanged(object? s, object args)
            {
                if (op.State != CoreWebView2DownloadState.InProgress)
                {
                    op.StateChanged -= OnStateChanged;
                    Interlocked.Decrement(ref activeDownloads);
                }
            }
            op.StateChanged += OnStateChanged;
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
        if (disposing) return;
        if (visible)
        {
            ResumeIfSuspended();
            LastActiveAt = Environment.TickCount64;
        }
        else
        {
            LastActiveAt = Environment.TickCount64;
        }
        Control.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    public void TouchActivity() => LastActiveAt = Environment.TickCount64;

    public void ApplyDownloadDirectory(string? directory)
    {
        if (disposing || Control.CoreWebView2 == null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                Control.CoreWebView2.Profile.DefaultDownloadFolderPath = directory;
        }
        catch (Exception ex)
        {
            Log.I("MuseWebview", $"set download dir failed id={AccountId} type={ex.GetType().Name}");
        }
    }

    public async Task ApplyThemeAsync(bool dark, bool syncDom = true)
    {
        isDarkTheme = dark;
        if (disposing || Control.CoreWebView2 == null) return;
        try
        {
            Control.DefaultBackgroundColor = ThemePalette.For(dark).WebViewBgColor;
            var core = Control.CoreWebView2;
            core.Profile.PreferredColorScheme = dark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;

            var existing = await core.CookieManager.GetCookiesAsync("https://muse.ai/");
            string? currentVal = null;
            foreach (var c in existing)
            {
                if (string.Equals(c.Name, ThemeHelper.ThemeCookieName, StringComparison.Ordinal))
                {
                    currentVal = c.Value;
                    if (!string.Equals(c.Domain, ThemeHelper.ThemeCookieDomain, StringComparison.OrdinalIgnoreCase))
                        core.CookieManager.DeleteCookie(c);
                }
            }
            var newVal = ThemeHelper.BuildUpdatedThemeCookieValue(currentVal, dark);
            var cookie = core.CookieManager.CreateCookie(ThemeHelper.ThemeCookieName, newVal, ThemeHelper.ThemeCookieDomain, "/");
            cookie.IsSecure = true;
            cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            cookie.Expires = DateTime.UtcNow.AddYears(1);
            core.CookieManager.AddOrUpdateCookie(cookie);

            if (syncDom && !IsSuspended && MuseUrlHelper.IsMusePage(core.Source))
                await core.ExecuteScriptAsync(ThemeHelper.BuildDomThemeSyncScript(dark));
        }
        catch (Exception ex)
        {
            Log.I("MuseTheme", $"sync failed id={AccountId} type={ex.GetType().Name}");
        }
    }

    public async Task<bool> TrySuspendAsync()
    {
        if (disposing || IsSuspended || IsBusyInBackground || Control.CoreWebView2 == null) return false;
        try
        {
            Control.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            var ok = await Control.CoreWebView2.TrySuspendAsync();
            if (ok)
            {
                IsSuspended = true;
                Log.I("MusePerf", $"suspended background id={AccountId}");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Log.I("MusePerf", $"suspend failed id={AccountId} type={ex.GetType().Name}");
            return false;
        }
    }

    public void ResumeIfSuspended()
    {
        if (disposing || !IsSuspended || Control.CoreWebView2 == null) return;
        try
        {
            Control.CoreWebView2.Resume();
            Control.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            IsSuspended = false;
            Log.I("MusePerf", $"resumed id={AccountId}");
        }
        catch (Exception ex)
        {
            Log.I("MusePerf", $"resume failed id={AccountId} type={ex.GetType().Name}");
        }
    }

    public void Reload()
    {
        ResumeIfSuspended();
        Core.Reload();
    }

    public void Navigate(string url)
    {
        ResumeIfSuspended();
        Core.Navigate(url);
    }

    public bool CanGoBack => !disposing && Control.CoreWebView2?.CanGoBack == true;
    public void GoBack()
    {
        ResumeIfSuspended();
        Core.GoBack();
    }

    public async Task<List<string>> GetMuseCookieNamesAsync()
    {
        var cookies = await Core.CookieManager.GetCookiesAsync("https://muse.ai/");
        return cookies.Select(c => c.Name).ToList();
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
        CurrentUrlChanged = null;
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
