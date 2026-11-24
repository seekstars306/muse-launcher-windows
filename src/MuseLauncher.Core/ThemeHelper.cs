using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuseLauncher.Core;

/// <summary>
/// 同步 Muse 网页端日夜模式状态（镜像安卓 ThemeCookieHelper）：
/// - muse.ai 在 Domain=.muse.ai 下写入名为 `theme` 的 JSON Cookie（如 {"mode":"dark","accent":"blue"}），
///   并通过 document.documentElement 的 dark / light class 驱动样式。
/// - 更新时保留用户已有的其余字段（accent / style / chatTheme 等），只更新 mode。
/// </summary>
public static class ThemeHelper
{
    public const string ThemeCookieName = "theme";
    public const string ThemeCookieDomain = ".muse.ai";

    /// <summary>根据现有 theme Cookie 值（可为空、原始 JSON 或 URL 编码 JSON）生成更新后的 URL 编码值。</summary>
    public static string BuildUpdatedThemeCookieValue(string? existingCookieValue, bool isDark)
    {
        var mode = isDark ? "dark" : "light";
        var obj = ParseThemeJson(existingCookieValue);
        obj["mode"] = mode;
        return Uri.EscapeDataString(obj.ToJsonString());
    }

    /// <summary>实时切换已打开 muse.ai 页面的日夜模式脚本（无需刷新，保留输入内容与语音通话）。</summary>
    public static string BuildDomThemeSyncScript(bool isDark)
    {
        var mode = isDark ? "dark" : "light";
        return "(function(mode){try{"
            + "var r=document.documentElement;"
            + "if(r){r.classList.remove('light','dark');r.classList.add(mode);r.style.colorScheme=mode;}"
            + "var host=(location.hostname||'').toLowerCase();"
            + "if(host==='muse.ai'||host.endsWith('.muse.ai')){"
            + "var obj={};"
            + "var parts=(document.cookie||'').split(';');"
            + "for(var i=0;i<parts.length;i++){"
            + "var p=parts[i].trim();"
            + "if(p.indexOf('theme=')===0){"
            + "try{var raw=decodeURIComponent(p.substring(6));"
            + "if(raw&&raw.charAt(0)==='{'){var parsed=JSON.parse(raw);"
            + "if(parsed&&typeof parsed==='object'&&!Array.isArray(parsed)){"
            + "var ks=Object.keys(parsed);for(var k=0;k<ks.length;k++)obj[ks[k]]=parsed[ks[k]];}}}catch(e){}"
            + "}}"
            + "obj.mode=mode;"
            + "document.cookie='theme=; Path=/; Max-Age=0; SameSite=Lax; Secure';"
            + "document.cookie='theme='+encodeURIComponent(JSON.stringify(obj))"
            + "+'; Domain=.muse.ai; Path=/; Max-Age=31536000; SameSite=Lax; Secure';"
            + "}"
            + "}catch(e){}})('" + mode + "');";
    }

    private static JsonObject ParseThemeJson(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return new JsonObject();
        var trimmed = rawValue.Trim();
        try
        {
            var decoded = Uri.UnescapeDataString(trimmed);
            if (decoded.StartsWith('{') && JsonNode.Parse(decoded) is JsonObject decodedObj)
                return decodedObj;
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException) { }

        try
        {
            if (trimmed.StartsWith('{') && JsonNode.Parse(trimmed) is JsonObject rawObj)
                return rawObj;
        }
        catch (JsonException) { }

        return new JsonObject();
    }
}
