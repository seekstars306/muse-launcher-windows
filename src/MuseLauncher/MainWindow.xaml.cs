using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>窗口管理展示状态；协调器统一拥有创建中和常驻会话，实际释放淘汰资源。</summary>
public partial class MainWindow : Window
{
    private const int MaxAliveWebviews = 4;
    private const int MinSplashMs = 500;
    private const int SplashTimeoutMs = 8000;
    private readonly AccountsStore store;
    private readonly SessionCoordinator<AccountWebHost, BrowserSessionConfiguration> sessions;
    private readonly BrowserViewState viewState = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<long> changingAccounts = new();
    private readonly Dictionary<long, int> recoveryAttempts = new();
    private readonly string profilesRoot;
    private readonly string avatarDirectory;
    private readonly string startUrl;
    private readonly DispatcherTimer toastTimer;
    private CancellationTokenSource? transition;
    private long splashShownAt;
    private long editId = -1;
    private long confirmDelId = -1;
    private bool fullscreen;
    private WindowState preFullscreenState = WindowState.Normal;
    private bool probingDisk;
    private bool probeDiskDirty;
    private bool closing;
    private bool closed;
    private bool drawerManuallyExpanded;
    private bool sidebarHidden;
    private bool drawerAutoCollapsed;
    private TextBox? renameInput;

    public MainWindow() : this(null, null, AccountWebHost.StartUrl) { }

    internal MainWindow(string? dataDirectory, string? profilesDirectory, string initialUrl)
    {
        profilesRoot = Path.GetFullPath(profilesDirectory ?? AccountWebHost.ProfilesRoot);
        startUrl = initialUrl;
        InitializeComponent();
        var appDir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuseLauncher");
        avatarDirectory = Path.Combine(appDir, "Avatars");
        store = new AccountsStore(new FileKeyValueStore(Path.Combine(appDir, "accounts.json")),
            id => Directory.Exists(ProfilePath(id)));
        sessions = new SessionCoordinator<AccountWebHost, BrowserSessionConfiguration>(MaxAliveWebviews,
            CreateWebViewAsync, host => host.DisposeAsync().AsTask());
        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); ToastPopup.IsOpen = false; };
        Closing += OnClosing;
        Activated += (_, _) => ProbeNonAliveSoon();
        Deactivated += (_, _) => { toastTimer.Stop(); ToastPopup.IsOpen = false; };
        Loaded += async (_, _) =>
        {
            CheckStoreNotice();
            RenderAccounts();
            if (store.IsFullscreen()) ApplyFullscreen(true);
            var current = store.GetCurrentId();
            if (current > 0 && viewState.AccountId == null) await SwitchTo(current, animate: true);
            ProbeNonAliveSoon();
        };
    }

    internal AccountsStore Accounts => store;
    internal IReadOnlyDictionary<long, AccountWebHost> HostedSessions => sessions.Snapshot();
    internal int HostedControlCount => WebContainer.Children.Count;
    internal BrowserViewState DisplayState => viewState;
    internal Task ActivateAccountAsync(long id) => SwitchTo(id, animate: true);

    private string ProfilePath(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return Path.Combine(profilesRoot, "acct_" + id);
    }

    private void CheckStoreNotice()
    {
        var notice = store.TakeLoadNotice();
        if (notice == "recovered") Toast("账号数据已从备份恢复，请核对配置");
        else if (notice == "corrupt") Toast("已跳过损坏或重复的账号记录，请核对账号列表");
    }

    public async void HandleLaunchCommands(string[] args)
    {
#if DEBUG
        if (closing || credentialTransferBusy) return;
        try
        {
            var command = LaunchCommands.Parse(args);
            if (command.StartAccount > 0) await SwitchTo(command.StartAccount, animate: true);
            if (command.HasFullscreen) SetFullscreen(command.Fullscreen);
            if (command.ShowDrawer) OpenDrawer();
            if (command.SearchQuery != null) { OpenDrawer(); SearchInput.Text = command.SearchQuery; }
            if (command.HasOpenConfig)
            {
                var id = command.OpenConfigId > 0 ? command.OpenConfigId : store.GetCurrentId();
                await OpenConfigAsync(id);
            }
            if (command.AddAccount) await AddAccountAsync();
            if (command.HasDelAccount && command.DelAccountId > 0) await DeleteAccountAsync(command.DelAccountId);
            if (command.DumpAuth) await DumpAuthStateAsync();
        }
        catch (Exception ex) { ReportOperationFailure("调试命令执行失败", ex); }
#else
        await Task.CompletedTask;
#endif
    }

