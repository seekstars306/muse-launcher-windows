using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>
/// 主窗口：左侧窄条 + 抽屉账号列表 + 主区网页容器。
/// 行为与安卓版 MainActivity 对齐（LRU 常驻、错误遮罩重试、splash 过渡、登录态探测等）。
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxAliveWebviews = 4;      // LRU：最多同时保留的账号页面数（含当前）
    private const double MinSplashMs = 500;
    private const double SplashTimeoutMs = 8000;
    private const double SplashLoadedHideMs = 450;

    private readonly AccountsStore store;
    private readonly LruAccountRegistry<AccountWebHost> webViews = new(MaxAliveWebviews);
    private readonly Dictionary<long, bool> webLoaded = new();
    private readonly Dictionary<long, bool> webErrored = new();
    private readonly Dictionary<long, long> loadStartAt = new();
    private readonly Dictionary<long, Task<AccountWebHost?>> creatingTasks = new();
    private AccountWebHost? currentWebView;

    private long editId = -1;
    private long confirmDelId = -1;
    private long splashShownAt;
    private bool fullscreen;
    private WindowState preFullscreenState = WindowState.Normal;
    private bool probingDisk;
    private bool probeDiskDirty;

    private DispatcherTimer? splashLoadedTimer;
    private DispatcherTimer? splashFallbackTimer;
    private DispatcherTimer? splashMinDelayTimer;
    private DispatcherTimer? toastTimer;

    public MainWindow()
    {
        InitializeComponent();

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MuseLauncher");
        store = new AccountsStore(new FileKeyValueStore(Path.Combine(appDir, "accounts.json")));
        CheckStoreNotice();

        var cur = store.GetCurrentId();
        if (cur < 0 || store.FindById(cur) == null)
        {
            if (store.All().Count == 0)
            {
                RenderAccounts();
            }
            else
            {
                _ = SwitchTo(store.All()[0].Id, animate: false);
            }
        }
        else
        {
            _ = SwitchTo(cur, animate: false);
        }

        if (store.IsFullscreen()) EnterFullscreen();

        ProbeNonAliveSoon();

        Closing += (_, _) =>
        {
            foreach (var host in webViews.Snapshot().Values) host.Dispose();
        };
    }

    private static long Tick() => Environment.TickCount64;

    /// <summary>账号存储损坏/自愈提示（takeLoadNotice 取走即清，不重复弹）。</summary>
    private void CheckStoreNotice()
    {
        var n = store.TakeLoadNotice();
        if (n == "recovered")
        {
            Toast(Strings.ToastStoreRecovered);
        }
        else if (n == "corrupt")
        {
            Toast(Strings.ToastStoreCorrupt);
        }
    }

    // ---------- 调试/自动化命令（仅 Debug 构建，对应安卓 am start extras） ----------

    public void HandleLaunchCommands(string[] args)
    {
#if DEBUG
        if (!LaunchCommands.Enabled(debugBuild: true)) return;
        var cmd = LaunchCommands.Parse(args);
        if (cmd.StartAccount >= 0 && store.FindById(cmd.StartAccount) != null)
        {
            _ = SwitchTo(cmd.StartAccount, animate: true);
        }
        if (cmd.HasFullscreen)
        {
            if (cmd.Fullscreen) EnterFullscreen();
            else ExitFullscreen();
        }
        if (cmd.ShowDrawer) OpenDrawer();
        if (cmd.HasOpenConfig)
        {
            var cfgId = cmd.OpenConfigId >= 0 ? cmd.OpenConfigId : store.GetCurrentId();
            if (cfgId >= 0) OpenConfig(cfgId);
        }
        if (cmd.SearchQuery != null)
        {
            OpenDrawer();
            SearchInput.Text = cmd.SearchQuery;
        }
        if (cmd.DumpAuth) DumpAuthState();
        if (cmd.AddAccount) AddAccount();
        if (cmd.HasDelAccount && cmd.DelAccountId >= 0 && store.FindById(cmd.DelAccountId) != null)
        {
            DeleteAccount(cmd.DelAccountId);
        }
#endif
    }

    private void DumpAuthState()
    {
#if DEBUG
        DumpAuthStateCore();
#endif
    }

