using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MuseLauncher;

public partial class CredentialsWindow : Window
{
    private readonly Func<string, string, IProgress<string>, Task<string>> export;
    private readonly Func<string, string, IProgress<string>, Task<string>> import;
    private bool busy;

    internal CredentialsWindow(
        Func<string, string, IProgress<string>, Task<string>> export,
        Func<string, string, IProgress<string>, Task<string>> import,
        bool canExport)
    {
        this.export = export;
        this.import = import;
        InitializeComponent();
        ExportMode.IsEnabled = canExport;
        if (!canExport) ImportMode.IsChecked = true;
        Closing += OnClosing;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (RunButton == null) return;
        var exporting = ExportMode.IsChecked == true;
        ConfirmPanel.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Text = exporting ? "设置文件密码（至少 8 个字符）" : "文件密码";
        RunButton.Content = exporting ? "选择文件并导出" : "选择文件并导入";
        Status.Text = "";
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var exporting = ExportMode.IsChecked == true;
        var passphrase = PasswordInput.Password;
        if (passphrase.Length == 0 || (exporting && passphrase.Length < 8))
        {
            Status.Text = exporting ? "请设置至少 8 个字符的密码。" : "请输入文件密码。";
            return;
        }
        if (exporting && passphrase != ConfirmInput.Password)
        {
            Status.Text = "两次输入的密码不一致。";
            return;
        }
        string path;
        if (exporting)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出加密凭证", Filter = "Muse 加密凭证|*.musecred", DefaultExt = ".musecred",
                FileName = "Muse-accounts-" + DateTime.Now.ToString("yyyyMMdd") + ".musecred",
            };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入加密凭证", Filter = "Muse 加密凭证|*.musecred", CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        await RunAsync(exporting, path, passphrase);
    }

    internal async Task RunAsync(bool exporting, string path, string passphrase)
    {
        if (busy) return;
        busy = true;
        var acceptingProgress = true;
        ModePanel.IsEnabled = RunButton.IsEnabled = CloseButton.IsEnabled = false;
        PasswordInput.IsEnabled = ConfirmInput.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Status.Text = exporting ? "正在收集账号凭证…" : "正在解密并验证文件…";
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (acceptingProgress) Status.Text = message;
            });
            Status.Text = await (exporting ? export : import)(path, passphrase, progress);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            Status.Text = "未完成：" + ex.Message;
        }
        catch (Exception)
        {
            // 文件路径、代理和 Cookie 不进入日志或异常详情。
            Status.Text = "未完成。请检查文件权限、磁盘空间或 WebView2 环境后重试；现有账号已保留。";
        }
        finally
        {
            // 每轮独立失效，避免排队或迟到的进度覆盖最终结果或下一轮状态。
            acceptingProgress = false;
            passphrase = "";
            PasswordInput.Clear();
            ConfirmInput.Clear();
            busy = false;
            ModePanel.IsEnabled = RunButton.IsEnabled = CloseButton.IsEnabled = true;
            PasswordInput.IsEnabled = ConfirmInput.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) e.Cancel = true;
        else { PasswordInput.Clear(); ConfirmInput.Clear(); }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!busy) Close();
    }
}