#if DEBUG
    private async Task DumpAuthStateAsync()
    {
        foreach (var account in store.All())
        {
            if (!sessions.Snapshot().TryGetValue(account.Id, out var host)) continue;
            try
            {
                var names = await host.GetMuseCookieNamesAsync();
                var keys = await host.EvalAuthStateAsync();
                Log.I("MuseAuth", $"acct={account.Id} cookieNames=[{string.Join(' ', names)}] keys={keys}");
            }
            catch (Exception ex) { Log.I("MuseAuth", $"acct={account.Id} unavailable={ex.GetType().Name}"); }
        }
        Toast("已输出 cookie 名与存储键，不含 cookie 值：" + Log.FilePath);
    }
#endif

    private void SetFullscreen(bool value)
    {
        if (fullscreen == value || closing) return;
        try { store.SetFullscreen(value); ApplyFullscreen(value); }
        catch (Exception ex) { ReportOperationFailure("未能保存全屏设置", ex); }
    }

    private void ApplyFullscreen(bool value)
    {
        fullscreen = value;
        if (value)
        {
            preFullscreenState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = preFullscreenState;
        }
        UpdateSidebarLayout();
    }

    private void UpdateSidebarLayout()
    {
        if (fullscreen)
        {
            SidebarCol.Width = new GridLength(0);
            Rail.Visibility = Visibility.Collapsed;
            DrawerContent.Visibility = Visibility.Collapsed;
            SidebarEdgeTrigger.Visibility = Visibility.Collapsed;
        }
        else if (sidebarHidden)
        {
            SidebarCol.Width = new GridLength(4);
            Rail.Visibility = Visibility.Collapsed;
            DrawerContent.Visibility = Visibility.Collapsed;
            SidebarEdgeTrigger.Visibility = Visibility.Visible;
        }
        else if (drawerManuallyExpanded && !drawerAutoCollapsed)
        {
            SidebarCol.Width = new GridLength(274);
            Rail.Visibility = Visibility.Collapsed;
            DrawerContent.Visibility = Visibility.Visible;
            SidebarEdgeTrigger.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarCol.Width = new GridLength(56);
            Rail.Visibility = Visibility.Visible;
            DrawerContent.Visibility = Visibility.Collapsed;
            SidebarEdgeTrigger.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<AccountWebHost> CreateWebViewAsync(
        long id, BrowserSessionConfiguration configuration, CancellationToken cancellationToken)
    {
        var host = await AccountWebHost.CreateAsync(id, configuration,
            control => WebContainer.Children.Add(control), cancellationToken);
        try
        {
            if (PendingCredentials.Exists(configuration.ProfileDirectory!))
            {
                try
                {
                    var imported = PendingCredentials.Read(configuration.ProfileDirectory!);
                    var restored = await host.RestoreMuseCookiesAsync(imported.Cookies);
                    if (!restored) Toast("导入账号的登录 Cookie 已失效，请重新登录");
                }
                catch (Exception)
                {
                    Toast("导入账号的登录态未能恢复，请重新登录");
                }
            }
            // 被取消的 host 会销毁；保留 pending，供下次激活恢复 session Cookie。
            cancellationToken.ThrowIfCancellationRequested();
            host.NavigationStarting += OnNavigationStarting;
            host.NavigationCompleted += OnNavigationCompleted;
            host.RendererFailed += OnRendererFailed;
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private void RefreshVisibleState()
    {
        var current = sessions.Current;
        foreach (var entry in sessions.Snapshot())
        {
            var show = !credentialTransferBusy && viewState.CanShowWebView && viewState.AccountId == entry.Key
                && ReferenceEquals(current, entry.Value) && sessions.IsCurrent(entry.Key, entry.Value);
            entry.Value.SetVisible(show);
        }
        Splash.Visibility = viewState.SplashVisible ? Visibility.Visible : Visibility.Collapsed;
        ErrorOverlay.Visibility = viewState.Error != null ? Visibility.Visible : Visibility.Collapsed;
        ErrorMsg.Text = viewState.Error ?? "";
    }

    private void OnNavigationStarting(AccountWebHost host)
    {
        if (!sessions.IsCurrent(host.AccountId, host) || closing) return;
        viewState.SetError(host.AccountId, viewState.Version, null);
        RefreshVisibleState();
    }

    private void OnNavigationCompleted(AccountWebHost host, bool success, string? error)
    {
        if (!sessions.IsRegistered(host.AccountId, host) || closing || credentialTransferBusy) return;
        if (success) recoveryAttempts.Remove(host.AccountId);
        if (sessions.IsCurrent(host.AccountId, host))
        {
            var version = viewState.Version;
            viewState.SetError(host.AccountId, version, success ? null : error ?? Strings.ErrorGeneric);
            if (success) _ = HideSplashAfterDelayAsync(host.AccountId, version,
                Math.Max(0, MinSplashMs - (Environment.TickCount64 - splashShownAt)));
            RefreshVisibleState();
        }
        _ = ProbeAliveAsync(host);
    }

    private async void OnRendererFailed(AccountWebHost host)
    {
        if (closing || !sessions.IsRegistered(host.AccountId, host)) return;
        if (credentialTransferBusy)
        {
            credentialTransferFailures.Add(host);
            return;
        }
        var wasCurrent = sessions.IsCurrent(host.AccountId, host);
        var version = viewState.Version;
        try
        {
            if (!await sessions.InvalidateAsync(host.AccountId, host)) return;
            if (!wasCurrent || !viewState.Matches(host.AccountId, version) || closing) return;
            var attempts = recoveryAttempts.GetValueOrDefault(host.AccountId) + 1;
            recoveryAttempts[host.AccountId] = attempts;
            if (attempts > 2)
            {
                viewState.SetError(host.AccountId, version, "浏览器连续退出，请检查环境后点击重试");
                RefreshVisibleState();
                return;
            }
            Toast("浏览器异常，正在重建该账号环境");
            await SwitchTo(host.AccountId, animate: true);
        }
        catch (Exception ex)
        {
            if (viewState.Matches(host.AccountId, version))
            {
                viewState.SetError(host.AccountId, version, "环境释放失败，请稍后重试或重新打开启动器");
                RefreshVisibleState();
            }
            ReportOperationFailure("浏览器恢复失败", ex);
        }
    }

    private async Task SwitchTo(long id, bool animate, bool reload = false)
    {
        if (closing || credentialTransferBusy || changingAccounts.Contains(id)) return;
        var account = store.FindById(id);
        if (account == null) return;
        try { store.SetCurrent(id); }
        catch (Exception ex) { ReportOperationFailure("未能保存当前账号", ex); return; }

        transition?.Cancel();
        transition?.Dispose();
        transition = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var version = viewState.Begin(id, showSplash: true);
        splashShownAt = Environment.TickCount64;
        SplashName.Text = account.Name;
        SplashTile.Text = account.Avatar;
        RenderAccounts();
        RefreshVisibleState();
        _ = HideSplashAfterDelayAsync(id, version, SplashTimeoutMs);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(transition.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var host = await sessions.ActivateAsync(id,
                new BrowserSessionConfiguration(account.Proxy, account.ForceProxy, ProfilePath(id)), timeout.Token);
            if (!viewState.Matches(id, version) || closing || credentialTransferBusy) return;
            if (host == null)
            {
                viewState.SetError(id, version, "环境创建已取消或超时，请重试");
                RefreshVisibleState();
                return;
            }
            // 只有协调器确认激活成功后才消费 pending；删除失败时禁止导航，避免重放旧凭证。
            try { PendingCredentials.Delete(ProfilePath(id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                viewState.SetError(id, version, "待恢复凭证文件无法清理，请检查账号目录权限后重试");
                RefreshVisibleState();
                return;
            }
            viewState.BindHost(id, version);
            viewState.SetError(id, version, null);
            RefreshVisibleState();
            if (!host.HasNavigated) host.Navigate(startUrl);
            else if (host.Errored || reload) host.Reload();
            else _ = HideSplashAfterDelayAsync(id, version,
                animate ? Math.Max(0, MinSplashMs - (Environment.TickCount64 - splashShownAt)) : 0);
            _ = ProbeAliveAsync(host);
        }
        catch (Exception ex)
        {
            if (viewState.Matches(id, version))
            {
                viewState.SetError(id, version, ex is FormatException ? ex.Message
                    : "创建浏览器环境失败。请检查代理或等待旧浏览器退出后重试。");
                RefreshVisibleState();
            }
            Log.I("MuseWebview", $"creation failed id={id} type={ex.GetType().Name}");
        }
    }

    private async Task HideSplashAfterDelayAsync(long id, long version, long milliseconds)
    {
        var token = transition?.Token ?? lifetime.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), token);
            if (!viewState.Matches(id, version)) return;
            if (!viewState.HasHost)
                viewState.SetError(id, version, "正在等待浏览器初始化，完成后将自动显示；也可以稍后重试");
            else viewState.HideSplash(id, version);
            RefreshVisibleState();
        }
        catch (OperationCanceledException) { }
    }

    private async Task AddAccountAsync()
    {
        if (closing || credentialTransferBusy) return;
        try
        {
            var previous = store.FindById(store.GetCurrentId());
            var account = store.AddDefault(previous?.Proxy, previous?.ForceProxy == true);
            editId = confirmDelId = -1;
            await SwitchTo(account.Id, animate: true);
            Toast(string.Format(Strings.ToastCreatedFmt, account.Name));
        }
        catch (Exception ex) { ReportOperationFailure("未能创建账号", ex); }
    }

    private void CommitRename(long id, TextBox input)
    {
        if (credentialTransferBusy) return;
        var name = input.Text.Trim();
        editId = -1;
        if (string.IsNullOrEmpty(name)) { RenderAccounts(); return; }
        try { store.Rename(id, LimitText(name, 16)); }
        catch (Exception ex) { ReportOperationFailure("名称未保存", ex); }
        RenderAccounts();
    }

    private static string LimitText(string value, int count)
    {
        var elements = StringInfo.GetTextElementEnumerator(value);
        var text = new System.Text.StringBuilder();
        for (var index = 0; index < count && elements.MoveNext(); index++) text.Append(elements.GetTextElement());
        return text.ToString();
    }

    private async Task DeleteAccountAsync(long id)
    {
        if (closing || credentialTransferBusy || store.FindById(id) == null || !changingAccounts.Add(id)) return;
        try
        {
            if (viewState.AccountId == id)
            {
                transition?.Cancel();
                viewState.Clear();
                RefreshVisibleState();
            }
            await sessions.InvalidateAsync(id);
            // 落盘成功后才删目录，保存失败不会留下“账号仍在、登录数据已删”的状态。
            store.Remove(id);
            recoveryAttempts.Remove(id);
            editId = confirmDelId = -1;
            RenderAccounts();
            var removed = await DeleteProfileDirectoryAsync(ProfilePath(id));
            Toast(removed ? Strings.ToastDeleted : "账号已移除，但旧目录仍被占用；已保留，不会用于新账号");
        }
        catch (Exception ex) { ReportOperationFailure("删除未完成，账号数据已保留", ex); }
        finally { changingAccounts.Remove(id); }
        if (!closing && store.GetCurrentId() > 0) await SwitchTo(store.GetCurrentId(), animate: true);
        else if (!closing) { viewState.Clear(); RefreshVisibleState(); }
    }

    private static async Task<bool> DeleteProfileDirectoryAsync(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await Task.Run(() => { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); });
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) return false;
                await Task.Delay(300);
            }
        }
        return false;
    }

    private async Task ProbeAliveAsync(AccountWebHost host)
    {
        try
        {
            var names = await host.GetMuseCookieNamesAsync();
            if (!closing && sessions.IsRegistered(host.AccountId, host)) UpdateLogged(host.AccountId, CookieProbe.IsLogged(names));
        }
        catch (Exception ex) { Log.I("MuseAuth", $"probe unavailable id={host.AccountId} type={ex.GetType().Name}"); }
    }

    private void UpdateLogged(long id, bool? logged)
    {
        if (logged == null || closing) return;
        var account = store.FindById(id);
        if (account == null || account.Logged == logged) return;
        store.SetLogged(id, logged.Value);
        if (editId < 0 && confirmDelId < 0) RenderAccounts();
    }

    private async void ProbeNonAliveSoon()
    {
        if (closing || credentialTransferBusy || sessions == null) return;
        if (probingDisk) { probeDiskDirty = true; return; }
        probingDisk = true;
        try
        {
            do
            {
                probeDiskDirty = false;
                var hosts = sessions.Snapshot();
                foreach (var account in store.All())
                {
                    if (closing || credentialTransferBusy) return;
                    if (hosts.TryGetValue(account.Id, out var host)) { await ProbeAliveAsync(host); continue; }
                    var names = await Task.Run(() => CookieProbe.ProbeCookieNamesOnDisk(ProfilePath(account.Id)));
                    if (!sessions.Snapshot().ContainsKey(account.Id))
                        UpdateLogged(account.Id, names == null ? null : CookieProbe.IsLogged(names));
                }
            } while (probeDiskDirty && !closing);
        }
        catch (Exception ex) { Log.I("MuseAuth", "disk probe failed: " + ex.GetType().Name); }
        finally { probingDisk = false; }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (credentialTransferBusy) return;
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        transition?.Cancel();
        viewState.Close();
        toastTimer.Stop();
        ToastPopup.IsOpen = false;
        RefreshVisibleState();
        try { await sessions.CloseAsync(); }
        catch (Exception ex)
        {
            Log.I("MuseWebview", "close failed: " + ex.GetType().Name);
            MessageBox.Show("部分浏览器未在超时内退出，账号目录未删除。下次启动会检查目录占用。", Strings.AppName);
        }
        finally
        {
            closed = true;
            // 空会话可能同步完成清理；等当前 Closing 事件返回后再关闭。
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void ReportOperationFailure(string operation, Exception exception)
    {
        Log.I("App", operation + ": " + exception.GetType().Name);
        if (!closing) Toast(operation + "。请检查磁盘权限、剩余空间或浏览器占用情况。");
    }

    // ---------- 账号列表 ----------

    private void RenderAccounts()
    {
        if (store == null || closing || draggingAccountId != null) return;
        RailAvatars.Children.Clear();
        AccountList.Children.Clear();
        var query = SearchInput.Text.Trim();
        var accounts = store.All();
        var shown = 0;
        foreach (var account in accounts)
        {
            if (!Matches(account, query)) continue;
            shown++;
            RailAvatars.Children.Add(MakeRailAvatar(account));
            AccountList.Children.Add(MakeDrawerRow(account));
        }
        AccCount.Text = string.Format(Strings.AccountsCountFmt, accounts.Count);
        EmptyState.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawerEmpty.Visibility = query.Length > 0 && shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(Account account, string query)
        => query.Length == 0 || account.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || account.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));

    private FrameworkElement MakeRailAvatar(Account account)
    {
        var selected = account.Id == store.GetCurrentId();
        var grid = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand };
        grid.Children.Add(new Border
        {
            Background = selected ? RowBrush(true) : Brushes.Transparent,
            BorderBrush = selected ? AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2 : 0),
            CornerRadius = new CornerRadius(14), Child = MakeTile(38, 20, LimitText(account.Avatar, 1), account.AvatarImage),
        });
        grid.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Background = StatusBrush(account.Logged), BorderBrush = BrushFromRgb(0x0F, 0x11, 0x14), BorderThickness = new Thickness(1),
        });
        grid.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await SwitchTo(account.Id, animate: true); };
        grid.ContextMenu = MakeAccountContextMenu(account);
        return grid;
    }

    private FrameworkElement MakeDrawerRow(Account account)
    {
        var selected = account.Id == store.GetCurrentId();
        var row = new Border
        {
            Background = RowBrush(selected), CornerRadius = new CornerRadius(10),
            BorderBrush = selected ? AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            Padding = new Thickness(9, 8, 9, 8), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand,
        };
        row.ContextMenu = MakeAccountContextMenu(account);
        var dock = new DockPanel { LastChildFill = true };
        row.Child = dock;
        if (confirmDelId == account.Id)
        {
            dock.Children.Add(MakeTextBtn(Strings.BtnCancel, false, () => { confirmDelId = -1; RenderAccounts(); }));
            dock.Children.Add(MakeTextBtn(Strings.BtnDelete, true, () => _ = DeleteAccountAsync(account.Id)));
            dock.Children.Add(new TextBlock
            {
                Text = string.Format(Strings.ConfirmDeleteFmt, account.Name), Foreground = ForegroundBrush,
                FontSize = 12.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            });
            return row;
        }
        if (editId == account.Id)
        {
            dock.Children.Add(MakeIconBtn("\uE711", Strings.BtnCancel, () => { editId = -1; RenderAccounts(); }));
            dock.Children.Add(MakeIconBtn("\uE73E", Strings.CdRename, () =>
            {
                if (renameInput != null) CommitRename(account.Id, renameInput);
            }));
            dock.Children.Add(MakeTile(40, 20, account.Avatar, account.AvatarImage));
            var input = new TextBox
            {
                Text = account.Name, Foreground = ForegroundBrush, FontSize = 13.5,
                Background = RowBrush(false), BorderBrush = Brushes.DodgerBlue,
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(8, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            renameInput = input;
            dock.Children.Add(input);
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; CommitRename(account.Id, input); }
                else if (e.Key == Key.Escape) { e.Handled = true; editId = -1; RenderAccounts(); }
            };
            input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
            return row;
        }
        dock.Children.Add(MakeIconBtn("\uE74D", Strings.CdDelete, () => { confirmDelId = account.Id; editId = -1; RenderAccounts(); }));
        dock.Children.Add(MakeIconBtn("\uE70F", Strings.CdRename, () => { editId = account.Id; confirmDelId = -1; RenderAccounts(); }));
        dock.Children.Add(MakeIconBtn("\uE713", Strings.CdConfig, () => _ = OpenConfigAsync(account.Id)));
        dock.Children.Add(MakeTile(40, 20, account.Avatar, account.AvatarImage));
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        info.Children.Add(new TextBlock
        {
            Text = account.Name, Foreground = ForegroundBrush, FontSize = 14.5,
            FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var status = new TextBlock { Foreground = MutedBrush, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis };
        status.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = StatusBrush(account.Logged) });
        status.Inlines.Add(account.Logged ? Strings.StatusLogged : Strings.StatusNotLogged);
        info.Children.Add(status);
        dock.Children.Add(info);
        AttachAccountOrderHandlers(row, account);
        row.MouseLeftButtonUp += async (_, e) =>
        {
            accountDragStart = null;
            if (suppressAccountClick)
            {
                suppressAccountClick = false;
                e.Handled = true;
                return;
            }
            if (IsButtonSource(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            await SwitchTo(account.Id, animate: true);
        };
        return row;
    }

    private ContextMenu MakeAccountContextMenu(Account account)
    {
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = string.Format(Strings.ContextDeleteFmt, account.Name) };
        delete.Click += async (_, _) =>
        {
            var result = MessageBox.Show(
                string.Format(Strings.ConfirmDeleteFmt, account.Name), Strings.ConfirmDeleteTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes) await DeleteAccountAsync(account.Id);
        };
        menu.Items.Add(delete);
        return menu;
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static readonly Brush ForegroundBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEE));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7F, 0xFF));
    private static Brush BrushFromRgb(byte red, byte green, byte blue) => new SolidColorBrush(Color.FromRgb(red, green, blue));
    private static Brush RowBrush(bool active) => new SolidColorBrush(active
        ? Color.FromRgb(0x1B, 0x27, 0x40) : Color.FromRgb(0x15, 0x17, 0x1C));
    private static Brush StatusBrush(bool logged) => new SolidColorBrush(logged
        ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0x4A, 0x4F, 0x57));

    private static Border MakeTile(double size, double fontSize, string? text, string? imagePath = null)
    {
        var border = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1C, 0x28)),
        };
        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128;
                using var stream = File.OpenRead(imagePath);
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                border.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
                return border;
            }
            catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException or NotSupportedException
                or InvalidOperationException or ArgumentException or FormatException or OutOfMemoryException)
            {
                // 文件被删除或不是受支持的图片时回退到文字头像。
            }
        }
        border.Child = new TextBlock
        {
            Text = string.IsNullOrEmpty(text) ? "m" : LimitText(text, 2), FontFamily = new FontFamily("Georgia"),
            FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic, FontSize = fontSize,
            Foreground = Brushes.DodgerBlue, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, size * 0.1),
        };
        return border;
    }

    private Button MakeIconBtn(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)Resources["IconButtonSm"], ToolTip = tooltip,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = MutedBrush },
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(button, Dock.Right);
        button.Click += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    private Button MakeTextBtn(string text, bool danger, Action onClick)
    {
        var button = new Button
        {
            Content = text, FontSize = 12, Foreground = Brushes.White, Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = danger ? Brushes.IndianRed : RowBrush(false), Template = (ControlTemplate)Resources["PillTemplate"],
        };
        DockPanel.SetDock(button, Dock.Right);
        button.Click += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    private async Task OpenConfigAsync(long id)
    {
        if (closing || credentialTransferBusy || changingAccounts.Contains(id)) return;
        var account = store.FindById(id);
        if (account == null) return;
        try
        {
            var window = new AccountConfigWindow(account, updated => store.Update(updated), avatarDirectory) { Owner = this };
            window.ShowDialog();
            RenderAccounts();
            if (!window.Saved || !window.ProxyChanged) return;
            await ApplySavedConfigurationAsync(id);
        }
        catch (Exception ex) { ReportOperationFailure("配置或环境重建失败，请重试", ex); }
    }

    internal async Task ApplySavedConfigurationAsync(long id)
    {
        if (credentialTransferBusy || !changingAccounts.Add(id)) return;
        var wasCurrent = store.GetCurrentId() == id;
        if (wasCurrent) { transition?.Cancel(); viewState.Clear(); RefreshVisibleState(); }
        try { await sessions.InvalidateAsync(id); }
        finally { changingAccounts.Remove(id); }
        if (wasCurrent && !closing && store.GetCurrentId() == id) await SwitchTo(id, animate: true);
    }

    private void OpenDrawer(bool manual = true)
    {
        if (fullscreen || closing) return;
        sidebarHidden = false;
        drawerAutoCollapsed = false;
        if (manual) drawerManuallyExpanded = true;
        UpdateSidebarLayout();
    }

    private void CloseDrawer(bool manual = true)
    {
        if (fullscreen || sidebarHidden) return;
        if (manual) drawerManuallyExpanded = false;
        drawerAutoCollapsed = !manual;
        UpdateSidebarLayout();
    }

    private void HideSidebar()
    {
        if (fullscreen || closing) return;
        sidebarHidden = true;
        drawerManuallyExpanded = false;
        UpdateSidebarLayout();
    }

    private void OnSidebarEdgeMouseEnter(object sender, MouseEventArgs e) => OpenDrawer();

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || fullscreen || closing || sidebarHidden) return;
        if (ActualWidth < 980 && drawerManuallyExpanded)
            drawerAutoCollapsed = true;
        else if (ActualWidth >= 1120)
            drawerAutoCollapsed = false;
        UpdateSidebarLayout();
    }

    private void Toast(string message)
    {
        if (closing) return;
        ToastText.Text = message;
        ToastPopup.IsOpen = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void OnRailExpandClick(object sender, RoutedEventArgs e) => OpenDrawer();
    private void OnCollapseClick(object sender, RoutedEventArgs e) => CloseDrawer();
    private void OnHideSidebarClick(object sender, RoutedEventArgs e) => HideSidebar();
    private async void OnAddAccountClick(object sender, RoutedEventArgs e) => await AddAccountAsync();
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(!fullscreen);
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RenderAccounts();
    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        var id = store.GetCurrentId();
        recoveryAttempts.Remove(id);
        if (id > 0) await SwitchTo(id, animate: true, reload: true);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.B && e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !fullscreen)
        {
            if (sidebarHidden) OpenDrawer();
            else HideSidebar();
            e.Handled = true;
        }
        else if (key == Key.F11) { SetFullscreen(!fullscreen); e.Handled = true; }
        else if (key == Key.Escape)
        {
            if (editId > 0) { editId = -1; RenderAccounts(); e.Handled = true; }
            else if (fullscreen) { SetFullscreen(false); e.Handled = true; }
            else if (DrawerContent.Visibility == Visibility.Visible) { CloseDrawer(); e.Handled = true; }
        }
        else if (e.KeyboardDevice.Modifiers == ModifierKeys.Alt && key == Key.Left
            && sessions.Current is { CanGoBack: true } host)
        {
            try { host.GoBack(); e.Handled = true; }
            catch (Exception ex) { ReportOperationFailure("无法返回上一页", ex); }
        }
    }
}
