using MuseLauncher.Core;
using Xunit;

namespace MuseLauncher.Core.Tests;

public sealed class V11FeatureTests
{
    [Fact]
    public void ThemeHelper_PreservesExistingCookieFieldsAndUpdatesMode()
    {
        var existing = Uri.EscapeDataString("{\"mode\":\"light\",\"accent\":\"blue\",\"chatTheme\":\"ocean\"}");
        var updated = ThemeHelper.BuildUpdatedThemeCookieValue(existing, isDark: true);
        var decoded = Uri.UnescapeDataString(updated);

        Assert.Contains("\"mode\":\"dark\"", decoded);
        Assert.Contains("\"accent\":\"blue\"", decoded);
        Assert.Contains("\"chatTheme\":\"ocean\"", decoded);
    }

    [Fact]
    public void ThemeHelper_HandlesEmptyOrInvalidCookie()
    {
        var updated = ThemeHelper.BuildUpdatedThemeCookieValue("not-json", isDark: false);
        Assert.Equal("{\"mode\":\"light\"}", Uri.UnescapeDataString(updated));

        var script = ThemeHelper.BuildDomThemeSyncScript(true);
        Assert.Contains("classList.add(mode)", script);
        Assert.Contains("('dark')", script);
    }

    [Fact]
    public void MuseUrlHelper_ClassifiesMuseAndExternalPages()
    {
        Assert.True(MuseUrlHelper.IsMusePage("https://muse.ai/"));
        Assert.True(MuseUrlHelper.IsMusePage("https://app.muse.ai/chat/123"));
        Assert.False(MuseUrlHelper.IsMusePage("https://notmuse.ai/"));
        Assert.False(MuseUrlHelper.IsExternalPage("https://muse.ai/"));
        Assert.False(MuseUrlHelper.IsExternalPage("about:blank"));
        Assert.False(MuseUrlHelper.IsExternalPage("blob:https://muse.ai/1234"));
        Assert.True(MuseUrlHelper.IsExternalPage("https://example.com/article"));
        Assert.Equal("example.com", MuseUrlHelper.DisplayHost("https://example.com/article"));
    }

    [Fact]
    public void MuseUrlHelper_AllowsBlobDataAndTrustedPermissions()
    {
        Assert.True(MuseUrlHelper.IsAllowedNavigationUri("https://muse.ai/"));
        Assert.True(MuseUrlHelper.IsAllowedNavigationUri("about:blank"));
        Assert.True(MuseUrlHelper.IsAllowedNavigationUri("blob:https://muse.ai/abc"));
        Assert.True(MuseUrlHelper.IsAllowedNavigationUri("data:text/html,hello"));
        Assert.False(MuseUrlHelper.IsAllowedNavigationUri("file:///C:/Windows/System32"));

        Assert.True(MuseUrlHelper.IsTrustedWebPermissionOrigin("https://muse.ai/call"));
        Assert.True(MuseUrlHelper.IsTrustedWebPermissionOrigin("https://cdn.metaaiusercontent.com/x"));
        Assert.False(MuseUrlHelper.IsTrustedWebPermissionOrigin("https://evil.example/"));
        Assert.True(MuseUrlHelper.IsInAppAuthHost("https://accounts.google.com/o/oauth2"));
        Assert.False(MuseUrlHelper.IsInAppAuthHost("https://github.com/"));
    }

    [Fact]
    public void AppUpdateHelper_ComparesSemverAndPreReleaseCorrectly()
    {
        Assert.True(AppUpdateHelper.IsNewerVersion("v1.1.0", "1.0.4"));
        Assert.True(AppUpdateHelper.IsNewerVersion("1.1.0", "1.1.0-beta.1"));
        Assert.True(AppUpdateHelper.IsNewerVersion("1.1.0-beta.2", "1.1.0-beta.1"));
        Assert.True(AppUpdateHelper.IsNewerVersion("1.2.0-beta.1", "1.1.0"));
        Assert.False(AppUpdateHelper.IsNewerVersion("1.1.0-beta.1", "1.1.0"));
        Assert.False(AppUpdateHelper.IsNewerVersion("v1.0.4", "1.0.4"));
        Assert.False(AppUpdateHelper.IsNewerVersion("invalid", "1.0.4"));
    }

