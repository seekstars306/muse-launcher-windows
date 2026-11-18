using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseLauncher;
using MuseLauncher.Core;

namespace MuseLauncher.WindowsSmoke;

/// <summary>Independent WPF Cookie smoke: synthetic profiles, temporary logs, about:blank only.</summary>
public static class CredentialCookieSmoke
{
    private const string Secret = "synthetic-cookie-secret-do-not-log";

    public static async Task RunAsync()
    {
        var application = Application.Current;
        Assert(application is not null && application is not App,
            "Cookie smoke requires the independent STA smoke Application");
        application!.Dispatcher.VerifyAccess();
        var root = Path.Combine(Path.GetTempPath(), "MuseLauncherCookieSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Log.ConfigureDirectoryForTests(Path.Combine(root, "log"));
        Log.I("CookieSmoke", "Starting isolated Cookie smoke");
        var panel = new Grid();
        var window = new Window
        {
            Width = 320, Height = 240, ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow, Content = panel,
        };
        try
        {
            window.Show();
            await using var source = await CookieBrowser.CreateAsync(panel, Path.Combine(root, "source-profile"));
            await using var target = await CookieBrowser.CreateAsync(panel, Path.Combine(root, "target-profile"));
            SeedCookies(source.Host.Core.CookieManager);
            var exported = await source.Host.ExportMuseCookiesAsync();
            Assert(exported.Count == 7, "Export must include all seven live muse.ai Cookies and no others");
            Assert(exported.Count(cookie => cookie.Name == "smoke_domain_identity") == 2
                && exported.Any(cookie => cookie.Name == "smoke_domain_identity" && cookie.Domain == "muse.ai")
                && exported.Any(cookie => cookie.Name == "smoke_domain_identity" && cookie.Domain == ".muse.ai"),
                "Host-only and domain Cookies with matching names and paths must coexist");
            Assert(exported.Any(cookie => cookie.Name == AccountWebHost.AuthCookieName
                && cookie.IsSession && cookie.ExpiresUnixSeconds is null && cookie.IsHttpOnly && cookie.IsSecure),
                "Export must preserve session, HttpOnly and Secure auth attributes");
            Assert(exported.Any(cookie => cookie.Domain == ".cdn.muse.ai" && cookie.Path == "/media"),
                "Whole-profile export must include subdomain Cookies with non-root paths");
            Assert(exported.Count(cookie => cookie.Name == "smoke_path") == 2,
                "Cookies with the same name and different paths must remain distinct");
            var native = await source.Host.Core.CookieManager.GetCookiesAsync(null);
            foreach (var cookie in exported) AssertMatches(native, cookie);

            Assert(!await target.Host.RestoreMuseCookiesAsync(Array.Empty<CredentialCookie>()),
                "Empty input must request login");
            Assert(!await target.Host.RestoreMuseCookiesAsync(
                exported.Where(cookie => cookie.Name != AccountWebHost.AuthCookieName).ToList()),
                "Missing auth Cookie must request login");
            var expiredAuth = MakeCookie(AccountWebHost.AuthCookieName, ".muse.ai", "/", expiry:
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
            Assert(!await target.Host.RestoreMuseCookiesAsync(exported
                .Where(cookie => cookie.Name != AccountWebHost.AuthCookieName).Append(expiredAuth).ToList()),
                "Expired persistent Cookie must request login");
            Assert((await target.Host.Core.CookieManager.GetCookiesAsync(null)).Count == 0,
                "Missing or expired auth must not write partial Cookies");

            foreach (var invalid in new[]
            {
                MakeCookie(" invalid", ".muse.ai", "/"),
                MakeCookie("invalid_domain", "muse.ai.synthetic.example", "/"),
                MakeCookie("invalid_enum", ".muse.ai", "/", sameSite: "123"),
            })
                await AssertSanitizedAsync(async () =>
                    await target.Host.RestoreMuseCookiesAsync(exported.Append(invalid).ToList()));
            await AssertSanitizedAsync(async () =>
                await target.Host.RestoreMuseCookiesAsync(exported.Append(exported[0]).ToList()));
            Assert((await target.Host.Core.CookieManager.GetCookiesAsync(null)).Count == 0,
                "Archive validation must finish before writing any Cookie");

            var expiredAuxiliary = MakeCookie("expired_auxiliary", ".muse.ai", "/",
                expiry: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
            Assert(await target.Host.RestoreMuseCookiesAsync(exported.Append(expiredAuxiliary).ToList()),
                "An expired auxiliary Cookie must not block live authentication Cookies");
            Assert(!(await target.Host.Core.CookieManager.GetCookiesAsync(null))
                .Any(cookie => cookie.Name == expiredAuxiliary.Name),
                "Expired auxiliary Cookies must be skipped rather than reintroduced");
            Assert(await target.Host.RestoreMuseCookiesAsync(exported), "Valid Cookie restore must succeed");
            var restored = await target.Host.Core.CookieManager.GetCookiesAsync(null);
            Assert(restored.Count == exported.Count, "Restore must not introduce unrelated Cookies");
            foreach (var cookie in exported) AssertMatches(restored, cookie);
            var roundTrip = await target.Host.ExportMuseCookiesAsync();
            Assert(roundTrip.Count == exported.Count, "Restored profile must remain exportable");
            foreach (var cookie in roundTrip) AssertMatches(native, cookie);
            source.AssertBlankOnly();
            target.AssertBlankOnly();

            await target.DisposeAsync();
            await AssertSanitizedAsync(async () => await target.Host.ExportMuseCookiesAsync());
            await AssertSanitizedAsync(async () => await target.Host.RestoreMuseCookiesAsync(exported));
            Assert(!File.ReadAllText(Log.FilePath).Contains(Secret, StringComparison.Ordinal),
                "Temporary logs must not contain Cookie values");
            Console.WriteLine("PASS: whole-profile muse.ai Cookie export/restore, attributes, validation and sanitized failures; about:blank only");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("NOTE: runtime still holds the synthetic Cookie smoke directory: " + root);
            }
        }
    }

    private static CredentialCookie MakeCookie(string name, string domain, string path,
        bool secure = true, bool httpOnly = false, string sameSite = "Lax", double? expiry = null)
        => new()
        {
            Name = name, Value = Secret + "-" + name + path, Domain = domain, Path = path,
            IsSecure = secure, IsHttpOnly = httpOnly, SameSite = sameSite,
            IsSession = expiry is null, ExpiresUnixSeconds = expiry,
        };

    private static void SeedCookies(CoreWebView2CookieManager manager)
    {
        var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600.125;
        var cookies = new[]
        {
            MakeCookie(AccountWebHost.AuthCookieName, ".muse.ai", "/", httpOnly: true, sameSite: "None"),
            MakeCookie("smoke_host", "muse.ai", "/", secure: false),
            MakeCookie("smoke_domain_identity", "muse.ai", "/"),
            MakeCookie("smoke_domain_identity", ".muse.ai", "/"),
            MakeCookie("smoke_subdomain", ".cdn.muse.ai", "/media", sameSite: "Strict", expiry: expiry),
            MakeCookie("smoke_path", ".muse.ai", "/one", httpOnly: true, expiry: expiry),
            MakeCookie("smoke_path", ".muse.ai", "/two", secure: false),
            MakeCookie("excluded_unrelated", "synthetic.example", "/"),
            MakeCookie("excluded_suffix", "evilmuse.ai", "/"),
            MakeCookie("excluded_prefix", "muse.ai.synthetic.example", "/"),
            MakeCookie("expired", ".muse.ai", "/", expiry: expiry - 7200),
        };
        foreach (var source in cookies)
        {
            var cookie = manager.CreateCookie(source.Name, source.Value, source.Domain, source.Path);
            cookie.IsSecure = source.IsSecure;
            cookie.IsHttpOnly = source.IsHttpOnly;
            cookie.SameSite = Enum.Parse<CoreWebView2CookieSameSiteKind>(source.SameSite);
            if (source.ExpiresUnixSeconds is double seconds)
                cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)).UtcDateTime;
            manager.AddOrUpdateCookie(cookie);
        }
    }

