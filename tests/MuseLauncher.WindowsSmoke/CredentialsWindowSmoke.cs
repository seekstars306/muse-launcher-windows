using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MuseLauncher;

namespace MuseLauncher.WindowsSmoke;

internal static class CredentialsWindowSmoke
{
    internal static async Task RunAsync()
    {
        Assert(OperatingSystem.IsWindows(), "Credentials window smoke requires Windows/WPF");
        var application = Application.Current;
        Assert(application is not null && application is not App,
            "Run from the smoke STA Application; do not start the production App");
        if (application is null || application is App)
            throw new InvalidOperationException("Credentials window smoke requires its isolated WPF host");
        application.Dispatcher.VerifyAccess();
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            && SynchronizationContext.Current is DispatcherSynchronizationContext,
            "Credentials window smoke requires a WPF STA Dispatcher synchronization context");

        const string path = "synthetic.musecred";
        const string passphrase = "synthetic-window-passphrase";
        const string currentMessage = "synthetic current progress";
        const string success = "synthetic import succeeded";
        Exception failure = new InvalidDataException("synthetic invalid archive");
        IProgress<string>? oldProgress = null;
        IProgress<string>? currentProgress = null;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new CredentialsWindow(
            (_, _, progress) =>
            {
                oldProgress = progress;
                progress.Report("synthetic queued failure progress");
                throw failure;
            },
            async (_, _, progress) =>
            {
                currentProgress = progress;
                progress.Report(currentMessage);
                await complete.Task;
                progress.Report("synthetic queued success progress");
                return success;
            },
            canExport: true);
        var status = (TextBlock)window.FindName("Status");
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Task? running = null;
        try
        {
            window.Show();
            await window.RunAsync(true, path, passphrase);
            await DrainDispatcherAsync(window.Dispatcher);
            Assert(status.Text == "未完成：synthetic invalid archive",
                "Queued progress overwrote the final archive error");

            failure = new InvalidOperationException("synthetic private error detail");
            await window.RunAsync(true, path, passphrase);
            await DrainDispatcherAsync(window.Dispatcher);
            Assert(status.Text == "未完成。请检查文件权限、磁盘空间或 WebView2 环境后重试；现有账号已保留。",
                "Queued progress overwrote the final generic error or exposed exception details");
            Console.WriteLine("PASS: synchronous reports followed by synchronous failure retain both final error messages");

            running = window.RunAsync(false, path, passphrase);
            Assert(!running.IsCompleted && oldProgress is not null && currentProgress is not null,
                "The next operation must remain asynchronous with both progress reporters captured");
            oldProgress!.Report("synthetic late progress from the previous operation");
            await DrainDispatcherAsync(window.Dispatcher);
            Assert(status.Text == currentMessage,
                "A previous operation's delayed progress polluted the next operation");

            complete.TrySetResult();
            await running;
            await DrainDispatcherAsync(window.Dispatcher);
            Assert(status.Text == success, "Queued progress overwrote the final asynchronous success");

            oldProgress!.Report("synthetic old progress after success");
            currentProgress!.Report("synthetic late progress after success");
            await DrainDispatcherAsync(window.Dispatcher);
            Assert(status.Text == success, "Late progress overwrote the completed operation's final success");
            Console.WriteLine("PASS: asynchronous success survives queued progress and previous-operation reports");
        }
        finally
        {
            complete.TrySetResult();
            try
            {
                if (running is not null) await running;
            }
            finally
            {
                window.Close();
                Assert(closed, "Credentials window did not close after the synthetic operation completed");
            }
        }
        Console.WriteLine("PASS: credentials window smoke closed without file dialogs, files, accounts, or WebView");
    }

    private static async Task DrainDispatcherAsync(Dispatcher dispatcher)
    {
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