    [Fact]
    public void AppUpdateHelper_ParsesReleaseJsonAndSkipsSmokeZip()
    {
        const string json = """
        {
          "tag_name": "v1.1.0",
          "name": "Muse Launcher v1.1.0",
          "body": "## 更新内容\n- **日夜模式** 联动\n- `图片头像` 支持",
          "html_url": "https://github.com/seekstars306/muse-launcher-windows/releases/tag/v1.1.0",
          "assets": [
            {
              "name": "MuseLauncher-WindowsSmoke-v1.1.0.zip",
              "browser_download_url": "https://example.com/smoke.zip",
              "size": 1000
            },
            {
              "name": "MuseLauncher-win-x64-v1.1.0.zip",
              "browser_download_url": "https://example.com/main.zip",
              "size": 52428800
            }
          ]
        }
        """;

        var info = AppUpdateHelper.ParseReleaseJson(json);
        Assert.NotNull(info);
        Assert.Equal("1.1.0", info!.VersionName);
        Assert.Equal("MuseLauncher-win-x64-v1.1.0.zip", info.AssetName);
        Assert.Equal("https://example.com/main.zip", info.AssetUrl);
        Assert.Equal(52428800, info.AssetSizeBytes);
        Assert.Contains("• 日夜模式 联动", info.Notes);
        Assert.Contains("• 图片头像 支持", info.Notes);
    }

    [Fact]
    public void AppUpdateHelper_RespectsAutoCheckInterval()
    {
        Assert.True(AppUpdateHelper.ShouldAutoCheck(0, 1000));
        Assert.False(AppUpdateHelper.ShouldAutoCheck(1000, 1000 + AppUpdateHelper.AutoCheckIntervalMs - 1));
        Assert.True(AppUpdateHelper.ShouldAutoCheck(1000, 1000 + AppUpdateHelper.AutoCheckIntervalMs));
    }

    [Fact]
    public void IdleSuspendPolicy_SkipsCurrentSuspendedAndBusySessions()
    {
        const long now = 1_000_000;
        var list = new[]
        {
            new IdleSuspendPolicy.SessionActivity(1, IsCurrent: true, IsSuspended: false, IsBusy: false, now - 500_000),
            new IdleSuspendPolicy.SessionActivity(2, IsCurrent: false, IsSuspended: true, IsBusy: false, now - 500_000),
            new IdleSuspendPolicy.SessionActivity(3, IsCurrent: false, IsSuspended: false, IsBusy: true, now - 500_000),
            new IdleSuspendPolicy.SessionActivity(4, IsCurrent: false, IsSuspended: false, IsBusy: false, now - 60_000),
            new IdleSuspendPolicy.SessionActivity(5, IsCurrent: false, IsSuspended: false, IsBusy: false, now - 200_000),
        };

        var toSuspend = IdleSuspendPolicy.SelectAccountsToSuspend(list, now);
        Assert.Equal(new[] { 5L }, toSuspend);
    }

    [Fact]
    public void LaunchCommands_ParseSafeNavigation_OnlyAllowsAccountAndFullscreen()
    {
        var cmd = LaunchCommands.ParseSafeNavigation(new[]
        {
            "--account=3", "--fullscreen=true", "--del_account=3", "--add_account", "--dump_auth", "--show_drawer"
        });
        Assert.Equal(3, cmd.StartAccount);
        Assert.True(cmd.HasFullscreen);
        Assert.True(cmd.Fullscreen);
        Assert.False(cmd.HasDelAccount);
        Assert.False(cmd.AddAccount);
        Assert.False(cmd.DumpAuth);
        Assert.False(cmd.ShowDrawer);
    }

    [Fact]
    public void AccountJsonCodec_RoundTripsAvatarImageAndRemainsBackwardCompatible()
    {
        var acc = new Account(1, "测试号") { Avatar = "测", AvatarImage = "acct_1_123.png" };
        var json = AccountJsonCodec.ToJson(new[] { acc });
        Assert.NotNull(json);
        var parsed = AccountJsonCodec.FromJsonOrNull(json);
        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        Assert.Equal("acct_1_123.png", parsed![0].AvatarImage);

        // 旧格式无 avatarImage 字段也能正常反序列化
        var legacy = AccountJsonCodec.FromJsonOrNull("[{\"id\":2,\"name\":\"旧账号\",\"avatar\":\"m\"}]");
        Assert.NotNull(legacy);
        Assert.Equal("", legacy![0].AvatarImage);
    }

    [Fact]
    public void AccountsStore_PersistsThemeDownloadDirAndUpdateCheck()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "muse-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "accounts.json");
            var store1 = new AccountsStore(new FileKeyValueStore(path));
            Assert.True(store1.IsDarkTheme());
            Assert.Equal("", store1.GetDownloadDirectory());
            Assert.Equal(0, store1.GetLastUpdateCheckAt());

            store1.SetDarkTheme(false);
            store1.SetDownloadDirectory(@"D:\MuseDownloads");
            store1.SetLastUpdateCheckAt(1730000000000L);

            var store2 = new AccountsStore(new FileKeyValueStore(path));
            Assert.False(store2.IsDarkTheme());
            Assert.Equal(@"D:\MuseDownloads", store2.GetDownloadDirectory());
            Assert.Equal(1730000000000L, store2.GetLastUpdateCheckAt());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
