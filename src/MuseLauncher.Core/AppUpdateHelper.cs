using System.Text;
using System.Text.Json;

namespace MuseLauncher.Core;

/// <summary>
/// 应用内在线更新检查与版本比较工具（对接 GitHub Releases，纯逻辑层可单测）。
/// </summary>
public static class AppUpdateHelper
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/seekstars306/muse-launcher-windows/releases/latest";
    public const string ReleasesWebUrl =
        "https://github.com/seekstars306/muse-launcher-windows/releases/latest";
    public static readonly long AutoCheckIntervalMs = (long)TimeSpan.FromHours(24).TotalMilliseconds;

    public sealed record ReleaseInfo(
        string TagName,
        string VersionName,
        string Title,
        string Notes,
        string AssetUrl,
        string AssetName,
        long AssetSizeBytes,
        string HtmlUrl);

    public static bool ShouldAutoCheck(long lastCheckAtMs, long nowMs)
        => lastCheckAtMs <= 0 || (nowMs - lastCheckAtMs) >= AutoCheckIntervalMs;

    /// <summary>去掉前导 v/V 与首尾空白。</summary>
    public static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..].Trim();
        return trimmed;
    }

    /// <summary>
    /// 比较远端版本是否严格高于当前版本（支持 1.0.4 vs 1.1.0、1.1.0-beta.1 vs 1.1.0、1.1.0-beta.1 vs 1.1.0-beta.2）。
    /// 规则：主次修订号相同的情况下，正式版 > 预发布版；同为预发布版时按字典序/数字尾段比较。
    /// </summary>
    public static bool IsNewerVersion(string? remoteRaw, string? currentRaw)
    {
        if (!TryParseVersion(remoteRaw, out var remoteParts, out var remotePre)) return false;
        if (!TryParseVersion(currentRaw, out var currentParts, out var currentPre)) return false;

        var maxLen = Math.Max(remoteParts.Length, currentParts.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var rv = i < remoteParts.Length ? remoteParts[i] : 0;
            var cv = i < currentParts.Length ? currentParts[i] : 0;
            if (rv != cv) return rv > cv;
        }

        // 数字段完全相等：正式版（无 pre）严格高于预发布版（有 pre）
        if (string.IsNullOrEmpty(remotePre) && !string.IsNullOrEmpty(currentPre)) return true;
        if (!string.IsNullOrEmpty(remotePre) && string.IsNullOrEmpty(currentPre)) return false;
        if (string.IsNullOrEmpty(remotePre) && string.IsNullOrEmpty(currentPre)) return false;

        return ComparePreRelease(remotePre!, currentPre!) > 0;
    }

    /// <summary>解析 GitHub Releases latest JSON，优先选择主安装包 .zip（跳过 WindowsSmoke 测试包）。</summary>
    public static ReleaseInfo? ParseReleaseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            var version = NormalizeVersion(tag);
            if (string.IsNullOrEmpty(version)) return null;

            var title = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) title = tag;
            var rawBody = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(htmlUrl)) htmlUrl = ReleasesWebUrl;

            var assetUrl = "";
            var assetName = "";
            var assetSize = 0L;
            var bestScore = -1;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    var dl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                    var size = asset.TryGetProperty("size", out var asz) && asz.ValueKind == JsonValueKind.Number
                        ? asz.GetInt64() : 0L;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dl)) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                    var lower = name.ToLowerInvariant();
                    if (lower.Contains("smoke")) continue;

                    var score = 1;
                    if (lower.Contains("win-x64")) score += 3;
                    if (lower.StartsWith("muselauncher")) score += 2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        assetName = name;
                        assetUrl = dl;
                        assetSize = size;
                    }
                }
            }

            return new ReleaseInfo(tag, version, title.Trim(), CleanReleaseNotes(rawBody), assetUrl, assetName, assetSize, htmlUrl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>将 GitHub Release Markdown 正文整理为适合原生弹窗阅读的清晰纯文本。</summary>
    public static string CleanReleaseNotes(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return "";
        var lines = rawBody.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var prevBlank = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (sb.Length > 0 && !prevBlank) sb.AppendLine();
                prevBlank = true;
                continue;
            }
            if (line.StartsWith("```")) continue;
            // 去除 Markdown 标题前缀 # / ## / ###
            while (line.StartsWith('#')) line = line[1..].TrimStart();
            // 列表项统一为 •
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..].TrimStart();
            // 去掉粗体和行内代码标记
            line = line.Replace("**", "").Replace("`", "");
            if (line.Length == 0) continue;
            sb.AppendLine(line);
            prevBlank = false;
        }
        return sb.ToString().Trim();
    }

    private static bool TryParseVersion(string? raw, out int[] parts, out string? preRelease)
    {
        parts = Array.Empty<int>();
        preRelease = null;
        var s = NormalizeVersion(raw);
        if (string.IsNullOrEmpty(s)) return false;

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = s[(dash + 1)..].Trim();
            s = s[..dash];
        }
        if (string.IsNullOrEmpty(s)) return false;

        var segments = s.Split('.');
        var nums = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i].Trim();
            if (seg.Length == 0 || !int.TryParse(seg, out var value) || value < 0) return false;
            nums[i] = value;
        }
        parts = nums;
        return true;
    }

    private static int ComparePreRelease(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var len = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < len; i++)
        {
            if (i >= aParts.Length) return -1;
            if (i >= bParts.Length) return 1;
            var aIsNum = int.TryParse(aParts[i], out var an);
            var bIsNum = int.TryParse(bParts[i], out var bn);
            if (aIsNum && bIsNum)
            {
                if (an != bn) return an.CompareTo(bn);
            }
            else
            {
                var cmp = string.Compare(aParts[i], bParts[i], StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }
}
