using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MuseLauncher;
using MuseLauncher.Core;

namespace MuseLauncher.WindowsSmoke;

internal static class AvatarNavigationSmoke
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly byte[] Png1X1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MuseLauncherUiSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunNavigationAsync(root);
            RunAvatarReflectionChecks(root);
            RunConfigSaveChecks(root);
            Console.WriteLine("PASS: --ui-only completed without WebView, real accounts, or external targets");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { Console.WriteLine("NOTE: 保留 UI smoke 临时目录供诊断：" + root); }
        }
    }

    private static async Task RunNavigationAsync(string root)
    {
        var window = new MainWindow(
            Path.Combine(root, "Metadata"),
            Path.Combine(root, "Profiles"),
            "http://ui-only.invalid")
        {
            Width = 1200,
            Height = 760,
        };
        window.Show();
        try
        {
            await FlushAsync();
            var column = (ColumnDefinition)window.FindName("SidebarCol");
            Assert(window.Accounts.All().Count == 0 && window.HostedSessions.Count == 0
                && window.HostedControlCount == 0, "UI-only 必须始终使用无 WebView 的空账号窗口");
            Assert(column.Width.Value == 56, "空账号窗口初始侧栏应为 56px");
            var avatarAccount = new Account(7, "独立头像草稿") { Avatar = "甲乙" };
            var rail = (Grid)InvokePrivate(window, "MakeRailAvatar", avatarAccount)!;
            var railTile = (Border)((Border)rail.Children[0]).Child;
            var row = (Border)InvokePrivate(window, "MakeDrawerRow", avatarAccount)!;
            var drawerTile = ((DockPanel)row.Child).Children.OfType<Border>().Single();
            Assert(((TextBlock)railTile.Child).Text == "甲", "窄栏应截取同一账号头像的一个字符");
            Assert(((TextBlock)drawerTile.Child).Text == "甲乙", "展开栏应保留同一账号头像的两个字符");
            Console.WriteLine("PASS: actual rail/drawer renderers use one/two avatar characters");

            Click(window, "BtnRailExpand");
            Assert(column.Width.Value == 274, "1200px 展开态侧栏应为 274px");
            window.Width = 900;
            await FlushAsync();
            Assert(column.Width.Value == 56, "宽度 900px 时展开侧栏应自动收起为 56px");
            window.Width = 1050;
            await FlushAsync();
            Assert(column.Width.Value == 56, "宽度 1050px 时侧栏应保持自动收起");
            window.Width = 1200;
            await FlushAsync();
            Assert(column.Width.Value == 274, "宽度 1200px 时侧栏应恢复 274px");
            Console.WriteLine("PASS: responsive sidebar 1200=>274, 900=>56, 1050=>56, 1200=>274");

            window.Width = 900;
            await FlushAsync();
            Click(window, "BtnRailExpand");
            Assert(column.Width.Value == 274, "窄窗仍可手动展开侧栏");
            window.Height = 740;
            await FlushAsync();
            Assert(column.Width.Value == 274, "仅改变高度不能收起窄窗中手动展开的侧栏");
            window.Width = 1200;
            await FlushAsync();
            Console.WriteLine("PASS: height-only resize preserves manually expanded narrow sidebar");

            Click(window, "BtnCollapse");
            window.Width = 900;
            await FlushAsync();
            window.Width = 1200;
            await FlushAsync();
            Assert(column.Width.Value == 56, "手动关闭后调整窗口不能自动展开侧栏");
            Console.WriteLine("PASS: manual collapse does not reopen on resize");

            Click(window, "BtnRailExpand");
            Assert(column.Width.Value == 274, "手动展开后侧栏应为 274px");
            SendKey(window, Key.F11);
            await FlushAsync();
            Assert(column.Width.Value == 0, "F11 应隐藏侧栏");
            SendKey(window, Key.F11);
            await FlushAsync();
            Assert(column.Width.Value == 274, "退出 F11 应恢复展开状态");

            Click(window, "BtnHideSidebar");
            Assert(column.Width.Value == 4, "完全隐藏侧栏应保留 4px 边缘触发区");
            SendKey(window, Key.F11);
            await FlushAsync();
            Assert(column.Width.Value == 0, "隐藏状态进入 F11 应隐藏侧栏");
            SendKey(window, Key.F11);
            await FlushAsync();
            Assert(column.Width.Value == 4, "隐藏状态退出 F11 应恢复 4px 边缘触发区");
            var edge = (Border)window.FindName("SidebarEdgeTrigger");
            Assert(edge.Visibility == Visibility.Visible && edge.Width == 4,
                "隐藏状态应显示 4px MouseEnter 触发区");
            Assert(((FrameworkElement)window.FindName("Rail")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)window.FindName("DrawerContent")).Visibility == Visibility.Collapsed,
                "隐藏状态下窄栏和展开栏都应收起");
            edge.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
            });
            Assert(column.Width.Value == 274, "鼠标移入 4px 边缘应恢复展开侧栏");
            Console.WriteLine("PASS: hidden 4px edge trigger and F11 state restoration");

            Assert(window.Accounts.All().Count == 0 && window.HostedSessions.Count == 0
                && window.HostedControlCount == 0, "侧栏操作不得创建账号或 WebView");
            Console.WriteLine("SKIPPED: Ctrl+Shift+B 未注入系统按键，避免在宿主桌面改变用户窗口状态");
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static void RunAvatarReflectionChecks(string root)
    {
        var avatarRoot = Path.Combine(root, "Avatars");
        Directory.CreateDirectory(avatarRoot);
        var good = Path.Combine(root, "supported.png");
        var bad = Path.Combine(root, "broken.png");
        File.WriteAllBytes(good, Png1X1);
        File.WriteAllText(bad, "this is not a PNG");

        var account = new Account(7, "头像测试") { Avatar = "m", AvatarImage = good };
        var config = new AccountConfigWindow(account, _ => { }, avatarRoot);
        try
        {
            var load = typeof(AccountConfigWindow).GetMethod("LoadAvatarImage", PrivateInstance)
                ?? throw new MissingMethodException("LoadAvatarImage");
            var preview = (Image)config.FindName("AvatarImagePreview");
            var tileText = (TextBlock)config.FindName("TileText");
            Assert(preview.Source != null && preview.Visibility == Visibility.Visible,
                "受支持 PNG 应显示配置窗口头像预览");

            using (File.Open(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var unlocked = Path.Combine(root, "supported-moved.png");
            File.Move(good, unlocked);
            Assert(File.Exists(unlocked), "读取头像后应释放源文件锁");

            load.Invoke(config, new object[] { bad });
            Assert(preview.Source == null && tileText.Visibility == Visibility.Visible,
                "损坏图片应回退到文字头像");
            Console.WriteLine("PASS: supported PNG, bad-image fallback, and unlocked avatar source");
        }
        finally { config.Close(); }

        var makeTile = typeof(MainWindow).GetMethod("MakeTile", PrivateStatic)
            ?? throw new MissingMethodException("MakeTile");
        var moved = Path.Combine(root, "supported-moved.png");
        var imageTile = (Border)makeTile.Invoke(null, new object?[] { 40d, 20d, "AB", moved })!;
        Assert(imageTile.Child is Image { Source: not null }, "MakeTile 应渲染受支持的 PNG");
        using (File.Open(moved, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var fallback = (Border)makeTile.Invoke(null, new object?[] { 40d, 20d, "AB", bad })!;
        Assert(fallback.Child is TextBlock { Text: "AB" }, "MakeTile 损坏图片应回退文字");
        var narrow = (Border)makeTile.Invoke(null, new object?[] { 38d, 20d, "A", null })!;
        var expanded = (Border)makeTile.Invoke(null, new object?[] { 40d, 20d, "AB", null })!;
        Assert(((TextBlock)narrow.Child!).Text == "A", "窄栏单字符头像应保留单字");
        Assert(((TextBlock)expanded.Child!).Text == "AB", "展开栏头像应保留双字");
        Console.WriteLine("PASS: narrow single-character and expanded two-character avatar tiles");
    }

    private static void RunConfigSaveChecks(string root)
    {
        var avatarRoot = Path.Combine(root, "ConfigAvatars");
        Directory.CreateDirectory(avatarRoot);
        var oldImage = Path.Combine(avatarRoot, "7-old.png");
        var source = Path.Combine(root, "new-source.png");
        File.WriteAllBytes(oldImage, Png1X1);
        File.WriteAllBytes(source, Png1X1);
        // 独立存储不连接主窗口，保存/恢复账号不会触发 WebView 或磁盘 Cookie 探测。
        var configPath = Path.Combine(root, "ConfigMetadata", "accounts.json");
        var store = new AccountsStore(new FileKeyValueStore(configPath));
        var original = store.AddDefault();
        original.AvatarImage = oldImage;
        store.Update(original);

        Account? saved = null;
        var saveWindow = new AccountConfigWindow(original, value =>
        {
            store.Update(value);
            saved = value.Clone();
        }, avatarRoot);
        try
        {
            InvokePrivate(saveWindow, "LoadAvatarImage", source);
            ((TextBox)saveWindow.FindName("AvatarInput")).Text = "xy";
            ((TextBox)saveWindow.FindName("NameInput")).Text = "新账号";
            ShowDialogAndSave(saveWindow);
            Assert(saved != null, "配置保存应回调独立草稿");
            Assert(saved!.AvatarImage.StartsWith(Path.GetFullPath(avatarRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "头像应保存到隔离临时头像目录");
            Assert(File.Exists(saved.AvatarImage) && !Path.GetFullPath(saved.AvatarImage).Equals(
                Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase), "保存头像应复制独立源文件");
            Assert(File.ReadAllBytes(saved.AvatarImage).SequenceEqual(Png1X1), "头像副本内容应与源文件一致");
            using (File.Open(saved.AvatarImage, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(source);
            Assert(File.ReadAllBytes(saved.AvatarImage).SequenceEqual(Png1X1), "删除独立源文件后已保存头像仍应可读");
            var reloaded = new AccountsStore(new FileKeyValueStore(configPath)).FindById(original.Id)!;
            Assert(reloaded.AvatarImage == saved.AvatarImage && reloaded.Avatar == "xy" && reloaded.Name == "新账号",
                "配置应真正保存到临时账号文件并可重新读取");
            Assert(original.AvatarImage == oldImage && File.Exists(oldImage), "保存草稿不能修改或删除旧头像");
            Console.WriteLine("PASS: config save copies avatar into isolated directory without locking source");
        }
        finally { if (saveWindow.IsVisible) saveWindow.Close(); }

        Account? reset = null;
        var savedImage = saved!.AvatarImage;
        var resetWindow = new AccountConfigWindow(saved, value =>
        {
            store.Update(value);
            reset = value.Clone();
        }, avatarRoot);
        try
        {
            InvokePrivate(resetWindow, "OnResetAvatarClick", resetWindow, new RoutedEventArgs());
            ShowDialogAndSave(resetWindow);
            Assert(reset != null && reset.AvatarImage.Length == 0 && reset.Avatar == "m",
                "恢复默认应保存 m 且清除新头像路径");
            Assert(File.Exists(oldImage) && File.ReadAllBytes(savedImage).SequenceEqual(Png1X1),
                "恢复默认应保留旧头像以及备份引用的图片");
            var backup = new AccountsStore(new FileKeyValueStore(configPath + ".bak"));
            Assert(backup.FindById(original.Id)?.AvatarImage == savedImage,
                "恢复默认后账号文件备份应仍引用可用旧头像");
            Console.WriteLine("PASS: reset to m keeps previous avatar backup");
        }
        finally { if (resetWindow.IsVisible) resetWindow.Close(); }

        File.WriteAllBytes(source, Png1X1);
        var failureAvatarRoot = Path.Combine(root, "FailedSaveAvatars");
        var beforeFailure = File.ReadAllBytes(configPath);
        Account? failed = null;
        var failedWindow = new AccountConfigWindow(saved!, value =>
        {
            failed = value;
            throw new IOException("synthetic save failure");
        }, failureAvatarRoot);
        try
        {
            InvokePrivate(failedWindow, "LoadAvatarImage", source);
            ShowDialogAndSave(failedWindow, expectSuccess: false);
            Assert(failed != null, "IOException 保存测试应到达保存回调");
            Assert(saved!.AvatarImage == savedImage && File.ReadAllBytes(savedImage).SequenceEqual(Png1X1)
                && File.ReadAllBytes(oldImage).SequenceEqual(Png1X1),
                "save 抛 IOException 时旧账号头像路径和图片内容不得改变");
            Assert(File.ReadAllBytes(configPath).SequenceEqual(beforeFailure), "保存失败不得修改磁盘账号配置");
            Assert(!File.Exists(failed!.AvatarImage), "保存失败应清理本次新复制且未被引用的头像");
            Console.WriteLine("PASS: IOException during save leaves old avatar unchanged");
        }
        finally { if (failedWindow.IsVisible) failedWindow.Close(); }
    }

    private static void ShowDialogAndSave(AccountConfigWindow window, bool expectSuccess = true)
    {
        Exception? failure = null;
        var callbackRan = false;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            callbackRan = true;
            try
            {
                var button = (Button)window.FindName("SaveBtn");
                InvokePrivate(window, "OnSaveClick", button, new RoutedEventArgs(Button.ClickEvent));
                Assert(window.Saved == expectSuccess, "配置窗口 Saved 标志不符合预期："
                    + ((TextBlock)window.FindName("TestStatus")).Text);
                if (!expectSuccess)
                {
                    Assert(!closed && window.IsVisible, "保存失败时配置窗口应保持打开");
                    Assert(((TextBlock)window.FindName("TestStatus")).Text.Contains("未保存"),
                        "保存失败应显示错误状态");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { if (!closed) window.Close(); }
        }));
        var result = window.ShowDialog();
        Assert(callbackRan, "配置窗口在保存回调之前被关闭");
        if (failure != null) throw new InvalidOperationException("模态配置保存检查失败", failure);
        Assert(result == expectSuccess, "配置窗口 DialogResult 不符合预期");
    }

    private static void Click(Window window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void SendKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("窗口尚未连接到 WPF presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
        Assert(args.Handled, "F11 PreviewKeyDown 路由事件应由主窗口处理");
    }

    private static object? InvokePrivate(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, PrivateInstance)
            ?? throw new MissingMethodException(target.GetType().Name, name);
        return method.Invoke(target, arguments);
    }

    private static async Task FlushAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(20);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
