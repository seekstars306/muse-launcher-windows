using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MuseLauncher.Core;

namespace MuseLauncher;

public partial class App : Application
{
    private Mutex? singleInstanceMutex;
    private bool ownsMutex;
    private CancellationTokenSource? pipeCts;
    private Task? pipeTask;
    private string pipeName = "";
    private readonly List<string[]> pendingInputs = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var identity = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("无法识别当前 Windows 用户");
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            pipeName = "MuseLauncher.Args." + suffix + "." + Process.GetCurrentProcess().SessionId;
            singleInstanceMutex = new Mutex(false, "Local\\MuseLauncher." + suffix);
            try { ownsMutex = singleInstanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                if (!TryForwardArgs(e.Args))
                    MessageBox.Show("已有启动器正在运行，但未能连接。请关闭原窗口后重试。", Strings.AppName);
                Shutdown(0);
                return;
            }

            if (!IsWebView2RuntimeAvailable())
            {
                ShowRuntimeDialog();
                Shutdown(1);
                return;
            }
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            StartPipeServer();
            if (e.Args.Length > 0) pendingInputs.Add(e.Args);
            foreach (var input in pendingInputs) DispatchInput(input);
            pendingInputs.Clear();
        }
        catch (Exception ex)
        {
            Log.I("App", "startup failed: " + ex.GetType().Name);
            MessageBox.Show("启动失败，未创建或覆盖账号数据：\n" + ex.Message, Strings.AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeCts?.Cancel();
        if (ownsMutex)
        {
            try { singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    private static void ShowRuntimeDialog()
    {
        if (MessageBox.Show(Strings.RuntimeNeedMsg, Strings.RuntimeNeedTitle,
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            TryOpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
    }

    public static void TryOpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.I("App", "open URL failed: " + ex.GetType().Name); }
    }

    private bool TryForwardArgs(string[] args)
    {
        try
        {
            var payload = LaunchArgumentsCodec.Encode(args);
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            client.WriteAsync(header, timeout.Token).AsTask().GetAwaiter().GetResult();
            client.WriteAsync(payload, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Log.I("App", "input forwarding failed: " + ex.GetType().Name);
            return false;
        }
    }

    private void StartPipeServer()
    {
        pipeCts = new CancellationTokenSource();
        pipeTask = Task.Run(() => ListenAsync(pipeCts.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await server.ReadExactlyAsync(header, readTimeout.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > LaunchArgumentsCodec.MaxPayloadBytes)
                    throw new InvalidDataException("无效的输入长度");
                var payload = new byte[length];
                await server.ReadExactlyAsync(payload, readTimeout.Token);
                var args = LaunchArgumentsCodec.Decode(payload);
                await Dispatcher.InvokeAsync(() => DispatchInput(args));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) return;
                Log.I("App", "input receiver failed: " + ex.GetType().Name);
                try { await Task.Delay(100, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void DispatchInput(string[] args)
    {
        Dispatcher.VerifyAccess();
        if (MainWindow is not MainWindow window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.HandleLaunchCommands(args);
    }
}
