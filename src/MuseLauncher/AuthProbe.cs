using System.IO;
using Microsoft.Data.Sqlite;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>
/// 登录态探测（对应安卓的分块 CookieManager 探测，但 Windows 上逐账号浏览器进程较重）：
/// - 活动中的 webview：直接用其 CookieManager 异步读 muse.ai cookie（实时、准确）；
/// - 未活动的账号：尽力而为读其用户数据目录里的 Cookies SQLite 库的 cookie 名
///   （Chromium 库里 name 明文，无需解密值），失败保留上次状态。
/// </summary>
public static class AuthProbe
{
    /// <summary>从磁盘上的 Cookies 库读 host 含 muse.ai 的 cookie 名。失败返回 null（未知态）。</summary>
    public static List<string>? ProbeCookieNamesOnDisk(long accountId)
    {
        var udf = AccountWebHost.ProfileDir(accountId);
        // 新版路径 {udf}\Default\Network\Cookies，旧版 {udf}\Default\Cookies
        var candidates = new[]
        {
            Path.Combine(udf, "Default", "Network", "Cookies"),
            Path.Combine(udf, "Default", "Cookies"),
        };
        var db = candidates.FirstOrDefault(File.Exists);
        if (db == null) return null;

        var tmpDir = Path.Combine(Path.GetTempPath(), "MuseLauncherProbe", "acct_" + accountId);
        try
        {
            Directory.CreateDirectory(tmpDir);
            // 连 -wal/-shm 一起拷走，浏览器进程不在时库不被锁；在时最多读到稍旧数据
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = db + suffix;
                if (File.Exists(src)) File.Copy(src, Path.Combine(tmpDir, "Cookies" + suffix), overwrite: true);
            }

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(tmpDir, "Cookies"),
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM cookies WHERE host_key LIKE '%muse.ai%'";
            var names = new List<string>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) names.Add(reader.GetString(0));
            }
            return names;
        }
        catch
        {
            return null; // 未知态：库被锁/版本变化/无该表
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>cookie 名列表 → 是否已登录（只认 hatch_sess，避免埋点 cookie 误判）。</summary>
    public static bool IsLogged(IReadOnlyList<string>? cookieNames)
    {
        return cookieNames != null && cookieNames.Contains(AccountWebHost.AuthCookieName);
    }

    /// <summary>后台逐个探测未活动账号的登录态，返回 (id, logged) 列表。</summary>
    public static async Task<List<(long Id, bool? Logged)>> ProbeAccountsAsync(IEnumerable<Account> accounts)
    {
        var result = new List<(long, bool?)>();
        foreach (var a in accounts)
        {
            var names = await Task.Run(() => ProbeCookieNamesOnDisk(a.Id));
            result.Add((a.Id, names == null ? null : IsLogged(names)));
            await Task.Yield();
        }
        return result;
    }
}
