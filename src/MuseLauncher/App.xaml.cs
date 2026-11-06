using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MuseLauncher.Core;

namespace MuseLauncher;

public partial class App : Application
{
    private static Mutex? singleInstanceMutex;
    private const string MutexName = "Local\\MuseLauncher.SingleInstance";
    private const string PipeName = "Local\\MuseLauncher.ArgsPipe";
    private static CancellationTokenSource? pipeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstanceMutex = new Mutex(true, MutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            // 已有实例：转发参数后退出（对应安卓 launchMode 单实例 + onNewIntent）
            TryForwardArgs(e.Args);
            Shutdown(0);
            return;
        }

        StartPipeServer();

        // WebView2 运行时缺失 → 引导安装（对应安卓 showWebViewUpgradeDialog）
        if (!IsWebView2RuntimeAvailable())
        {
            ShowRuntimeDialog();
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

#if DEBUG
        // 调试/自动化参数仅 Debug 构建响应（对应 BuildConfig.DEBUG 门控）
        if (e.Args.Length > 0)
        {
            window.Dispatcher.BeginInvoke(() => window.HandleLaunchCommands(e.Args));
        }
#endif
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeCts?.Cancel();
        try { singleInstanceMutex?.ReleaseMutex(); } catch { }
        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch
        {
            return false;
        }
    }

    private static void ShowRuntimeDialog()
    {
        var result = MessageBox.Show(
            Strings.RuntimeNeedMsg,
            Strings.RuntimeNeedTitle,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.OK);
        if (result == MessageBoxResult.OK)
        {
            TryOpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        }
    }

    public static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.I("App", $"open url failed: {url} {ex.Message}");
        }
    }

    // ---------- 单实例参数转发（≈ onNewIntent） ----------

    private static void TryForwardArgs(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            client.Write(payload, 0, payload.Length);
            client.Flush();
        }
        catch
        {
            // 首实例不可达：静默退出
        }
    }

    private static void StartPipeServer()
    {
        pipeCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!pipeCts.IsCancellationRequested)
            {
                string? payload = null;
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(pipeCts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    payload = await reader.ReadToEndAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // 一轮失败后稍等重来
                    try { await Task.Delay(500, pipeCts.Token); } catch (OperationCanceledException) { return; }
                    continue;
                }

                if (string.IsNullOrEmpty(payload)) continue;
                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
#if DEBUG
                var window = Current?.MainWindow as MainWindow;
                window?.Dispatcher.BeginInvoke(() => window.HandleLaunchCommands(args));
#endif
            }
        });
    }
}
