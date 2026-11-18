using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MuseLauncher;
using MuseLauncher.Core;

namespace MuseLauncher.WindowsSmoke;

/// <summary>Run serially on the smoke host's STA Application, never on the production App.</summary>
internal static class AccountOrderSmoke
{
    private const string AccountOrderFormat = "MuseLauncher.AccountOrder";
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync()
    {
        Assert(OperatingSystem.IsWindows(), "Account order smoke requires Windows/WPF");
        var application = Application.Current;
        Assert(application is not null && application is not App,
            "Use an isolated System.Windows.Application, not the production App");
        if (application is null || application is App)
            throw new InvalidOperationException("An isolated WPF smoke host is required");
        application.Dispatcher.VerifyAccess();
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "Account order smoke must run serially on the WPF STA thread");
        Assert(application.ShutdownMode == ShutdownMode.OnExplicitShutdown,
            "The smoke host must survive closing its only window");

        var root = Path.Combine(Path.GetTempPath(), "MuseLauncherAccountOrderSmoke-" + Guid.NewGuid().ToString("N"));
        var metadata = Path.Combine(root, "Metadata");
        var profiles = Path.Combine(root, "Profiles");
        var accountsPath = Path.Combine(metadata, "accounts.json");
        MainWindow? window = null;
        var closed = false;
        Directory.CreateDirectory(root);
        try
        {
            // Configure before constructing any window; do not query the production log path.
            Log.ConfigureDirectoryForTests(Path.Combine(root, "log"));
            Directory.CreateDirectory(metadata);
            Directory.CreateDirectory(profiles);
            window = new MainWindow(metadata, profiles, "about:blank") { Width = 1200, Height = 760 };
            window.Closed += (_, _) => closed = true;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Loaded += (_, _) =>
            {
                try
                {
                    Assert(window.Accounts.All().Count == 0 && window.Accounts.GetCurrentId() == -1,
                        "MainWindow Loaded must see an empty synthetic store to prevent automatic navigation");
                    AssertNoBrowser(window);
                    loaded.TrySetResult();
                }
                catch (Exception ex) { loaded.TrySetException(ex); }
            };
            window.Show();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await FlushAsync(window);
            Assert(Panel(window, "AccountList").Children.Count == 0
                && Panel(window, "RailAvatars").Children.Count == 0, "Loaded must render empty lists");

            // Add only after Loaded. Store operations do not invoke SwitchTo/ActivateAccountAsync.
            var a = AddSynthetic(window, profiles, "A");
            var b = AddSynthetic(window, profiles, "B");
            var c = AddSynthetic(window, profiles, "C");
            window.Accounts.SetCurrent(b.Id);
            var current = b.Id;
            var profileEntries = Directory.GetFileSystemEntries(profiles, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Render(window);
            await AssertBothLayoutsAsync(window, current, a, b, c);

            // Exercise the actual internal UI entry point in both directions, not AccountsStore.Move.
            window.MoveAccount(c.Id, a.Id, after: false);
            await AssertBothLayoutsAsync(window, current, c, a, b);
            window.MoveAccount(c.Id, b.Id, after: true);
            await AssertBothLayoutsAsync(window, current, a, b, c);
            window.MoveAccount(a.Id, b.Id, after: true);
            var expected = new[] { b, a, c };
            await AssertBothLayoutsAsync(window, current, expected);
            Console.WriteLine("PASS: actual MoveAccount inserts before/after and synchronizes drawer/rail without selecting an account");

            var primary = Snapshot(accountsPath);
            var backup = Snapshot(accountsPath + ".bak");
            window.MoveAccount(a.Id, b.Id, after: true);
            await AssertBothLayoutsAsync(window, current, expected);
            AssertUnchanged(accountsPath, primary, "Repeating the same move");
            AssertUnchanged(accountsPath + ".bak", backup, "Repeating the same move (backup)");
            window.MoveAccount(b.Id, b.Id, after: false);
            await AssertBothLayoutsAsync(window, current, expected);
            AssertUnchanged(accountsPath, primary, "Moving onto itself");
            AssertUnchanged(accountsPath + ".bak", backup, "Moving onto itself (backup)");
            Console.WriteLine("PASS: repeated move and self move preserve order, current, file bytes, and write timestamps");

            expected = await CheckRoutedEventsAsync(window, accountsPath, current, a, b, c);
            AssertNoBrowser(window);
            await CloseAsync(window);
            Assert(closed, "MainWindow did not finish its real asynchronous close path");
            Assert(window.HostedSessions.Count == 0 && window.HostedControlCount == 0,
                "Closing an order-only window must leave no browser sessions or controls");

            // Reopen only the file store. Showing another populated MainWindow would navigate.
            var persisted = Snapshot(accountsPath);
            var persistedBackup = Snapshot(accountsPath + ".bak");
            var reloaded = new AccountsStore(new FileKeyValueStore(accountsPath));
            Assert(reloaded.GetCurrentId() == current
                && AccountJsonCodec.ToJson(reloaded.All()) == AccountJsonCodec.ToJson(expected),
                "A fresh accounts.json store must retain the final non-original order, metadata, and current");
            Assert(reloaded.TakeLoadNotice() is null, "Reload must use the saved primary file, not backup recovery");
            AssertUnchanged(accountsPath, persisted, "Reloading the saved order");
            AssertUnchanged(accountsPath + ".bak", persistedBackup, "Reloading the saved order (backup)");
            Assert(Directory.GetFileSystemEntries(profiles, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(profileEntries),
                "Order-only smoke must not create browser profile content");
            foreach (var account in new[] { a, b, c })
                Assert(File.ReadAllText(ProfileMarker(profiles, account.Id)) == "synthetic order profile " + account.Id,
                    "Moving accounts must not modify synthetic profile markers");
            Console.WriteLine("PASS: closed window; new accounts.json store retains final order/current and profiles remain unchanged");
            Console.WriteLine("SKIPPED: native mouse drag gesture, DragDrop.DoDragDrop, drag threshold/capture, click suppression, and edge auto-scroll; no desktop input was injected");
        }
        finally
        {
            try
            {
                if (window is not null && !closed) await CloseAsync(window);
            }
            finally
            {
                if (window is null || closed)
                {
                    try { Directory.Delete(root, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Console.WriteLine("NOTE: retained synthetic order smoke directory: " + root); }
                }
                else Console.WriteLine("NOTE: window did not close; retained synthetic order smoke directory: " + root);
            }
        }
    }

    private static Account AddSynthetic(MainWindow window, string profiles, string label)
    {
        var account = window.Accounts.AddDefault();
        account.Name = "Synthetic order " + label;
        account.Avatar = label + "甲";
        window.Accounts.Update(account);
        var marker = ProfileMarker(profiles, account.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "synthetic order profile " + account.Id);
        return account;
    }

    private static string ProfileMarker(string profiles, long id)
        => Path.Combine(profiles, "acct_" + id, "order-smoke.txt");

    private static async Task AssertBothLayoutsAsync(MainWindow window, long current, params Account[] expected)
    {
        Click(window, "BtnRailExpand");
        await FlushAsync(window);
        Assert(((ColumnDefinition)window.FindName("SidebarCol")).Width.Value == 274
            && ((FrameworkElement)window.FindName("DrawerContent")).IsVisible,
            "Expanded drawer must actually be visible at 274px");
        AssertOrder(window, current, expected);
        AssertVerticalLayout(Panel(window, "AccountList"));
        Click(window, "BtnCollapse");
        await FlushAsync(window);
        Assert(((ColumnDefinition)window.FindName("SidebarCol")).Width.Value == 56
            && ((FrameworkElement)window.FindName("Rail")).IsVisible,
            "Collapsed rail must actually be visible at 56px");
        AssertOrder(window, current, expected);
        AssertVerticalLayout(Panel(window, "RailAvatars"));
    }

    private static void AssertOrder(MainWindow window, long current, Account[] expected)
    {
        Assert(window.Accounts.All().Select(account => account.Id).SequenceEqual(expected.Select(account => account.Id)),
            "Stored account order differs from the expected order");
        Assert(window.Accounts.GetCurrentId() == current, "Ordering must preserve current account ID");
        var rows = Panel(window, "AccountList").Children.Cast<Border>().ToArray();
        var rail = Panel(window, "RailAvatars").Children.Cast<Grid>().ToArray();
        Assert(rows.Length == expected.Length && rail.Length == expected.Length,
            "Both renderers must contain every synthetic account exactly once");
        for (var index = 0; index < expected.Length; index++)
        {
            var account = expected[index];
            Assert(DrawerName(rows[index]) == account.Name && rows[index].AllowDrop,
                "Drawer row order or actual account drag/drop handler attachment is incorrect");
            var outer = (Border)rail[index].Children[0];
            var tile = (Border)outer.Child;
            Assert(tile.Child is TextBlock text && text.Text == account.Avatar[..1],
                "Narrow rail avatar order must match the drawer and store");
            Assert(outer.BorderThickness == new Thickness(account.Id == current ? 2 : 0)
                && rows[index].BorderThickness == new Thickness(account.Id == current ? 1.5 : 1),
                "The same current account must stay highlighted in both renderers");
        }
        AssertNoBrowser(window);
    }

    private static string DrawerName(Border row)
        => ((DockPanel)row.Child).Children.OfType<StackPanel>().Single()
            .Children.OfType<TextBlock>().First().Text;

    private static void AssertVerticalLayout(Panel panel)
    {
        var previous = double.NegativeInfinity;
        foreach (FrameworkElement child in panel.Children)
        {
            var y = child.TranslatePoint(new Point(), panel).Y;
            Assert(child.IsVisible && child.ActualHeight > 0 && y > previous,
                "Visible rendered account positions must follow the child collection order");
            previous = y;
        }
    }

    private static async Task<Account[]> CheckRoutedEventsAsync(
        MainWindow window, string accountsPath, long current, Account a, Account b, Account c)
    {
        // DragEventArgs is sealed and has no public constructor. Do not use uninitialized objects,
        // native input, or bypass the real handlers; report an explicit fallback if WPF changes it.
        var constructor = typeof(DragEventArgs).GetConstructor(PrivateInstance, binder: null,
            types: new[] { typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects),
                typeof(DependencyObject), typeof(Point) }, modifiers: null);
        var dragId = typeof(MainWindow).GetField("draggingAccountId", PrivateInstance);
        var expected = new[] { b, a, c };
        if (constructor is null || dragId is null)
        {
            Console.WriteLine("SKIPPED: WPF synthetic DragOver/Drop construction is unavailable; only actual MoveAccount and RenderAccounts were exercised");
            return expected;
        }

        Click(window, "BtnRailExpand");
        await FlushAsync(window);
        var target = Row(window, b);
        DragEventArgs Create(IDataObject data, RoutedEvent routedEvent, bool after)
        {
            var point = new Point(target.ActualWidth / 2, target.ActualHeight * (after ? 0.75 : 0.25));
            var args = (DragEventArgs)constructor.Invoke(new object[]
            {
                data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, point,
            });
            args.RoutedEvent = routedEvent;
            Assert(Math.Abs(args.GetPosition(target).Y - point.Y) < 0.01,
                "Synthetic WPF drag coordinates must preserve the target-relative insertion point");
            return args;
        }

        try
        {
            // Probe only construction/coordinate support; failures in product handlers below must fail.
            _ = Create(new DataObject(AccountOrderFormat, c.Id), DragDrop.DragOverEvent, after: false);
        }
        catch (Exception ex) when (ex is MemberAccessException or TargetInvocationException or NotSupportedException)
        {
            Console.WriteLine("SKIPPED: synthetic DragEventArgs construction failed (" + ex.GetType().Name
                + "); only actual MoveAccount and RenderAccounts were exercised");
            await AssertBothLayoutsAsync(window, current, expected);
            return expected;
        }

        var primary = Snapshot(accountsPath);
        var backup = Snapshot(accountsPath + ".bak");
        var previousDragId = dragId.GetValue(window);
        Assert(previousDragId is null, "Order smoke must not run during a real drag");
        try
        {
            dragId.SetValue(window, c.Id);
            foreach (var data in new IDataObject[]
            {
                new DataObject("MuseLauncher.NotAccountOrder", c.Id),
                new DataObject(AccountOrderFormat, c.Id.ToString()),
                new DataObject(AccountOrderFormat, a.Id),
            })
                Reject(data, "Wrong format, wrong type, or mismatched source ID must be rejected");

            dragId.SetValue(window, b.Id);
            Reject(new DataObject(AccountOrderFormat, b.Id), "Dropping onto the dragged account must be rejected");
            dragId.SetValue(window, null);
            Reject(new DataObject(AccountOrderFormat, c.Id), "Unsolicited account data without an active drag must be rejected");
            AssertUnchanged(accountsPath, primary, "Rejected routed events");
            AssertUnchanged(accountsPath + ".bak", backup, "Rejected routed events (backup)");
            AssertOrder(window, current, expected);

            // C before B: [B,A,C] -> [C,B,A]. During a drag, rendering is intentionally deferred.
            dragId.SetValue(window, c.Id);
            AcceptedDrop(c, after: false);
            expected = new[] { c, b, a };
            Assert(window.Accounts.All().Select(account => account.Id).SequenceEqual(expected.Select(account => account.Id))
                && window.Accounts.GetCurrentId() == current, "Routed before-drop must move C before B without selecting C");
            Assert(Panel(window, "AccountList").Children.Cast<Border>().Select(DrawerName)
                .SequenceEqual(new[] { b.Name, a.Name, c.Name }), "RenderAccounts must defer list replacement during an active drag");
            dragId.SetValue(window, null);
            Render(window); // Same post-drag render as AttachAccountOrderHandlers' DoDragDrop finally.
            await AssertBothLayoutsAsync(window, current, expected);

            Click(window, "BtnRailExpand");
            await FlushAsync(window);
            target = Row(window, b);
            dragId.SetValue(window, c.Id);
            AcceptedDrop(c, after: true);
            expected = new[] { b, c, a };
            dragId.SetValue(window, null);
            Render(window);
            await AssertBothLayoutsAsync(window, current, expected);
            Console.WriteLine("PASS: synthetic routed DragOver/Drop rejects invalid data and exercises top/bottom insertion markers, DragLeave reset, before/after moves, deferred render, and stable current");
            return expected;
        }
        finally
        {
            dragId.SetValue(window, previousDragId);
            Render(window);
        }

        void Reject(IDataObject data, string message)
        {
            var thickness = target.BorderThickness;
            var brush = target.BorderBrush;
            foreach (var routedEvent in new[] { DragDrop.DragOverEvent, DragDrop.DropEvent })
            {
                var args = Create(data, routedEvent, after: false);
                target.RaiseEvent(args);
                Assert(args.Effects == DragDropEffects.None && !args.Handled, message);
                Assert(target.BorderThickness == thickness && ReferenceEquals(target.BorderBrush, brush),
                    "Rejected drag data must not leave an insertion marker");
            }
        }

        void AcceptedDrop(Account source, bool after)
        {
            var data = new DataObject(AccountOrderFormat, source.Id);
            var normalThickness = target.BorderThickness;
            var normalBrush = target.BorderBrush;
            var marker = after ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
            var over = Create(data, DragDrop.DragOverEvent, after);
            target.RaiseEvent(over);
            Assert(over.Handled && over.Effects == DragDropEffects.Move && target.BorderThickness == marker,
                "Valid DragOver must expose the requested before/after insertion marker and Move effect");
            target.RaiseEvent(Create(data, DragDrop.DragLeaveEvent, after));
            Assert(target.BorderThickness == normalThickness && ReferenceEquals(target.BorderBrush, normalBrush),
                "DragLeave must restore the row's original selection border");
            target.RaiseEvent(Create(data, DragDrop.DragOverEvent, after));
            var drop = Create(data, DragDrop.DropEvent, after);
            target.RaiseEvent(drop);
            Assert(drop.Handled && drop.Effects == DragDropEffects.Move,
                "Valid routed Drop must be handled by the actual account-order handler");
            Assert(target.BorderThickness == normalThickness && ReferenceEquals(target.BorderBrush, normalBrush),
                "Drop must reset the insertion marker even while rendering is deferred");
            AssertNoBrowser(window);
        }
    }

    private static Border Row(MainWindow window, Account account)
        => Panel(window, "AccountList").Children.Cast<Border>().Single(row => DrawerName(row) == account.Name);

    private static Panel Panel(MainWindow window, string name) => (Panel)window.FindName(name);

    private static void Render(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("RenderAccounts", PrivateInstance)
            ?? throw new MissingMethodException(nameof(MainWindow), "RenderAccounts");
        method.Invoke(window, null);
    }

    private static void Click(MainWindow window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertNoBrowser(MainWindow window)
        => Assert(window.HostedSessions.Count == 0 && window.HostedControlCount == 0
            && window.DisplayState.AccountId is null && !window.DisplayState.CanShowWebView,
            "Order-only smoke must never activate/navigate an account or create a WebView");

    private static async Task FlushAsync(MainWindow window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task CloseAsync(MainWindow window)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => completion.TrySetResult();
        window.Closed += OnClosed;
        try
        {
            window.Close();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { window.Closed -= OnClosed; }
    }

    private static (byte[] Bytes, DateTime Written) Snapshot(string path)
        => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    private static void AssertUnchanged(string path, (byte[] Bytes, DateTime Written) before, string operation)
        => Assert(File.ReadAllBytes(path).SequenceEqual(before.Bytes) && File.GetLastWriteTimeUtc(path) == before.Written,
            operation + " must not rewrite metadata or backup files");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