#if DEBUG
    private async void DumpAuthStateCore()
    {
        foreach (var a in store.All())
        {
            var host = webViews.Get(a.Id);
            if (host != null)
            {
                try
                {
                    var names = await host.GetMuseCookieNamesAsync();
                    Log.I("MuseAuth", $"acct={a.Id} name={a.Name} cookies=[{string.Join(' ', names)}]");
                    var js = await host.EvalAuthStateAsync();
                    Log.I("MuseAuth", $"acct={a.Id} js={js}");
                }
                catch (Exception ex)
                {
                    Log.I("MuseAuth", $"acct={a.Id} err={ex.Message}");
                }
            }
            else
            {
                var names = AuthProbe.ProbeCookieNamesOnDisk(a.Id);
                Log.I("MuseAuth", $"acct={a.Id} name={a.Name} cookies=" + (names == null
                    ? "null"
                    : "[" + string.Join(' ', names) + "]") + " (disk)");
            }
        }
        Toast("已输出登录态到日志：" + Log.FilePath);
    }
#endif

    // ---------- 全屏模式 ----------

    private void EnterFullscreen()
    {
        if (fullscreen) return;
        fullscreen = true;
        store.SetFullscreen(true);
        SidebarCol.Width = new GridLength(0);
        Rail.Visibility = Visibility.Collapsed;
        DrawerContent.Visibility = Visibility.Collapsed;
        preFullscreenState = WindowState;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
    }

    private void ExitFullscreen()
    {
        if (!fullscreen) return;
        fullscreen = false;
        store.SetFullscreen(false);
        SidebarCol.Width = new GridLength(56);
        Rail.Visibility = Visibility.Visible;
        DrawerContent.Visibility = Visibility.Collapsed;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = preFullscreenState;
    }

    // ---------- WebView（每账号独立实例） ----------

    private Task<AccountWebHost?> GetOrCreateAsync(Account a)
    {
        var existing = webViews.Get(a.Id);
        if (existing != null) return Task.FromResult<AccountWebHost?>(existing);
        if (!creatingTasks.TryGetValue(a.Id, out var t))
        {
            t = CreateWebViewAsync(a);
            creatingTasks[a.Id] = t;
            _ = t.ContinueWith(
                _ => creatingTasks.Remove(a.Id),
                TaskScheduler.FromCurrentSynchronizationContext());
        }
        return t;
    }

    private async Task<AccountWebHost?> CreateWebViewAsync(Account a)
    {
        loadStartAt[a.Id] = Tick();
        Log.I("MusePerf", $"load start id={a.Id}");
        try
        {
            // 先把控件挂进可视化树再异步初始化，确保底层 HWND 有父窗口
            var host = await AccountWebHost.CreateAsync(a, wv => WebContainer.Children.Add(wv));
            if (store.FindById(a.Id) == null)
            {
                // 创建期间账号被删除
                host.Dispose();
                return null;
            }
            host.LoadStartAt = loadStartAt[a.Id];
            host.NavigationStarting += h => webErrored[h.AccountId] = false;
            host.NavigationCompleted += OnNavigationCompleted;
            host.RendererFailed += OnRendererFailed;
            host.SetVisible(false);
            webViews.Put(a.Id, host);

            // LRU 驱逐超出上限的旧账号页面（磁盘数据保留，仅页面状态丢弃）
            foreach (var evictedId in webViews.TouchAndEvict(a.Id, host))
            {
                EvictWebView(evictedId);
            }
            return host;
        }
        catch (Exception ex)
        {
            loadStartAt.Remove(a.Id);
            Log.I("MusePerf", $"load error id={a.Id} desc={ex.Message}");
            if (store.GetCurrentId() == a.Id)
            {
                ShowErrorOverlay("创建浏览器环境失败：" + ex.Message);
            }
            return null;
        }
    }

    private void EvictWebView(long id)
    {
        var old = webViews.Remove(id);
        if (old == null) return;
        webLoaded.Remove(id);
        webErrored.Remove(id);
        loadStartAt.Remove(id);
        if (currentWebView == old) currentWebView = null;
        WebContainer.Children.Remove(old.Control);
        old.Dispose();
        Log.I("MuseWebview", $"lru evict id={id}");
    }

    private void OnNavigationCompleted(AccountWebHost host, bool isSuccess, string? errorText)
    {
        var id = host.AccountId;
        webLoaded[id] = true;
        if (loadStartAt.TryGetValue(id, out var st))
        {
            Log.I("MusePerf", $"load end id={id} cost={Tick() - st}ms errored={!isSuccess}");
            loadStartAt.Remove(id);
        }
        if (!isSuccess)
        {
            webErrored[id] = true;
        }
        if (store.GetCurrentId() == id)
        {
            if (webErrored.GetValueOrDefault(id))
            {
                ShowErrorOverlay(errorText ?? Strings.ErrorGeneric); // 错误页已渲染，保持遮罩等用户重试
            }
            else
            {
                HideErrorOverlay();
            }
            HideSplashAfterMinDelay();
        }
        _ = ProbeAliveAsync(host);
    }

    private void OnRendererFailed(AccountWebHost host)
    {
        // 渲染进程崩溃：销毁并重建该账号的 WebView；磁盘数据不受影响，登录态保留
        var id = host.AccountId;
        var wasCurrent = currentWebView == host;
        webViews.Remove(id);
        webLoaded.Remove(id);
        webErrored.Remove(id);
        loadStartAt.Remove(id);
        if (currentWebView == host) currentWebView = null;
        WebContainer.Children.Remove(host.Control);
        host.Dispose();

        if (wasCurrent && store.FindById(id) != null)
        {
            Toast(Strings.ToastWebviewRecovered);
            _ = SwitchTo(id, animate: false);
        }
    }

    private void ShowErrorOverlay(string msg)
    {
        ErrorMsg.Text = string.IsNullOrEmpty(msg) ? Strings.ErrorGeneric : msg;
        ErrorOverlay.Visibility = Visibility.Visible;
        currentWebView?.SetVisible(false);
    }

    private void HideErrorOverlay()
    {
        ErrorOverlay.Visibility = Visibility.Collapsed;
        currentWebView?.SetVisible(true);
    }

    // ---------- 账号操作 ----------

    private async void AddAccount()
    {
        // 新环境继承当前账号的代理：国内直连 muse.ai 不通，空代理的新环境必然卡在加载
        var prev = store.FindById(store.GetCurrentId());
        var a = store.AddDefault(prev?.Proxy);
        editId = -1;
        confirmDelId = -1;
        await SwitchTo(a.Id, animate: true);
        Toast(string.Format(Strings.ToastCreatedFmt, a.Name));
    }

    private async Task SwitchTo(long id, bool animate)
    {
        var a = store.FindById(id);
        if (a == null) return;
        store.SetCurrent(id);
        RenderAccounts();
        HideErrorOverlay();

        if (animate) ShowSplashFor(a);

        var host = await GetOrCreateAsync(a);
        if (host == null) return;           // 创建失败：错误遮罩已显示
        if (store.GetCurrentId() != id) return; // 等待期间用户已切走

        foreach (var kv in webViews.Snapshot())
        {
            kv.Value.SetVisible(kv.Key == id);
        }
        currentWebView = host;

        // 目标页面若处于错误态：自动重新加载，避免停留在无法恢复的错误页
        if (webErrored.GetValueOrDefault(id))
        {
            webErrored[id] = false;
            loadStartAt[id] = Tick();
            Log.I("MusePerf", $"load reload-on-switch id={id}");
            host.Reload();
        }
    }

    private void CommitRename(long id, TextBox input)
    {
        var a = store.FindById(id);
        editId = -1;
        if (a == null)
        {
            RenderAccounts();
            return;
        }
        var name = input.Text.Trim();
        if (name.Length > 16) name = name[..16];
        if (name.Length == 0 || name == a.Name)
        {
            RenderAccounts();
            return;
        }
        store.Rename(id, name);
        RenderAccounts();
        Toast(string.Format(Strings.ToastRenamedFmt, name));
    }

    private async void DeleteAccount(long id)
    {
        if (store.FindById(id) == null) return;
        // 销毁该账号的 WebView 实例
        var host = webViews.Get(id);
        if (host != null)
        {
            webViews.Remove(id);
            if (currentWebView == host) currentWebView = null;
            WebContainer.Children.Remove(host.Control);
            host.Dispose();
        }
        creatingTasks.Remove(id);
        webLoaded.Remove(id);
        webErrored.Remove(id);
        loadStartAt.Remove(id);

        // 清除该账号的磁盘环境（浏览器进程退出可能需要几秒，后台重试删除）
        _ = Task.Run(() => DeleteProfileDirWithRetry(AccountWebHost.ProfileDir(id)));

        store.Remove(id);
        editId = -1;
        confirmDelId = -1;
        RenderAccounts();
        Toast(Strings.ToastDeleted);
        ProbeNonAliveSoon();

        var cur = store.GetCurrentId();
        if (cur >= 0)
        {
            await SwitchTo(cur, animate: true);
        }
    }

    private static void DeleteProfileDirWithRetry(string dir)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, recursive: true);
                Log.I("MuseWebview", $"profile dir deleted: {dir}");
                return;
            }
            catch when (attempt < 9)
            {
                Thread.Sleep(700); // 进程树退出中，稍后重试
            }
            catch (Exception ex)
            {
                Log.I("MuseWebview", $"profile dir delete failed: {dir} {ex.Message}");
                return;
            }
        }
    }

    // ---------- 登录态探测 ----------

    private async Task ProbeAliveAsync(AccountWebHost host)
    {
        try
        {
            var names = await host.GetMuseCookieNamesAsync();
            UpdateLogged(host.AccountId, AuthProbe.IsLogged(names));
        }
        catch
        {
            // webview 可能刚被驱逐
        }
    }

    private void UpdateLogged(long id, bool? logged)
    {
        if (logged == null) return;
        var a = store.FindById(id);
        if (a == null || a.Logged == logged) return;
        a.Logged = logged.Value;
        RenderAccounts();
    }

    /// <summary>后台探测未活动账号（读磁盘 Cookies 库，尽力而为）；已有探测在跑则合并下一轮。</summary>
    private async void ProbeNonAliveSoon()
    {
        if (probingDisk)
        {
            probeDiskDirty = true;
            return;
        }
        probingDisk = true;
        try
        {
            do
            {
                probeDiskDirty = false;
                var targets = store.All().Where(a => !webViews.Contains(a.Id)).ToList();
                foreach (var (id, logged) in await AuthProbe.ProbeAccountsAsync(targets))
                {
                    UpdateLogged(id, logged);
                }
            } while (probeDiskDirty);
        }
        finally
        {
            probingDisk = false;
        }
    }

    // ---------- 渲染 ----------

    private void RenderAccounts()
    {
        RailAvatars.Children.Clear();
        AccountList.Children.Clear();
        var q = SearchInput.Text.Trim();
        var shown = 0;
        foreach (var a in store.All())
        {
            if (!Matches(a, q)) continue;
            shown++;
            RailAvatars.Children.Add(MakeRailAvatar(a));
            AccountList.Children.Add(MakeDrawerRow(a));
        }
        AccCount.Text = string.Format(Strings.AccountsCountFmt, store.All().Count);
        EmptyState.Visibility = store.All().Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawerEmpty.Visibility = q.Length > 0 && shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按 名称 + 标签 过滤（忽略大小写，空 query 全匹配）</summary>
    private static bool Matches(Account a, string q)
    {
        if (q.Length == 0) return true;
        var key = q.ToLowerInvariant();
        if (a.Name.ToLowerInvariant().Contains(key)) return true;
        return a.Tags.Any(t => t.ToLowerInvariant().Contains(key));
    }

    private void ShowSplashFor(Account a)
    {
        SplashTile.Text = string.IsNullOrEmpty(a.Avatar) ? "m" : a.Avatar;
        SplashName.Text = a.Name;
        Splash.Visibility = Visibility.Visible;
        currentWebView?.SetVisible(false);
        splashShownAt = Tick();

        splashLoadedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SplashLoadedHideMs) };
        splashLoadedTimer.Tick += (_, _) => { splashLoadedTimer!.Stop(); HideSplash(); };
        splashLoadedTimer.Stop();

        splashFallbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SplashTimeoutMs) };
        splashFallbackTimer.Tick += (_, _) => { splashFallbackTimer!.Stop(); HideSplash(); };
        splashFallbackTimer.Stop();

        // 已加载过的账号不会再次触发 NavigationCompleted，直接短延时收起
        if (webLoaded.GetValueOrDefault(a.Id)) splashLoadedTimer.Start();
        // 兜底：加载异常时也不会卡在过渡层
        splashFallbackTimer.Start();
    }

    private void HideSplashAfterMinDelay()
    {
        if (Splash.Visibility != Visibility.Visible) return;
        var elapsed = Tick() - splashShownAt;
        var delay = Math.Max(0, MinSplashMs - elapsed);

        splashMinDelayTimer ??= new DispatcherTimer();
        splashMinDelayTimer.Tick += (_, _) => { splashMinDelayTimer!.Stop(); HideSplash(); };
        splashMinDelayTimer.Interval = TimeSpan.FromMilliseconds(delay);
        splashMinDelayTimer.Stop();
        splashMinDelayTimer.Start();
    }

    private void HideSplash()
    {
        splashLoadedTimer?.Stop();
        splashFallbackTimer?.Stop();
        Splash.Visibility = Visibility.Collapsed;
        if (!webErrored.GetValueOrDefault(store.GetCurrentId()))
        {
            currentWebView?.SetVisible(true);
        }
    }

    private Border MakeRailAvatar(Account a)
    {
        var wrap = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Margin = new Thickness(0, 4, 0, 4),
            Background = a.Id == store.GetCurrentId() ? new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x40)) : Brushes.Transparent,
            Child = MakeTile(38, 20, a.Avatar),
            Cursor = Cursors.Hand,
        };
        wrap.MouseLeftButtonUp += (_, _) => _ = SwitchTo(a.Id, animate: true);

        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 1, 1),
            Background = new SolidColorBrush(a.Logged
                ? Color.FromRgb(0x34, 0xC7, 0x59)
                : Color.FromRgb(0x4A, 0x4F, 0x57)),
        };
        var grid = new Grid();
        grid.Children.Add(wrap);
        grid.Children.Add(dot);
        return new Border { Child = grid };
    }

    private FrameworkElement MakeDrawerRow(Account a)
    {
        // 删除确认态。DockPanel 按添加顺序停靠、最后一个子元素填充：
        // 先加靠右按钮（后加的靠左），最后加消息（填充）
        if (confirmDelId == a.Id)
        {
            var row = new Border
            {
                Background = RowBrush(false),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 4),
            };
            var dp = new DockPanel { LastChildFill = true };
            dp.Children.Add(MakeTextBtn(Strings.BtnCancel, danger: false, () =>
            {
                confirmDelId = -1;
                RenderAccounts();
            }));
            dp.Children.Add(MakeTextBtn(Strings.BtnDelete, danger: true, () => DeleteAccount(a.Id)));
            dp.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.ConfirmDeleteFmt, a.Name),
                Foreground = ForegroundBrush,
                FontSize = 12.5,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Child = dp;
            return row;
        }

        // 重命名编辑态：tile | 输入框(填充) | ✓ | ✕
        if (editId == a.Id)
        {
            var row = new Border
            {
                Background = RowBrush(false),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 4),
            };
            var dp = new DockPanel { LastChildFill = true };
            dp.Children.Add(MakeIconBtn("\uE711", Strings.BtnCancel, () =>
            {
                editId = -1;
                RenderAccounts();
            }));
            dp.Children.Add(MakeIconBtn("\uE73E", Strings.CdRename, () =>
            {
                if (renameInput != null) CommitRename(a.Id, renameInput);
            }));
            dp.Children.Add(MakeTile(40, 20, a.Avatar));

            var input = new TextBox
            {
                Text = a.Name,
                Foreground = ForegroundBrush,
                FontSize = 13.5,
                Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x14)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7C, 0xF6)),
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 32,
                Margin = new Thickness(8, 0, 0, 0),
            };
            dp.Children.Add(input);
            renameInput = input;
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitRename(a.Id, input);
                else if (e.Key == Key.Escape)
                {
                    editId = -1;
                    RenderAccounts();
                }
            };
            input.LostFocus += (_, _) => { if (editId == a.Id) CommitRename(a.Id, input); };
            row.Child = dp;
            input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
            return row;
        }

        // 常规态：tile | 信息(填充) | 设置 | 重命名 | 删除
        var normal = new Border
        {
            Background = RowBrush(a.Id == store.GetCurrentId()),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
        };
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(MakeIconBtn("\uE74D", Strings.CdDelete, () =>
        {
            confirmDelId = a.Id;
            editId = -1;
            RenderAccounts();
        }));
        dock.Children.Add(MakeIconBtn("\uE70F", Strings.CdRename, () =>
        {
            editId = a.Id;
            confirmDelId = -1;
            RenderAccounts();
        }));
        dock.Children.Add(MakeIconBtn("\uE713", Strings.CdConfig, () => OpenConfig(a.Id)));
        dock.Children.Add(MakeTile(40, 20, a.Avatar));

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = a.Name,
            Foreground = ForegroundBrush,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var statusColor = new SolidColorBrush(a.Logged
            ? Color.FromRgb(0x34, 0xC7, 0x59)
            : Color.FromRgb(0x4A, 0x4F, 0x57));
        var sub = new TextBlock { FontSize = 11.5, Foreground = MutedBrush, Margin = new Thickness(0, 3, 0, 0) };
        sub.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = statusColor });
        sub.Inlines.Add(a.Logged ? Strings.StatusLogged : Strings.StatusNotLogged);
        info.Children.Add(sub);

        dock.Children.Add(info); // 最后添加 = 填充中间

        normal.Child = dock;
        normal.MouseLeftButtonUp += (_, _) => _ = SwitchTo(a.Id, animate: true);
        return normal;
    }

    private TextBox? renameInput;

    private static readonly Brush ForegroundBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEE));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));

    private static Brush RowBrush(bool active)
    {
        return active
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x27, 0x40))
            : new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C));
    }

    private Border MakeTile(double sizePx, double fontSize, string? text)
    {
        return new Border
        {
            Width = sizePx,
            Height = sizePx,
            CornerRadius = new CornerRadius(sizePx * 0.28),
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1C, 0x28)),
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(text) ? "m" : text,
                FontFamily = new FontFamily("Georgia"),
                FontWeight = FontWeights.Bold,
                FontStyle = FontStyles.Italic,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x7C, 0xF6)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, sizePx * 0.1),
            },
        };
    }

    private Button MakeIconBtn(string glyph, string toolTip, Action onClick)
    {
        var b = new Button
        {
            Style = (Style)Resources["IconButtonSm"],
            ToolTip = toolTip,
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0x83, 0x8C)),
            },
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(b, Dock.Right);
        b.Click += (_, _) => onClick();
        return b;
    }

    private Button MakeTextBtn(string text, bool danger, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 12,
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(danger
                ? Color.FromRgb(0x8C, 0x2F, 0x36)
                : Color.FromRgb(0x22, 0x25, 0x2C)),
            Template = (ControlTemplate)FindResource("PillTemplate"),
        };
        DockPanel.SetDock(b, Dock.Right);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------- 配置页 ----------

    private void OpenConfig(long id)
    {
        var a = store.FindById(id);
        if (a == null) return;
        var oldProxy = a.Proxy;
        var win = new AccountConfigWindow(a) { Owner = this };
        win.ShowDialog();
        RenderAccounts();
        if (win.Saved && oldProxy != a.Proxy && webViews.Get(id) != null)
        {
            // 代理变了：驱逐该账号环境，下次进入按新代理重建（登录数据在磁盘，不受影响）
            EvictWebView(id);
            if (store.GetCurrentId() == id)
            {
                _ = SwitchTo(id, animate: true);
            }
        }
        ProbeNonAliveSoon();
    }

    // ---------- 抽屉/侧栏 ----------

    private void OpenDrawer()
    {
        if (fullscreen) return;
        SidebarCol.Width = new GridLength(274);
        Rail.Visibility = Visibility.Collapsed;
        DrawerContent.Visibility = Visibility.Visible;
    }

    private void CloseDrawer()
    {
        SidebarCol.Width = new GridLength(56);
        DrawerContent.Visibility = Visibility.Collapsed;
        Rail.Visibility = Visibility.Visible;
    }

    // ---------- Toast ----------

    private void Toast(string msg)
    {
        ToastText.Text = msg;
        ToastPopup.IsOpen = true;
        toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        toastTimer.Tick += ToastTimerTick;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void ToastTimerTick(object? sender, EventArgs e)
    {
        toastTimer!.Stop();
        ToastPopup.IsOpen = false;
    }

    // ---------- 事件 ----------

    private void OnRailExpandClick(object sender, RoutedEventArgs e) => OpenDrawer();

    private void OnCollapseClick(object sender, RoutedEventArgs e) => CloseDrawer();

    private void OnAddAccountClick(object sender, RoutedEventArgs e) => AddAccount();

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => EnterFullscreen();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RenderAccounts();

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        var curId = store.GetCurrentId();
        var host = currentWebView;
        if (host != null && curId >= 0)
        {
            webErrored[curId] = false;
            HideErrorOverlay();
            loadStartAt[curId] = Tick();
            Log.I("MusePerf", $"load retry id={curId}");
            host.Reload();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            if (fullscreen) ExitFullscreen();
            else EnterFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (fullscreen)
            {
                ExitFullscreen();
                e.Handled = true;
            }
            else if (DrawerContent.Visibility == Visibility.Visible)
            {
                CloseDrawer();
                e.Handled = true;
            }
            else if (currentWebView is { CanGoBack: true })
            {
                currentWebView.GoBack();
                e.Handled = true;
            }
            return;
        }
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Alt && e.Key == Key.Left
            && currentWebView is { CanGoBack: true })
        {
            currentWebView.GoBack();
            e.Handled = true;
        }
    }
}
