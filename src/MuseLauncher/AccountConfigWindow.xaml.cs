using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Net;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Net.Http;
using System.Windows;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>配置采用独立草稿，保存失败不会修改正在运行的账号。</summary>
public partial class AccountConfigWindow : Window
{
    private static readonly Uri TestUri = new("https://www.gstatic.com/generate_204");
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private readonly Account original;
    private readonly Action<Account> save;
    private readonly CancellationTokenSource lifetime = new();
    private readonly long accountId;
    private readonly string avatarDirectory;
    private bool testing;
    private bool detecting;
    private string avatarImagePath = "";

    public bool Saved { get; private set; }
    public bool ProxyChanged { get; private set; }

    public AccountConfigWindow(Account account, Action<Account> save, string? avatarDirectory = null)
    {
        original = account.Clone();
        accountId = account.Id;
        this.avatarDirectory = Path.GetFullPath(avatarDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MuseLauncher", "Avatars"));
        this.save = save;
        InitializeComponent();
        AvatarInput.Text = account.Avatar;
        LoadAvatarImage(account.AvatarImage);
        NameInput.Text = account.Name;
        ProxyInput.Text = account.Proxy;
        ForceProxyCheck.IsChecked = account.ForceProxy;
        TagsInput.Text = string.Join(", ", account.Tags);
        UpdateTile();
        Loaded += (_, _) => NameInput.Focus();
        Closed += (_, _) => lifetime.Cancel();
    }

    private static string LimitText(string text, int length)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var elements = new List<string>();
        while (elements.Count < length && enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return string.Concat(elements);
    }

    private void LoadAvatarImage(string path)
    {
        avatarImagePath = path;
        AvatarImagePreview.Source = null;
        if (avatarImagePath.Length > 0)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128;
                using var stream = File.OpenRead(avatarImagePath);
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                AvatarImagePreview.Source = image;
            }
            catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException or NotSupportedException
                or InvalidOperationException or ArgumentException or FormatException or OutOfMemoryException)
            {
                // 暂时不可读时保留路径，避免仅编辑名称就清掉原头像配置。
            }
        }
        AvatarImagePreview.Visibility = AvatarImagePreview.Source == null
            ? Visibility.Collapsed : Visibility.Visible;
        TileText.Visibility = AvatarImagePreview.Source == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTile()
    {
        if (TileText == null) return;
        var avatar = LimitText(AvatarInput.Text.Trim(), 2);
        TileText.Text = string.IsNullOrEmpty(avatar) ? "m" : avatar;
    }

    private void OnAvatarChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTile();

    private void OnChooseAvatarClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择账号头像",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
                    throw new InvalidDataException("请选择 PNG、JPG、BMP 或 GIF 图片");
                if (new FileInfo(dialog.FileName).Length > 5 * 1024 * 1024)
                    throw new InvalidDataException("头像文件不能超过 5 MB");
                var previous = avatarImagePath;
                LoadAvatarImage(dialog.FileName);
                if (AvatarImagePreview.Source == null)
                {
                    LoadAvatarImage(previous);
                    TestStatus.Text = "无法读取头像图片：文件损坏或格式不受支持";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TestStatus.Text = "无法读取头像图片：" + ex.Message;
            }
        }
    }

    private void OnResetAvatarClick(object sender, RoutedEventArgs e)
    {
        AvatarInput.Text = "m";
        LoadAvatarImage("");
    }

    private string StoreAvatarImage(out string? createdPath)
    {
        createdPath = null;
        if (avatarImagePath.Length == 0) return "";
        if (avatarImagePath == original.AvatarImage) return original.AvatarImage;
        var source = Path.GetFullPath(avatarImagePath);
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
            throw new InvalidDataException("请选择 PNG、JPG、BMP 或 GIF 图片");
        var info = new FileInfo(source);
        if (!info.Exists || info.Length > 5 * 1024 * 1024)
            throw new InvalidDataException("头像文件不存在或超过 5 MB");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        Directory.CreateDirectory(avatarDirectory);
        var destination = Path.Combine(avatarDirectory, accountId + "-" + hash + extension);
        if (!File.Exists(destination))
        {
            File.Copy(source, destination);
            createdPath = destination;
        }
        return destination;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string? createdAvatar = null;
        try
        {
            var draft = original.Clone();
            var avatar = LimitText(AvatarInput.Text.Trim(), 2);
            draft.Avatar = string.IsNullOrEmpty(avatar) ? "m" : avatar;
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
            draft.AvatarImage = StoreAvatarImage(out createdAvatar);
            save(draft);
            Saved = true;
            // 旧头像仍可能被账号备份引用，保留以支持恢复。
            createdAvatar = null;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
            or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            TestStatus.Text = "未保存：" + ex.Message;
        }
        finally
        {
            if (createdAvatar != null)
            {
                try { File.Delete(createdAvatar); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TestStatus.Text += "（未使用的头像文件暂时无法清理）";
                }
            }
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
