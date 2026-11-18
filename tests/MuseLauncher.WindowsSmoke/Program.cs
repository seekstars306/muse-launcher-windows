using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using MuseLauncher;
using MuseLauncher.Core;

namespace MuseLauncher.WindowsSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var uiOnly = args.Length == 1 && args[0] == "--ui-only";
        if (args.Length > 0 && !uiOnly)
        {
            Console.Error.WriteLine("Usage: MuseLauncher.WindowsSmoke [--ui-only]");
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine(uiOnly
                ? "SKIPPED: --ui-only 需要 Windows/WPF，不需要 WebView2 Runtime。"
                : "SKIPPED: 此入口需要 Windows 和 WebView2 Evergreen Runtime。");
            return 2;
        }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 1;
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.Startup += async (_, _) =>
        {
            try
            {
                if (uiOnly) await AvatarNavigationSmoke.RunAsync();
                else await RunAsync();
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); }
            finally { result.TrySetResult(exitCode); application.Shutdown(exitCode); }
        };
        application.Run();
        return result.Task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        // 每轮新目录，绝不读取用户账号/Profile。仅使用本机合成代理，不访问 muse.ai。
        var root = Path.Combine(Path.GetTempPath(), "MuseLauncherSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var upstreamA = new MockHttpProxy("alpha", "alice:first");
        await using var upstreamB = new MockHttpProxy("beta", "bob:second");
        var window = new MainWindow(Path.Combine(root, "Metadata"), Path.Combine(root, "Profiles"),
            "http://smoke.invalid/account") { Width = 1100, Height = 760 };
        window.Show();
        try
        {
            var column = (System.Windows.Controls.ColumnDefinition)window.FindName("SidebarCol");
            ((System.Windows.Controls.Button)window.FindName("BtnRailExpand")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (column.Width.Value != 274) throw new InvalidOperationException("侧栏未展开");
            ((System.Windows.Controls.Button)window.FindName("BtnCollapse")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (column.Width.Value != 56) throw new InvalidOperationException("侧栏未收起");
            ((System.Windows.Controls.Button)window.FindName("BtnFullscreen")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (column.Width.Value != 0) throw new InvalidOperationException("全屏未隐藏侧栏");
            ((System.Windows.Controls.Button)window.FindName("BtnFullscreen")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (column.Width.Value != 56) throw new InvalidOperationException("退出全屏未恢复侧栏");
            Console.WriteLine("PASS: sidebar expand/collapse and full-screen layout");
            var accounts = new List<Account>();
            for (var index = 0; index < 5; index++)
            {
                var gateway = index == 1 ? upstreamB : upstreamA;
                var credentials = index == 1 ? "bob:second" : "alice:first";
                var account = window.Accounts.AddDefault("http://" + credentials + "@127.0.0.1:" + gateway.Port);
                accounts.Add(account);
                await window.ActivateAccountAsync(account.Id);
                var host = await ReadyHostAsync(window, account.Id);
                var marker = index == 1 ? "beta" : "alpha";
                await AssertPageAsync(host, marker);
                if (window.HostedSessions.Count > 4 || window.HostedControlCount > 4)
                    throw new InvalidOperationException("LRU 未释放真实 WebView 控件");
                Console.WriteLine($"PASS: account {account.Id}, marker {marker}, controls {window.HostedControlCount}");
                if (index == 0)
                    await host.Core.ExecuteScriptAsync("localStorage.setItem('smoke-isolation','first');");
                if (index == 1)
                {
                    var value = await host.Core.ExecuteScriptAsync("localStorage.getItem('smoke-isolation')");
                    if (value != "null") throw new InvalidOperationException("账号存储发生串号");
                    Console.WriteLine("PASS: independent localStorage");
                }
            }
            await window.ActivateAccountAsync(accounts[0].Id);
            var restored = await ReadyHostAsync(window, accounts[0].Id);
            var stored = await restored.Core.ExecuteScriptAsync("localStorage.getItem('smoke-isolation')");
            if (stored != "\"first\"") throw new InvalidOperationException("LRU 重建未恢复账号存储");
            Console.WriteLine("PASS: eviction and profile restoration");

            upstreamA.ExpectedCredentials = "alice:changed";
            var updated = window.Accounts.FindById(accounts[0].Id)!;
            updated.Proxy = "http://alice:changed@127.0.0.1:" + upstreamA.Port;
            window.Accounts.Update(updated);
            await window.ApplySavedConfigurationAsync(updated.Id);
            var recreated = await ReadyHostAsync(window, updated.Id);
            if (ReferenceEquals(restored, recreated)) throw new InvalidOperationException("更换账密没有重建环境");
            await AssertPageAsync(recreated, "alpha");
            if (upstreamA.LastCredentials != "alice:changed") throw new InvalidOperationException("仍使用旧账密");
            Console.WriteLine("PASS: same gateway credentials changed and browser recreated");

            var invalid = window.Accounts.FindById(accounts[1].Id)!;
            invalid.Proxy = "http://user:password@";
            window.Accounts.Update(invalid);
            await window.ActivateAccountAsync(invalid.Id);
            if (window.DisplayState.CanShowWebView || window.DisplayState.AccountId != invalid.Id)
                throw new InvalidOperationException("创建失败显示了其他账号");
            invalid.Proxy = "http://bob:second@127.0.0.1:" + upstreamB.Port;
            window.Accounts.Update(invalid);
            ((System.Windows.Controls.Button)window.FindName("BtnRetry")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await AssertPageAsync(await ReadyHostAsync(window, invalid.Id), "beta");
            Console.WriteLine("PASS: creation failure and retry remain on selected account");
            Console.WriteLine("PASS: Windows WebView2 smoke completed without real accounts or external targets");
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(80));
            if (window.HostedControlCount != 0) throw new InvalidOperationException("关闭后仍有 WebView 控件");
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { Console.WriteLine("NOTE: 运行时暂占用合成目录，保留供诊断：" + root); }
        }
    }

    private static async Task<AccountWebHost> ReadyHostAsync(MainWindow window, long id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (window.HostedSessions.TryGetValue(id, out var host) && host.Loaded && !host.Errored) return host;
            await Task.Delay(100);
        }
        throw new TimeoutException("等待测试页失败：" + window.DisplayState.Error);
    }

    private static async Task AssertPageAsync(AccountWebHost host, string marker)
    {
        var text = await host.Core.ExecuteScriptAsync("document.body.textContent");
        if (!text.Contains(marker, StringComparison.Ordinal)) throw new InvalidOperationException("代理路由标记错误：" + text);
    }

    private sealed class MockHttpProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly List<Task> clients = new();
        private readonly Task acceptTask;
        private readonly string marker;
        public int Port { get; }
        public string ExpectedCredentials { get; set; }
        public string? LastCredentials { get; private set; }

        public MockHttpProxy(string marker, string credentials)
        {
            this.marker = marker;
            ExpectedCredentials = credentials;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptTask = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    var task = ServeAsync(client);
                    lock (clients) clients.Add(task);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var bytes = new List<byte>();
                    var one = new byte[1];
                    while (bytes.Count < 16384)
                    {
                        if (await stream.ReadAsync(one, deadline.Token) == 0) return;
                        bytes.Add(one[0]);
                        if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var header = Encoding.ASCII.GetString(bytes.ToArray());
                    var auth = header.Split("\r\n").FirstOrDefault(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                    var token = auth?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
                    LastCredentials = token == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(token));
                    var valid = LastCredentials == ExpectedCredentials;
                    var body = valid ? "<!doctype html><body>" + marker + "</body>" : "rejected";
                    var response = valid ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\n";
                    var payload = Encoding.UTF8.GetBytes(body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "Content-Type: text/html; charset=utf-8\r\nContent-Length: "
                        + payload.Length + "\r\nConnection: close\r\n\r\n"), deadline.Token);
                    await stream.WriteAsync(payload, deadline.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or FormatException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();
            await acceptTask;
            Task[] pending;
            lock (clients) pending = clients.ToArray();
            await Task.WhenAll(pending);
            shutdown.Dispose();
        }
    }
}
