using System.Diagnostics;
using System.IO;
using System.Windows;
using MuseLauncher.Core;

namespace MuseLauncher;

public partial class UpdateDialogWindow : Window
{
    private readonly AppUpdateHelper.ReleaseInfo release;
    private readonly string? accountProxy;
    private readonly string downloadDir;
    private CancellationTokenSource? downloadCts;
    private string? downloadedFilePath;

    public UpdateDialogWindow(
        AppUpdateHelper.ReleaseInfo release,
        string currentVersion,
        string? accountProxy,
        string? customDownloadDir,
        ThemePalette palette)
    {
        this.release = release;
        this.accountProxy = accountProxy;
        downloadDir = !string.IsNullOrWhiteSpace(customDownloadDir) && Directory.Exists(customDownloadDir)
            ? customDownloadDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        InitializeComponent();
        ApplyTheme(palette);

        TitleLabel.Text = string.IsNullOrWhiteSpace(release.Title) ? $"发现新版本 {release.TagName}" : release.Title;
        var sizeSuffix = release.AssetSizeBytes > 0 ? $" · {release.AssetSizeBytes / (1024.0 * 1024.0):F1} MB" : "";
        VersionLabel.Text = $"当前版本：v{AppUpdateHelper.NormalizeVersion(currentVersion)}  →  最新版本：v{release.VersionName}{sizeSuffix}";
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? "包含体验优化与问题修复。" : release.Notes;
        if (string.IsNullOrWhiteSpace(release.AssetUrl))
            BtnDownload.Visibility = Visibility.Collapsed;

        Closed += (_, _) => downloadCts?.Cancel();
    }

    private void ApplyTheme(ThemePalette palette)
    {
        Background = palette.DrawerBg;
        TitleLabel.Foreground = palette.Foreground;
        VersionLabel.Foreground = palette.MutedText;
        NotesCard.Background = palette.InputBg;
        NotesCard.BorderBrush = palette.BorderBrush;
        NotesText.Foreground = palette.Foreground;
        StatusText.Foreground = palette.MutedText;
        DownloadProgress.Foreground = palette.Accent;
        DownloadProgress.Background = palette.BorderBrush;
        BtnClose.Background = palette.ButtonBg;
        BtnClose.Foreground = palette.Foreground;
        BtnOpenWeb.Background = palette.ButtonBg;
        BtnOpenWeb.Foreground = palette.Foreground;
        BtnDownload.Background = palette.Accent;
    }

    private void OnOpenWebClick(object sender, RoutedEventArgs e)
    {
        App.TryOpenUrl(release.HtmlUrl);
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(downloadedFilePath) && File.Exists(downloadedFilePath))
        {
            OpenContainingFolder(downloadedFilePath);
            return;
        }
        if (downloadCts != null) return;

        downloadCts = new CancellationTokenSource();
        BtnDownload.IsEnabled = false;
        BtnClose.Content = "取消下载";
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = "正在连接下载（自动复用账号代理）…";

        var progress = new Progress<(long Downloaded, long Total)>(p =>
        {
            if (p.Total > 0)
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = Math.Clamp(p.Downloaded * 100.0 / p.Total, 0, 100);
                StatusText.Text = $"已下载 {p.Downloaded / (1024.0 * 1024.0):F1} MB / {p.Total / (1024.0 * 1024.0):F1} MB";
            }
            else
            {
                DownloadProgress.IsIndeterminate = true;
                StatusText.Text = $"已下载 {p.Downloaded / (1024.0 * 1024.0):F1} MB…";
            }
        });

        try
        {
            var path = await UpdateService.DownloadAssetAsync(release, downloadDir, accountProxy, progress, downloadCts.Token);
            downloadedFilePath = path;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 100;
            StatusText.Text = $"下载完成：{path}（退出当前启动器后解压覆盖即可更新）";
            BtnDownload.Content = "打开所在目录";
            BtnDownload.IsEnabled = true;
            BtnClose.Content = "关闭";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载";
            BtnDownload.IsEnabled = true;
            BtnClose.Content = "稍后";
        }
        catch (Exception ex)
        {
            StatusText.Text = "下载失败：" + ex.Message;
            BtnDownload.IsEnabled = true;
            BtnClose.Content = "关闭";
        }
        finally
        {
            downloadCts?.Dispose();
            downloadCts = null;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (downloadCts != null)
        {
            downloadCts.Cancel();
            return;
        }
        Close();
    }

    private static void OpenContainingFolder(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.I("Update", "open folder failed: " + ex.GetType().Name);
        }
    }
}
