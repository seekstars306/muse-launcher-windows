using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using MuseLauncher.Core;

namespace MuseLauncher;

public partial class MainWindow
{
    private bool credentialTransferBusy;
    private readonly HashSet<AccountWebHost> credentialTransferFailures = new();

    private void OnCredentialsClick(object sender, RoutedEventArgs e)
    {
        if (closing || credentialTransferBusy || changingAccounts.Count > 0) return;
        var dialog = new CredentialsWindow(ExportCredentialsAsync, ImportCredentialsAsync, store.All().Count > 0)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private HashSet<long> BeginCredentialTransfer()
    {
        if (closing || credentialTransferBusy || changingAccounts.Count > 0)
            throw new InvalidOperationException("请等待账号操作完成");
        credentialTransferBusy = true;
        transition?.Cancel();
        editId = confirmDelId = -1;
        RenderAccounts();
        WebContainer.IsEnabled = false;
        var snapshot = sessions.Snapshot();
        foreach (var host in snapshot.Values) host.SetVisible(false);
        return snapshot.Keys.ToHashSet();
    }

    private async Task EndCredentialTransferAsync(IReadOnlySet<long> activeAccounts)
    {
        var current = store.GetCurrentId();
        var rebuildCurrent = false;
        var cleanupFailed = false;
        try
        {
            while (credentialTransferFailures.Count > 0)
            {
                var failed = credentialTransferFailures.ToArray();
                credentialTransferFailures.Clear();
                foreach (var host in failed)
                {
                    if (!sessions.IsRegistered(host.AccountId, host)) continue;
                    rebuildCurrent |= host.AccountId == current;
                    try { await sessions.InvalidateAsync(host.AccountId, host); }
                    catch (Exception) { cleanupFailed = true; }
                }
            }
        }
        finally
        {
            credentialTransferBusy = false;
            WebContainer.IsEnabled = true;
            RenderAccounts();
        }
        if (closing) return;
        if (cleanupFailed)
        {
            if (current > 0) viewState.SetError(current, viewState.Version, "浏览器释放失败，请重新打开启动器后重试");
            RefreshVisibleState();
            return;
        }
        if (current > 0 && activeAccounts.Contains(current)
            && (rebuildCurrent || !viewState.HasHost))
        {
            await SwitchTo(current, animate: false);
            return;
        }
        transition?.Dispose();
        transition = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (sessions.Current is { } active && sessions.IsCurrent(current, active)
            && viewState.AccountId == current && viewState.HasHost)
        {
            viewState.SetError(current, viewState.Version, active.Errored ? active.ErrorMessage : null);
            if (active.LoadStartAt != null)
                _ = HideSplashAfterDelayAsync(current, viewState.Version, SplashTimeoutMs);
            else if (active.Loaded || active.Errored)
                viewState.HideSplash(current, viewState.Version);
        }
        RefreshVisibleState();
    }

    internal async Task<string> ExportCredentialsAsync(string path, string passphrase, IProgress<string> progress)
    {
        var activeAccounts = BeginCredentialTransfer();
        var paused = new List<AccountWebHost>();
        try
        {
            foreach (var host in sessions.Snapshot().Values)
            {
                if (!await host.Core.TrySuspendAsync())
                    throw new InvalidDataException("有账号页面暂时无法暂停，请结束页面任务后重试导出");
                paused.Add(host);
            }
            var accounts = store.All();
            if (accounts.Count == 0) throw new InvalidDataException("没有可导出的账号");
            if (accounts.Count > CredentialArchive.MaximumAccounts) throw new InvalidDataException("账号数量超过导出上限");
            var bundle = new CredentialBundle();
            var unavailable = new HashSet<string>(StringComparer.Ordinal);
            long avatarBytes = 0;
            for (var index = 0; index < accounts.Count; index++)
            {
                var account = accounts[index];
                progress.Report($"正在收集账号 {index + 1}/{accounts.Count}…");
                var item = new CredentialAccount
                {
                    Name = account.Name, Avatar = account.Avatar, Proxy = account.Proxy,
                    ForceProxy = account.ForceProxy, Tags = new(account.Tags),
                };
                if (account.AvatarImage.Length > 0)
                {
                    var file = new FileInfo(account.AvatarImage);
                    if (!file.Exists || file.Length > 5 * 1024 * 1024)
                        throw new InvalidDataException("有账号的头像无法读取或超过 5 MB，请修正后再导出");
                    avatarBytes += file.Length;
                    if (avatarBytes > 24 * 1024 * 1024)
                        throw new InvalidDataException("头像总大小超过 24 MB，请缩小图片后再导出");
                    item.AvatarExtension = file.Extension.ToLowerInvariant();
                    item.AvatarImage = await File.ReadAllBytesAsync(file.FullName);
                    ValidateAvatar(item.AvatarImage);
                }

                var profile = ProfilePath(account.Id);
                var active = sessions.Snapshot().TryGetValue(account.Id, out var host) && !host.IsDisposed;
                if (active)
                {
                    try
                    {
                        item.Cookies = await host!.ExportMuseCookiesAsync();
                        item.CookiesCaptured = true;
                    }
                    catch (Exception)
                    {
                        item.Cookies.Clear();
                        item.CookiesCaptured = false;
                        unavailable.Add(account.Name);
                    }
                    if (item.CookiesCaptured && !item.Cookies.Any(cookie => cookie.Name == AccountWebHost.AuthCookieName))
                        unavailable.Add(account.Name);
                }
                else if (PendingCredentials.Exists(profile))
                {
                    try
                    {
                        item.Cookies = PendingCredentials.Read(profile).Cookies;
                        item.CookiesCaptured = true;
                        if (!item.Cookies.Any(cookie => cookie.Name == AccountWebHost.AuthCookieName))
                            unavailable.Add(account.Name);
                    }
                    catch (Exception)
                    {
                        item.CookiesCaptured = false;
                        unavailable.Add(account.Name);
                    }
                }
                else if (Directory.Exists(profile))
                {
                    AccountWebHost? temporary = null;
                    try
                    {
                        temporary = await AccountWebHost.CreateAsync(account.Id,
                            new BrowserSessionConfiguration(account.Proxy, account.ForceProxy, profile),
                            control => WebContainer.Children.Add(control));
                        item.Cookies = await temporary.ExportMuseCookiesAsync();
                        item.CookiesCaptured = true;
                        if (!item.Cookies.Any(cookie => cookie.Name == AccountWebHost.AuthCookieName))
                            unavailable.Add(account.Name);
                    }
                    catch (Exception)
                    {
                        item.Cookies.Clear();
                        item.CookiesCaptured = false;
                        unavailable.Add(account.Name);
                    }
                    finally
                    {
                        if (temporary != null)
                        {
                            // A profile that cannot be released is unsafe to archive; abort.
                            await temporary.DisposeAsync();
                        }
                    }
                }
                else item.CookiesCaptured = true;
                bundle.Accounts.Add(item);
            }
            progress.Report("正在加密并写入文件…");
            await Task.Run(() =>
            {
                var encrypted = CredentialArchive.Encrypt(bundle, passphrase);
                try { WriteCredentialFile(path, encrypted); }
                finally { CryptographicOperations.ZeroMemory(encrypted); }
            });
            return $"已加密导出 {bundle.Accounts.Count} 个账号。" + (unavailable.Count == 0 ? ""
                : "\n以下账号未能取得登录态，导入后需要重新登录：" + string.Join("、", unavailable));
        }
        finally
        {
            var resumeFailed = false;
            foreach (var host in paused)
            {
                try { if (!host.IsDisposed) host.Core.Resume(); }
                catch (Exception) { resumeFailed = true; }
            }
            await EndCredentialTransferAsync(activeAccounts);
            if (resumeFailed)
                throw new InvalidDataException("导出后的账号页面未能恢复，请重新打开启动器；账号配置已保留");
        }
    }

    internal async Task<string> ImportCredentialsAsync(string path, string passphrase, IProgress<string> progress)
    {
        var activeAccounts = BeginCredentialTransfer();
        try
        {
            var bundle = await Task.Run(() =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > CredentialArchive.MaximumFileBytes)
                    throw new InvalidDataException("凭证文件过大");
                var encrypted = new byte[checked((int)file.Length)];
                try { file.ReadExactly(encrypted); return CredentialArchive.Decrypt(encrypted, passphrase); }
                finally { CryptographicOperations.ZeroMemory(encrypted); }
            });
            foreach (var item in bundle.Accounts)
                if (item.AvatarImage is { Length: > 0 }) ValidateAvatar(item.AvatarImage);
            progress.Report("正在追加账号，不覆盖现有配置…");
            var drafts = bundle.Accounts.Select(item => new Account(1, item.Name)
            {
                Avatar = item.Avatar, Proxy = item.Proxy, ForceProxy = item.ForceProxy, Tags = new(item.Tags),
            }).ToArray();
            progress.Report("正在准备账号资料…");
            var createdProfiles = new List<string>();
            var createdAvatars = new List<string>();
            IReadOnlyList<Account> imported;
            try
            {
                imported = store.AppendImported(drafts, added =>
                {
                    for (var index = 0; index < added.Count; index++)
                    {
                        var account = added[index];
                        var item = bundle.Accounts[index];
                        var profile = ProfilePath(account.Id);
                        // ID 分配会跳过已有目录；任何碰撞都停止，不覆盖。
                        if (Directory.Exists(profile) || File.Exists(profile)) throw new IOException("新账号目录已占用");
                        Directory.CreateDirectory(profile);
                        createdProfiles.Add(profile);
                        if (item.CookiesCaptured && item.Cookies.Count > 0)
                        {
                            PendingCredentials.Write(profile, new CredentialAccount
                            {
                                Name = account.Name, CookiesCaptured = true, Cookies = item.Cookies,
                            });
                        }
                        if (item.AvatarImage is { Length: > 0 })
                        {
                            Directory.CreateDirectory(avatarDirectory);
                            var target = Path.Combine(avatarDirectory, account.Id + "-"
                                + Convert.ToHexString(SHA256.HashData(item.AvatarImage)) + item.AvatarExtension);
                            using (var avatar = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                createdAvatars.Add(target);
                                avatar.Write(item.AvatarImage);
                                avatar.Flush(flushToDisk: true);
                            }
                            account.AvatarImage = target;
                        }
                    }
                });
            }
            catch
            {
                foreach (var file in createdAvatars)
                {
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                foreach (var directory in createdProfiles)
                {
                    try { Directory.Delete(directory, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
            var needsLogin = new List<string>();
            for (var index = 0; index < imported.Count; index++)
            {
                progress.Report($"正在恢复登录 Cookie {index + 1}/{imported.Count}…");
                var account = imported[index];
                var item = bundle.Accounts[index];
                if (!item.CookiesCaptured || item.Cookies.Count == 0)
                {
                    needsLogin.Add(account.Name);
                    continue;
                }
                AccountWebHost? temporary = null;
                try
                {
                    temporary = await AccountWebHost.CreateAsync(account.Id,
                        new BrowserSessionConfiguration("", false, ProfilePath(account.Id)),
                        control => WebContainer.Children.Add(control));
                    if (!await temporary.RestoreMuseCookiesAsync(item.Cookies)) needsLogin.Add(account.Name);
                    // 保留 DPAPI 待恢复文件，首次正常打开时再恢复 session Cookie 后删除。
                }
                catch (Exception) { needsLogin.Add(account.Name); }
                finally
                {
                    if (temporary != null)
                    {
                        try { await temporary.DisposeAsync(); }
                        catch (Exception) { if (!needsLogin.Contains(account.Name)) needsLogin.Add(account.Name); }
                    }
                }
            }
            return $"已追加 {imported.Count} 个账号，现有账号未覆盖。"
                + (needsLogin.Count == 0 ? "\nCookie 已写入；是否仍有效需在打开 Muse 时确认。"
                    : "\n以下账号需要重新登录：" + string.Join("、", needsLogin)
                        + "\n其他账号的 Cookie 是否仍有效，请打开 Muse 后确认。");
        }
        finally
        {
            await EndCredentialTransferAsync(activeAccounts);
        }
    }

    private static void ValidateAvatar(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
        }
        catch (Exception) { throw new InvalidDataException("凭证包中有无法读取的头像图片"); }
    }

    private static void WriteCredentialFile(string path, byte[] encrypted)
    {
        var target = Path.GetFullPath(path);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
