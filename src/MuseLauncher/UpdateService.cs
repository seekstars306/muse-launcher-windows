using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>
/// 对接 GitHub Releases 的更新检查与下载服务：
/// 自动复用当前账号代理（经 LoopbackProxyBridge），代理不可用时自动回退直连。
/// </summary>
public static class UpdateService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public static string CurrentVersion
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.4";
        }
    }

    public static async Task<AppUpdateHelper.ReleaseInfo?> FetchLatestReleaseAsync(
        string? accountProxy, CancellationToken cancellationToken)
    {
        ProxyConfiguration? proxy = null;
        try { proxy = ProxyConfiguration.Parse(accountProxy); } catch { }

        if (proxy != null)
        {
            try
            {
                return await FetchWithProxyAsync(proxy, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.I("Update", "proxy check failed, fallback direct: " + ex.GetType().Name);
            }
        }
        return await FetchDirectAsync(cancellationToken);
    }

    public static async Task<string> DownloadAssetAsync(
        AppUpdateHelper.ReleaseInfo release,
        string destinationDirectory,
        string? accountProxy,
        IProgress<(long Downloaded, long Total)> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(release.AssetUrl))
            throw new InvalidOperationException("该版本未提供 Windows 安装包下载链接");

        Directory.CreateDirectory(destinationDirectory);
        var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(release.AssetName)
            ? $"MuseLauncher-win-x64-{release.TagName}.zip" : release.AssetName);
        var finalPath = Path.Combine(destinationDirectory, safeName);
        var tempPath = finalPath + ".downloading";

        ProxyConfiguration? proxy = null;
        try { proxy = ProxyConfiguration.Parse(accountProxy); } catch { }

        try
        {
            if (proxy != null)
            {
                try
                {
                    await DownloadCoreAsync(release.AssetUrl, tempPath, proxy, progress, cancellationToken);
                    File.Move(tempPath, finalPath, overwrite: true);
                    return finalPath;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.I("Update", "proxy download failed, fallback direct: " + ex.GetType().Name);
                    TryDelete(tempPath);
                }
            }

            await DownloadCoreAsync(release.AssetUrl, tempPath, null, progress, cancellationToken);
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task<AppUpdateHelper.ReleaseInfo?> FetchWithProxyAsync(
        ProxyConfiguration proxy, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        await using var bridge = await LoopbackProxyBridge.StartAsync(proxy, timeout.Token);
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(bridge.Address)
            {
                Credentials = new NetworkCredential(bridge.Username, bridge.Password),
            },
        };
        using var client = CreateClient(handler, CheckTimeout);
        using var response = await client.GetAsync(AppUpdateHelper.ReleasesApiUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        return AppUpdateHelper.ParseReleaseJson(json);
    }

    private static async Task<AppUpdateHelper.ReleaseInfo?> FetchDirectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        using var handler = new HttpClientHandler();
        using var client = CreateClient(handler, CheckTimeout);
        using var response = await client.GetAsync(AppUpdateHelper.ReleasesApiUrl, timeout.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        return AppUpdateHelper.ParseReleaseJson(json);
    }

    private static async Task DownloadCoreAsync(
        string url,
        string tempPath,
        ProxyConfiguration? proxy,
        IProgress<(long Downloaded, long Total)> progress,
        CancellationToken cancellationToken)
    {
        LoopbackProxyBridge? bridge = null;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true };
            if (proxy != null)
            {
                bridge = await LoopbackProxyBridge.StartAsync(proxy, cancellationToken);
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(bridge.Address)
                {
                    Credentials = new NetworkCredential(bridge.Username, bridge.Password),
                };
            }
            using var client = CreateClient(handler, DownloadTimeout);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[65536];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress.Report((downloaded, total));
            }
        }
        finally
        {
            if (bridge != null) await bridge.DisposeAsync();
        }
    }

    private static HttpClient CreateClient(HttpClientHandler handler, TimeSpan timeout)
    {
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MuseLauncher-Windows/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
