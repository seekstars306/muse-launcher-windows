using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>配置采用独立草稿，保存失败不会修改正在运行的账号。</summary>
public partial class AccountConfigWindow : Window
{
    private static readonly Uri TestUri = new("https://www.gstatic.com/generate_204");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private readonly Account original;
    private readonly Action<Account> save;
    private readonly ThemePalette palette;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> importedTempAvatars = new();
    private string draftAvatarImage;
    private bool testing;
    private bool detecting;

    public bool Saved { get; private set; }
    public bool ProxyChanged { get; private set; }

    public AccountConfigWindow(Account account, Action<Account> save, ThemePalette? palette = null)
    {
        original = account.Clone();
        this.save = save;
        this.palette = palette ?? ThemePalette.Dark;
        draftAvatarImage = account.AvatarImage ?? "";
        InitializeComponent();
        ApplyTheme();

        AvatarInput.Text = account.Avatar;
        NameInput.Text = account.Name;
        ProxyInput.Text = account.Proxy;
        ForceProxyCheck.IsChecked = account.ForceProxy;
        TagsInput.Text = string.Join(", ", account.Tags);
        UpdateTile();
        Loaded += (_, _) => NameInput.Focus();
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            CleanupUnusedImportedAvatars();
        };
    }

    private void ApplyTheme()
    {
        Background = palette.DrawerBg;
        foreach (var label in new[] { LblAvatar, LblName, LblProxy, LblTags })
            label.Foreground = palette.Foreground;
        foreach (var hint in new[] { HintAvatar, HintProxy1, HintProxy2, HintTags, TestStatus })
            hint.Foreground = palette.MutedText;
        ForceProxyCheck.Foreground = palette.SecondaryText;

        foreach (var input in new[] { AvatarInput, NameInput, ProxyInput, TagsInput })
        {
            input.Foreground = palette.Foreground;
            input.CaretBrush = palette.Foreground;
            input.Background = palette.InputBg;
            input.BorderBrush = palette.BorderBrush;
        }
        foreach (var btn in new[] { UploadAvatarBtn, ClearAvatarBtn, DetectBtn, TestBtn, ShortcutBtn, CancelBtn })
        {
            btn.Background = palette.ButtonBg;
            btn.Foreground = palette.Foreground;
        }
        SaveBtn.Background = palette.Accent;
    }

    private static string LimitText(string text, int length)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var elements = new List<string>();
        while (elements.Count < length && enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return string.Concat(elements);
    }

    private void UpdateTile()
    {
        if (TilePreviewHost == null) return;
        var avatar = LimitText(AvatarInput.Text.Trim(), 2);
        TilePreviewHost.Child = AvatarStore.MakeAvatarTile(48, string.IsNullOrEmpty(avatar) ? "m" : avatar, draftAvatarImage, palette);
        ClearAvatarBtn.Visibility = string.IsNullOrEmpty(draftAvatarImage) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAvatarChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTile();

    private void OnUploadAvatarClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择账号头像图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var fileName = AvatarStore.ImportImage(original.Id, dialog.FileName);
            importedTempAvatars.Add(fileName);
            draftAvatarImage = fileName;
            UpdateTile();
            TestStatus.Text = "已导入头像图片（点击保存生效）";
        }
        catch (Exception ex)
        {
            TestStatus.Text = "导入头像失败：" + ex.Message;
            Log.I("Avatar", "import failed: " + ex.GetType().Name);
        }
    }

    private void OnClearAvatarClick(object sender, RoutedEventArgs e)
    {
        draftAvatarImage = "";
        UpdateTile();
        TestStatus.Text = "已切换回文字头像（点击保存生效）";
    }

    private void OnCreateShortcutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var preview = original.Clone();
            var avatar = LimitText(AvatarInput.Text.Trim(), 2);
            preview.Avatar = string.IsNullOrEmpty(avatar) ? "m" : avatar;
            preview.AvatarImage = draftAvatarImage;
            var name = LimitText(NameInput.Text.Trim(), 16);
            if (!string.IsNullOrEmpty(name)) preview.Name = name;

            var path = DesktopShortcutHelper.CreateAccountShortcut(preview, palette);
            TestStatus.Text = "已创建桌面快捷方式：" + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            TestStatus.Text = "创建快捷方式失败：" + ex.Message;
            Log.I("Shortcut", "create failed: " + ex.GetType().Name);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var draft = original.Clone();
            var avatar = LimitText(AvatarInput.Text.Trim(), 2);
            draft.Avatar = string.IsNullOrEmpty(avatar) ? "m" : avatar;
            draft.AvatarImage = draftAvatarImage;
            var name = LimitText(NameInput.Text.Trim(), 16);
            if (!string.IsNullOrEmpty(name)) draft.Name = name;
            draft.Proxy = ProxyInput.Text.Trim();
            draft.ForceProxy = ForceProxyCheck.IsChecked == true;
            var configuration = ProxyConfiguration.Parse(draft.Proxy);
            if (draft.ForceProxy && configuration == null)
                throw new FormatException("启用代理绕过控制时必须填写代理，不能使用空地址");
            draft.Tags = TagsInput.Text.Split(',', '，').Select(t => LimitText(t.Trim(), 8))
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // 账密与用户名中的 country/session 同样属于生效配置，不能只比较网关地址。
            ProxyChanged = draft.Proxy != original.Proxy || draft.ForceProxy != original.ForceProxy;
            save(draft);

            // 旧图片已被替换或清除时清理磁盘残留
            if (!string.IsNullOrEmpty(original.AvatarImage)
                && !string.Equals(original.AvatarImage, draft.AvatarImage, StringComparison.OrdinalIgnoreCase))
            {
                AvatarStore.DeleteImage(original.AvatarImage);
            }

            Saved = true;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or InvalidOperationException or ArgumentException)
        {
            TestStatus.Text = "未保存：" + ex.Message;
        }
    }

    private void CleanupUnusedImportedAvatars()
    {
        var keep = Saved ? draftAvatarImage : original.AvatarImage;
        foreach (var file in importedTempAvatars)
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                AvatarStore.DeleteImage(file);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (testing) return;
        testing = true;
        TestBtn.IsEnabled = false;
        TestStatus.Text = Strings.ConfigProxyTesting;
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TestTimeout);
        try
        {
            var proxy = ProxyConfiguration.Parse(ProxyInput.Text.Trim())
                ?? throw new FormatException(Strings.ConfigProxyEmpty);
            // 与浏览器走完全相同的桥接/认证路径，SOCKS 测活不再只检查 TCP 端口。
            await using var bridge = await LoopbackProxyBridge.StartAsync(proxy, timeout.Token);
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy(bridge.Address)
                {
                    Credentials = new NetworkCredential(bridge.Username, bridge.Password),
                },
                AllowAutoRedirect = false,
            };
            using var client = new HttpClient(handler) { Timeout = TestTimeout };
            using var response = await client.GetAsync(TestUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            TestStatus.Text = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
                ? $"已通过代理协议及 HTTPS 请求验证（{started.ElapsedMilliseconds} ms）"
                : "目标返回 HTTP " + (int)response.StatusCode;
        }
        catch (OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) TestStatus.Text = Strings.ConfigProxyTimeout;
        }
        catch (Exception ex)
        {
            // 不拼入原始地址，避免将用户输入的账密记入错误或日志。
            TestStatus.Text = ex is FormatException ? ex.Message
                : "代理请求失败。请检查协议、账密、证书及目标可达性。";
            Log.I("MuseProxy", "probe failed: " + ex.GetType().Name);
        }
        finally
        {
            testing = false;
            if (!lifetime.IsCancellationRequested) TestBtn.IsEnabled = true;
        }
    }

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        detecting = true;
        DetectBtn.IsEnabled = false;
        TestStatus.Text = Strings.ConfigProxyDetecting;
        try
        {
            var port = await Task.Run(ProxyUtil.ProbeLocalProxy, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (port > 0)
            {
                ProxyInput.Text = ProxyUtil.ProxyUrlForPort(port);
                TestStatus.Text = string.Format(Strings.ConfigProxyDetectFound, port);
            }
            else TestStatus.Text = Strings.ConfigProxyDetectNone;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested) TestStatus.Text = "本机端口探测失败";
            Log.I("MuseProxy", "port probe failed: " + ex.GetType().Name);
        }
        finally
        {
            detecting = false;
            if (!lifetime.IsCancellationRequested) DetectBtn.IsEnabled = true;
        }
    }
}