    private static void AssertMatches(IReadOnlyList<CoreWebView2Cookie> native, CredentialCookie expected)
    {
        // Compare the exact domain exposed by WebView2, including its leading dot when present.
        var actual = native.SingleOrDefault(cookie => cookie.Name == expected.Name
            && cookie.Domain == expected.Domain && cookie.Path == expected.Path);
        Assert(actual is not null, "Cookie domain/path/name identity must be preserved");
        Assert(actual!.Value == expected.Value && actual.IsSecure == expected.IsSecure
            && actual.IsHttpOnly == expected.IsHttpOnly && actual.IsSession == expected.IsSession
            && actual.SameSite.ToString() == expected.SameSite, "Every Cookie attribute must round-trip");
        if (actual.IsSession)
            Assert(expected.ExpiresUnixSeconds is null, "Session Cookie expiry must remain null");
        else
        {
            var seconds = new DateTimeOffset(DateTime.SpecifyKind(actual.Expires, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds() / 1000d;
            Assert(expected.ExpiresUnixSeconds is double expiry && Math.Abs(seconds - expiry) < 0.001,
                "Persistent Cookie Unix expiry must round-trip to millisecond precision");
        }
    }

    private static async Task AssertSanitizedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidDataException ex)
        {
            Assert(ex.InnerException is null && !ex.ToString().Contains(Secret, StringComparison.Ordinal),
                "Cookie failures must have sanitized messages and no inner exception");
            return;
        }
        throw new InvalidOperationException("Invalid Cookie operation must throw InvalidDataException");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CookieBrowser : IAsyncDisposable
    {
        private readonly CoreWebView2Environment environment;
        private readonly WebView2 control = new();
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int navigationCount;
        private bool blockedNavigation;
        private bool disposed;
        public AccountWebHost Host { get; private set; } = null!;

        private CookieBrowser(CoreWebView2Environment environment)
        {
            this.environment = environment;
            environment.BrowserProcessExited += OnExited;
        }

        public static async Task<CookieBrowser> CreateAsync(Panel panel, string profile)
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, profile,
                new CoreWebView2EnvironmentOptions("--disable-background-networking --no-first-run")
                { ExclusiveUserDataFolderAccess = true });
            var browser = new CookieBrowser(environment);
            try
            {
                panel.Children.Add(browser.control);
                await browser.control.EnsureCoreWebView2Async(environment);
                browser.Host = new AccountWebHost(1, profile, browser.control);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                browser.Host.Core.NavigationStarting += (_, e) =>
                {
                    browser.navigationCount++;
                    if (e.Uri == "about:blank") return;
                    browser.blockedNavigation = true;
                    e.Cancel = true;
                };
                browser.Host.Core.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess) ready.TrySetResult();
                    else ready.TrySetException(new InvalidOperationException("about:blank navigation failed"));
                };
                browser.Host.Core.Navigate("about:blank");
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                return browser;
            }
            catch { await browser.DisposeAsync(); throw; }
        }

        public void AssertBlankOnly() => Assert(navigationCount == 1 && !blockedNavigation,
            "Cookie operations must not navigate; the fixture permits only its initial about:blank");

        private void OnExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => exited.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            var initialized = control.CoreWebView2 is not null;
            try
            {
                if (Host is not null) await Host.DisposeAsync();
                else
                {
                    if (control.Parent is Panel panel) panel.Children.Remove(control);
                    control.Dispose();
                }
                if (initialized) await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { environment.BrowserProcessExited -= OnExited; }
        }
    }
}
