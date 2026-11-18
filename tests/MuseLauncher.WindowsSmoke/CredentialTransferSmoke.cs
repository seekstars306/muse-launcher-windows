using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using MuseLauncher;
using MuseLauncher.Core;

namespace MuseLauncher.WindowsSmoke;

internal static class CredentialTransferSmoke
{
    private const string Passphrase = "synthetic-transfer-passphrase-测试";
    private const string PendingFileName = "pending-muse-credentials.bin";
    private static readonly byte[] Png1X1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    internal static async Task RunAsync()
    {
        Assert(OperatingSystem.IsWindows(), "Credential transfer smoke requires Windows/WPF and WebView2");
        var application = Application.Current;
        Assert(application is not null && application is not App,
            "Run from the smoke STA Application; do not start the production App");
        if (application is null || application is App)
            throw new InvalidOperationException("Credential transfer smoke requires its isolated WPF host");
        application.Dispatcher.VerifyAccess();
        var root = Path.Combine(Path.GetTempPath(), "MuseLauncherCredentialSmoke-" + Guid.NewGuid().ToString("N"));
        var metadata = Path.Combine(root, "Metadata");
        var profiles = Path.Combine(root, "Profiles");
        var avatars = Path.Combine(metadata, "Avatars");
        var metadataPath = Path.Combine(metadata, "accounts.json");
        MainWindow? window = null;
        Directory.CreateDirectory(root);
        try
        {
            Log.ConfigureDirectoryForTests(Path.Combine(root, "log"));
            window = new MainWindow(metadata, profiles, "about:blank") { Width = 1100, Height = 760 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert(window.Accounts.All().Count == 0 && window.Accounts.GetCurrentId() == -1
                && window.HostedSessions.Count == 0 && window.HostedControlCount == 0,
                "Show and finish Loaded with an empty store before adding any accounts");

            // Adding only after Loaded prevents MainWindow's automatic current-account navigation.
            var existing = window.Accounts.AddDefault();
            existing.Name = "Synthetic duplicate";
            existing.Avatar = "旧甲";
            existing.Tags = new() { "existing-only" };
            Directory.CreateDirectory(avatars);
            var sourceAvatar = Path.Combine(root, "source-avatar.png");
            File.WriteAllBytes(sourceAvatar, Png1X1);
            existing.AvatarImage = Path.Combine(avatars, "existing.png");
            File.Copy(sourceAvatar, existing.AvatarImage);
            window.Accounts.Update(existing);
            var existingProfile = ProfilePath(profiles, existing.Id);
            Directory.CreateDirectory(existingProfile);
            var existingMarker = Path.Combine(existingProfile, "keep-existing.txt");
            File.WriteAllText(existingMarker, "synthetic existing profile");
            var reservedId = existing.Id + 1;
            var reservedProfile = ProfilePath(profiles, reservedId);
            Directory.CreateDirectory(reservedProfile);
            var reservedMarker = Path.Combine(reservedProfile, "keep-reserved.txt");
            File.WriteAllText(reservedMarker, "synthetic reserved profile");

            var bundle = CreateBundle(existing.Name, File.ReadAllBytes(sourceAvatar));
            var archivePath = Path.Combine(root, "input.musecred");
            var tamperedPath = Path.Combine(root, "tampered.musecred");
            var encrypted = CredentialArchive.Encrypt(bundle, Passphrase);
            try
            {
                File.WriteAllBytes(archivePath, encrypted);
                encrypted[^1] ^= 1; // Change the authenticated GCM tag, keeping a valid archive header.
                File.WriteAllBytes(tamperedPath, encrypted);
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
            var baseline = SnapshotTree(metadata, profiles);
            var baselineAccounts = AccountJsonCodec.ToJson(window.Accounts.All());
            var progress = new TransferProgress();
            await AssertRejectedAsync(window, archivePath, Passphrase + "-wrong", progress,
                metadataPath, metadata, profiles, existing.Id, baselineAccounts, baseline);
            await AssertRejectedAsync(window, tamperedPath, Passphrase, progress,
                metadataPath, metadata, profiles, existing.Id, baselineAccounts, baseline);
            Console.WriteLine("PASS: incorrect passphrase and tampering leave accounts, current, avatars, and profile trees unchanged");

            var importResult = await window.ImportCredentialsAsync(archivePath, Passphrase, progress);
            Assert(progress.Reports > 0 && importResult.Contains(bundle.Accounts[1].Name, StringComparison.Ordinal)
                && importResult.Contains("重新登录", StringComparison.Ordinal)
                && !importResult.Contains(bundle.Accounts[0].Name, StringComparison.Ordinal),
                "Import should restore the captured account and identify only the uncaptured account for login");
            var all = window.Accounts.All();
            Assert(all.Count == 3 && window.Accounts.GetCurrentId() == existing.Id,
                "Import must append two accounts without changing the existing current account");
            Assert(AccountJsonCodec.ToJson(all.Take(1).ToArray()) == baselineAccounts,
                "Import overwrote existing account metadata");
            var imported = all.Skip(1).ToArray();
            Assert(all.Select(account => account.Id).Distinct().Count() == 3
                && imported.All(account => account.Id > existing.Id && account.Id != reservedId)
                && all.Count(account => account.Name == existing.Name) == 2,
                "Same-name import must receive fresh IDs and skip the reserved profile directory");
            for (var index = 0; index < imported.Length; index++)
            {
                AssertConfiguration(imported[index], bundle.Accounts[index]);
                Assert(!imported[index].Logged && Directory.Exists(ProfilePath(profiles, imported[index].Id)),
                    "Imported metadata must not assert login and must own a fresh profile directory");
            }
            var reloaded = new AccountsStore(new FileKeyValueStore(metadataPath));
            Assert(reloaded.GetCurrentId() == existing.Id
                && AccountJsonCodec.ToJson(reloaded.All()) == AccountJsonCodec.ToJson(all),
                "A new metadata store did not reload the imported IDs, fields, image paths, and unchanged current");
            Assert(File.ReadAllText(existingMarker) == "synthetic existing profile"
                && File.ReadAllText(reservedMarker) == "synthetic reserved profile",
                "Existing or reserved profile content was overwritten");
            var copiedAvatar = imported[0].AvatarImage;
            Assert(Path.GetDirectoryName(copiedAvatar) == avatars && copiedAvatar != sourceAvatar
                && copiedAvatar != existing.AvatarImage && Path.GetExtension(copiedAvatar) == ".png"
                && File.ReadAllBytes(copiedAvatar).SequenceEqual(Png1X1)
                && File.ReadAllBytes(existing.AvatarImage).SequenceEqual(Png1X1)
                && File.ReadAllBytes(sourceAvatar).SequenceEqual(Png1X1)
                && imported[1].AvatarImage.Length == 0 && Directory.GetFiles(avatars).Length == 2,
                "Import must copy the PNG into a new avatar file and retain the other account's text-only avatar");
            var capturedProfile = ProfilePath(profiles, imported[0].Id);
            AssertProtectedPending(capturedProfile, bundle.Accounts[0]);
            Assert(!PendingCredentials.Exists(ProfilePath(profiles, imported[1].Id))
                && window.HostedSessions.Count == 0 && window.HostedControlCount == 0,
                "Uncaptured cookies must not create pending credentials; temporary import hosts must be disposed");
            Console.WriteLine("PASS: append, fresh IDs, metadata reload, avatar copy, and DPAPI pending round-trip");

            var pendingPath = Path.Combine(capturedProfile, PendingFileName);
            File.SetAttributes(pendingPath, FileAttributes.ReadOnly);
            try
            {
                await window.ActivateAccountAsync(imported[0].Id);
                Assert(window.HostedSessions.TryGetValue(imported[0].Id, out var blocked)
                    && !blocked.HasNavigated && !window.DisplayState.CanShowWebView
                    && window.DisplayState.Error?.Contains("无法清理", StringComparison.Ordinal) == true
                    && PendingCredentials.Exists(capturedProfile),
                    "Failure to delete pending credentials must block navigation and preserve the pending file");
            }
            finally { File.SetAttributes(pendingPath, FileAttributes.Normal); }
            await window.ActivateAccountAsync(imported[0].Id);
            Assert(window.HostedSessions.TryGetValue(imported[0].Id, out var active) && !active.IsDisposed,
                "Imported account did not create a live host");
            var host = active!;
            await WaitForBlankAsync(host);
            Assert(!PendingCredentials.Exists(capturedProfile)
                && !File.Exists(Path.Combine(capturedProfile, PendingFileName)),
                "First normal activation must consume the DPAPI pending file");
            await AssertLiveCookiesAsync(host, bundle.Accounts[0].Cookies);

            // CookieManager queries are local reads, including URLs identifying Muse cookie scopes.
            // Seed unrelated domains to prove export filters them without navigating to any website.
            var outsideDomains = new[] { "outside.invalid", "notmuse.ai", "muse.ai.invalid" };
            for (var index = 0; index < outsideDomains.Length; index++)
            {
                var cookie = host.Core.CookieManager.CreateCookie("transfer_outside_" + index,
                    "synthetic-outside-" + index, outsideDomains[index], "/");
                cookie.Expires = DateTime.UtcNow.AddDays(1);
                host.Core.CookieManager.AddOrUpdateCookie(cookie);
            }
            var localCookies = await host.Core.CookieManager.GetCookiesAsync(null);
            Assert(Enumerable.Range(0, outsideDomains.Length).All(index =>
                localCookies.Any(cookie => cookie.Name == "transfer_outside_" + index)),
                "Out-of-scope cookie fixtures were not inserted, so the export filter would be untested");
            Assert(host.Core.Source == "about:blank", "Smoke must navigate only to about:blank");
            Console.WriteLine("PASS: about:blank activation consumes pending and CookieManager returns the synthetic cookie values");

            var exportPath = Path.Combine(root, "roundtrip.musecred");
            var reportsBeforeExport = progress.Reports;
            await window.ExportCredentialsAsync(exportPath, Passphrase, progress);
            var exportBytes = File.ReadAllBytes(exportPath);
            CredentialBundle exported;
            try { exported = CredentialArchive.Decrypt(exportBytes, Passphrase); }
            finally { CryptographicOperations.ZeroMemory(exportBytes); }
            Assert(progress.Reports > reportsBeforeExport && exported.Accounts.Count == all.Count,
                "Export must report progress and include existing plus imported accounts");
            for (var index = 0; index < all.Count; index++)
                AssertExportedConfiguration(exported.Accounts[index], all[index]);
            Assert(exported.Accounts.All(account => account.CookiesCaptured),
                "Every synthetic profile should be readable during export, including empty profiles");
            AssertCookies(exported.Accounts[1].Cookies, bundle.Accounts[0].Cookies);
            Assert(exported.Accounts[0].Cookies.Count == 0 && exported.Accounts[2].Cookies.Count == 0
                && exported.Accounts.SelectMany(account => account.Cookies).All(cookie =>
                    IsMuseDomain(cookie.Domain) && !cookie.Name.StartsWith("transfer_outside_", StringComparison.Ordinal)),
                "Export must contain only the captured account's Muse cookies, preserving account and domain scope");
            Assert(window.Accounts.GetCurrentId() == imported[0].Id
                && window.HostedSessions.TryGetValue(imported[0].Id, out var afterExport)
                && ReferenceEquals(host, afterExport) && !host.IsDisposed && !host.Core.IsSuspended,
                "Export must retain and resume the selected browser instead of losing session Cookies");
            await AssertLiveCookiesAsync(host, bundle.Accounts[0].Cookies);
            var missingAvatarAccount = window.Accounts.FindById(existing.Id)!;
            var previousAvatar = missingAvatarAccount.AvatarImage;
            missingAvatarAccount.AvatarImage = Path.Combine(root, "missing-avatar.png");
            window.Accounts.Update(missingAvatarAccount);
            try
            {
                var rejected = false;
                try { await window.ExportCredentialsAsync(Path.Combine(root, "failed.musecred"), Passphrase, progress); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected && !host.Core.IsSuspended
                    && window.HostedSessions.TryGetValue(imported[0].Id, out var afterFailure)
                    && ReferenceEquals(host, afterFailure),
                    "Failed export must resume every paused browser and preserve the live host");
                await AssertLiveCookiesAsync(host, bundle.Accounts[0].Cookies);
            }
            finally
            {
                missingAvatarAccount.AvatarImage = previousAvatar;
                window.Accounts.Update(missingAvatarAccount);
            }
            await RunCancelledActivationAsync(Path.Combine(root, "cancelled-activation"));
            Console.WriteLine("PASS: encrypted export/decrypt preserves full proxies, avatars, persistent/session cookies, and Muse-only scope");
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(80));
                    Assert(window.HostedControlCount == 0 && window.HostedSessions.Count == 0,
                        "Closing must dispose all temporary WebView hosts before cleanup");
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }
        Console.WriteLine("PASS: credential transfer smoke completed using only synthetic temporary data and about:blank");
    }

    private static async Task RunCancelledActivationAsync(string root)
    {
        var metadata = Path.Combine(root, "Metadata");
        var profiles = Path.Combine(root, "Profiles");
        var window = new MainWindow(metadata, profiles, "about:blank");
        window.Show();
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var account = window.Accounts.AddDefault();
            var profile = ProfilePath(profiles, account.Id);
            var cookie = new CredentialCookie
            {
                Name = AccountWebHost.AuthCookieName, Value = "synthetic-cancelled-session",
                Domain = ".muse.ai", Path = "/", IsSecure = true, IsHttpOnly = true,
                IsSession = true, SameSite = "Lax",
            };
            PendingCredentials.Write(profile, new CredentialAccount
            {
                Name = account.Name, CookiesCaptured = true, Cookies = new() { cookie },
            });
            var factory = (Func<long, BrowserSessionConfiguration, CancellationToken, Task<AccountWebHost>>)
                typeof(MainWindow).GetMethod("CreateWebViewAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .CreateDelegate(typeof(Func<long, BrowserSessionConfiguration, CancellationToken, Task<AccountWebHost>>), window);
            using var cancelled = new CancellationTokenSource();
            await using (var coordinator = new SessionCoordinator<AccountWebHost, BrowserSessionConfiguration>(1,
                async (id, configuration, token) =>
                {
                    var created = await factory(id, configuration, token);
                    cancelled.Cancel();
                    return created;
                }, host => host.DisposeAsync().AsTask()))
            {
                var host = await coordinator.ActivateAsync(account.Id,
                    new BrowserSessionConfiguration("", false, profile), cancelled.Token);
                Assert(host is null && coordinator.Snapshot().Count == 0
                    && window.HostedControlCount == 0 && PendingCredentials.Exists(profile),
                    "Cancellation after restoration must retain pending session Cookies when the unpublished host exits");
            }
            await window.ActivateAccountAsync(account.Id);
            Assert(window.HostedSessions.TryGetValue(account.Id, out var restored)
                && !PendingCredentials.Exists(profile),
                "A later accepted activation must consume retained pending credentials");
            await WaitForBlankAsync(restored!);
            await AssertLiveCookiesAsync(restored!, new[] { cookie });
            Console.WriteLine("PASS: cancelled activation retains pending session Cookies and later activation restores them");
            await RunTransferRecoveryAsync(window, restored!, account.Id);
        }
        finally
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(80));
        }
    }

    private static async Task RunTransferRecoveryAsync(MainWindow window, AccountWebHost host, long accountId)
    {
        await window.ActivateAccountAsync(accountId);
        Assert(window.DisplayState.HasHost && window.DisplayState.SplashVisible,
            "Cached activation must enter its minimum splash delay before the transfer regression");
        var rejected = false;
        try
        {
            await window.ImportCredentialsAsync(Path.Combine(Path.GetTempPath(),
                "MuseLauncher-missing-" + Guid.NewGuid().ToString("N") + ".musecred"),
                Passphrase, new TransferProgress());
        }
        catch (FileNotFoundException) { rejected = true; }
        Assert(rejected && window.DisplayState.CanShowWebView && !window.DisplayState.SplashVisible,
            "Rejected transfer during the minimum splash delay must not leave the existing host covered");

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var begin = (Func<HashSet<long>>)typeof(MainWindow).GetMethod("BeginCredentialTransfer", flags)!
            .CreateDelegate(typeof(Func<HashSet<long>>), window);
        var end = (Func<IReadOnlySet<long>, Task>)typeof(MainWindow).GetMethod("EndCredentialTransferAsync", flags)!
            .CreateDelegate(typeof(Func<IReadOnlySet<long>, Task>), window);
        var snapshot = begin();
        try
        {
            host.Reload();
            await WaitUntilAsync(() => host.LoadStartAt != null);
            var version = window.DisplayState.Begin(accountId, showSplash: true);
            window.DisplayState.BindHost(accountId, version);
            await end(snapshot);
            snapshot.Clear();
            await WaitUntilAsync(() => host.LoadStartAt == null && window.DisplayState.CanShowWebView);
            Assert(window.DisplayState.Error is null && !window.DisplayState.SplashVisible,
                "Navigation completing after transfer must use a live display-delay token");
        }
        finally { if (snapshot.Count > 0) await end(snapshot); }
        Console.WriteLine("PASS: transfer restores minimum-delay splash and in-flight navigation display scheduling");

        var backgroundId = accountId;
        var current = window.Accounts.AddDefault();
        await window.ActivateAccountAsync(current.Id);
        var currentHost = window.HostedSessions[current.Id];
        await WaitForBlankAsync(currentHost);
        snapshot = begin();
        try
        {
            // Exercise the actual one-shot failure notification without killing any desktop process.
            typeof(AccountWebHost).GetMethod("ReportFailure", flags)!.Invoke(host, null);
            typeof(AccountWebHost).GetMethod("ReportFailure", flags)!.Invoke(currentHost, null);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(window.HostedSessions.ContainsKey(backgroundId) && window.HostedSessions.ContainsKey(current.Id),
                "Failures must remain queued until the credential transfer finishes");
            await end(snapshot);
            snapshot.Clear();
            Assert(host.IsDisposed && currentHost.IsDisposed && !window.HostedSessions.ContainsKey(backgroundId)
                && window.HostedSessions.TryGetValue(current.Id, out var replacement)
                && !ReferenceEquals(currentHost, replacement),
                "Transfer completion must invalidate queued background failures and rebuild the selected host");
            await WaitForBlankAsync(window.HostedSessions[current.Id]);
            await WaitUntilAsync(() => window.DisplayState.CanShowWebView);
            Console.WriteLine("PASS: actual one-shot failure callbacks queued during transfer invalidate both hosts and restore the selected page");
        }
        finally { if (snapshot.Count > 0) await end(snapshot); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Synthetic recovery condition was not reached");
            await Task.Delay(10);
        }
    }

    private static CredentialBundle CreateBundle(string duplicateName, byte[] image)
    {
        var expiry = (double)DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        return new CredentialBundle
        {
            Accounts = new()
            {
                new CredentialAccount
                {
                    Name = duplicateName, Avatar = "新甲", AvatarImage = image, AvatarExtension = ".png",
                    Proxy = "http://smoke%40user:smoke%3Apass%25%2F@127.0.0.1:9", ForceProxy = true,
                    Tags = new() { "imported", "有图片" }, CookiesCaptured = true,
                    Cookies = new()
                    {
                        new CredentialCookie
                        {
                            Name = "hatch_sess", Value = "synthetic-auth-" + Guid.NewGuid().ToString("N"),
                            Domain = ".muse.ai", Path = "/", IsSecure = true, IsHttpOnly = true,
                            IsSession = false, SameSite = "Lax", ExpiresUnixSeconds = expiry,
                        },
                        new CredentialCookie
                        {
                            Name = "transfer_session", Value = "synthetic-session-" + Guid.NewGuid().ToString("N"),
                            Domain = ".muse.ai", Path = "/", IsSecure = true, IsHttpOnly = true,
                            IsSession = true, SameSite = "None",
                        },
                        new CredentialCookie
                        {
                            Name = "transfer_subdomain", Value = "synthetic-subdomain-" + Guid.NewGuid().ToString("N"),
                            Domain = ".api.muse.ai", Path = "/smoke", IsSecure = true,
                            IsSession = false, SameSite = "Strict", ExpiresUnixSeconds = expiry,
                        },
                    },
                },
                new CredentialAccount
                {
                    Name = "Synthetic needs login", Avatar = "乙二",
                    Proxy = "socks5://second%40user:second%3Apass%25@127.0.0.1:9", ForceProxy = false,
                    Tags = new() { "imported", "text-only" }, CookiesCaptured = false,
                },
            },
        };
    }

    private static async Task AssertRejectedAsync(MainWindow window, string path, string passphrase,
        IProgress<string> progress, string metadataPath, string metadata, string profiles, long currentId,
        string? accountsJson, IReadOnlyDictionary<string, string> baseline)
    {
        var rejected = false;
        try { await window.ImportCredentialsAsync(path, passphrase, progress); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Incorrect passphrase and tampered archives must fail authentication");
        Assert(window.Accounts.GetCurrentId() == currentId
            && AccountJsonCodec.ToJson(window.Accounts.All()) == accountsJson,
            "Rejected import changed the in-memory accounts or selection");
        var reloaded = new AccountsStore(new FileKeyValueStore(metadataPath));
        Assert(reloaded.GetCurrentId() == currentId && AccountJsonCodec.ToJson(reloaded.All()) == accountsJson,
            "Rejected import changed persisted metadata");
        var after = SnapshotTree(metadata, profiles);
        Assert(baseline.Count == after.Count && baseline.All(pair =>
            after.TryGetValue(pair.Key, out var content) && content == pair.Value),
            "Rejected import added, removed, or changed an avatar, metadata file, or profile directory");
        Assert(window.HostedSessions.Count == 0 && window.HostedControlCount == 0,
            "Authentication failure must not initialize a browser");
    }

    private static void AssertProtectedPending(string profile, CredentialAccount expected)
    {
        Assert(PendingCredentials.Exists(profile), "Captured cookies must have a pending credentials file");
        var pending = PendingCredentials.Read(profile);
        Assert(pending.Name == expected.Name && pending.CookiesCaptured,
            "Pending credentials did not retain the imported account identity");
        AssertCookies(pending.Cookies, expected.Cookies);
        var encrypted = File.ReadAllBytes(Path.Combine(profile, PendingFileName));
        byte[]? plain = null;
        try
        {
            foreach (var cookie in expected.Cookies)
                Assert(encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(cookie.Value)) < 0,
                    "Pending credentials exposed a plaintext synthetic cookie value");
            plain = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("MuseLauncher.PendingCookies.v1"),
                DataProtectionScope.CurrentUser);
            Assert(!encrypted.SequenceEqual(plain) && plain.Length > 0 && plain[0] == (byte)'{',
                "Pending file must be current-user DPAPI ciphertext rather than plaintext JSON");
            foreach (var cookie in expected.Cookies)
                Assert(plain.AsSpan().IndexOf(Encoding.UTF8.GetBytes(cookie.Value)) >= 0,
                    "The DPAPI payload did not contain the expected synthetic cookie");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static async Task WaitForBlankAsync(AccountWebHost host)
    {
        var core = host.Core;
        if (host.Loaded)
        {
            Assert(!host.Errored && core.Source == "about:blank", "Unexpected activation navigation");
            return;
        }
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess && core.Source == "about:blank") navigation.TrySetResult();
            else navigation.TrySetException(new InvalidOperationException("about:blank navigation failed"));
        }
        core.NavigationCompleted += Completed;
        try { await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { core.NavigationCompleted -= Completed; }
        Assert(host.Loaded && !host.Errored && core.Source == "about:blank",
            "Activation did not complete about:blank navigation");
    }

    private static async Task AssertLiveCookiesAsync(AccountWebHost host, IReadOnlyList<CredentialCookie> expected)
    {
        foreach (var source in expected)
        {
            // GetCookiesAsync is a local CookieManager read, not an HTTP request.
            var cookies = await host.Core.CookieManager.GetCookiesAsync(
                "https://" + CanonicalDomain(source.Domain) + source.Path);
            var actual = cookies.SingleOrDefault(cookie => cookie.Name == source.Name
                && CanonicalDomain(cookie.Domain) == CanonicalDomain(source.Domain) && cookie.Path == source.Path);
            Assert(actual is not null && actual.Value == source.Value && actual.IsSecure == source.IsSecure
                && actual.IsHttpOnly == source.IsHttpOnly && actual.IsSession == source.IsSession
                && actual.SameSite.ToString() == source.SameSite,
                "CookieManager did not restore the synthetic cookie value and attributes");
            if (!source.IsSession)
                Assert(actual is not null && source.ExpiresUnixSeconds is double expiry
                    && Math.Abs(new DateTimeOffset(DateTime.SpecifyKind(actual.Expires, DateTimeKind.Utc))
                        .ToUnixTimeSeconds() - expiry) < 1,
                    "Persistent Muse cookie expiry was not restored");
        }
    }

    private static void AssertConfiguration(Account actual, CredentialAccount expected)
        => Assert(actual.Name == expected.Name && actual.Avatar == expected.Avatar
            && actual.Proxy == expected.Proxy && actual.ForceProxy == expected.ForceProxy
            && actual.Tags.SequenceEqual(expected.Tags),
            "Imported account changed its name, text avatar, full proxy credentials, flags, or tags");

    private static void AssertExportedConfiguration(CredentialAccount actual, Account expected)
    {
        AssertConfiguration(expected, actual);
        if (expected.AvatarImage.Length == 0)
            Assert(actual.AvatarImage is null && actual.AvatarExtension.Length == 0,
                "Text-only avatar unexpectedly exported an image");
        else
            Assert(actual.AvatarImage is not null
                && actual.AvatarImage.SequenceEqual(File.ReadAllBytes(expected.AvatarImage))
                && actual.AvatarExtension == Path.GetExtension(expected.AvatarImage),
                "Exported avatar image bytes or extension changed");
    }

    private static void AssertCookies(IReadOnlyList<CredentialCookie> actual, IReadOnlyList<CredentialCookie> expected)
    {
        Assert(actual.Count == expected.Count, "Cookie collection has missing or out-of-scope entries");
        foreach (var source in expected)
        {
            var restored = actual.SingleOrDefault(cookie => cookie.Name == source.Name
                && CanonicalDomain(cookie.Domain) == CanonicalDomain(source.Domain) && cookie.Path == source.Path);
            Assert(restored is not null && restored.Value == source.Value && restored.IsSecure == source.IsSecure
                && restored.IsHttpOnly == source.IsHttpOnly && restored.IsSession == source.IsSession
                && restored.SameSite == source.SameSite
                && ((restored.ExpiresUnixSeconds is null && source.ExpiresUnixSeconds is null)
                    || (restored.ExpiresUnixSeconds is double restoredExpiry
                        && source.ExpiresUnixSeconds is double sourceExpiry && Math.Abs(restoredExpiry - sourceExpiry) < 1)),
                "Synthetic cookie identity, value, expiry, session state, or security attributes changed");
        }
    }

    private static Dictionary<string, string> SnapshotTree(params string[] roots)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < roots.Length; index++)
        {
            var root = roots[index];
            snapshot[index + "/"] = Directory.Exists(root) ? "directory" : "absent";
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
                snapshot[index + "/" + Path.GetRelativePath(root, path)] = Directory.Exists(path)
                    ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return snapshot;
    }

    private static string ProfilePath(string profiles, long id) => Path.Combine(profiles, "acct_" + id);
    private static string CanonicalDomain(string domain) => domain.TrimStart('.').ToLowerInvariant();
    private static bool IsMuseDomain(string domain)
        => CanonicalDomain(domain) is var canonical
            && (canonical == "muse.ai" || canonical.EndsWith(".muse.ai", StringComparison.Ordinal));
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TransferProgress : IProgress<string>
    {
        internal int Reports { get; private set; }
        public void Report(string value) => Reports++;
    }
}
