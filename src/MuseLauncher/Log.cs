using System.IO;

namespace MuseLauncher;

/// <summary>
/// 轻量文件日志（对应安卓 logcat 的 MusePerf / MuseAuth / MuseWebview tag）。
/// 写入 %LOCALAPPDATA%\MuseLauncher\log.txt，启动时超过 5MB 截断。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? path;

    public static string FilePath
    {
        get
        {
            if (path == null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MuseLauncher");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "log.txt");
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024) fi.Delete();
                }
                catch
                {
                    // 截断失败不影响使用
                }
            }
            return path;
        }
    }

    public static void I(string tag, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate) File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        catch
        {
            // 日志尽力而为
        }
    }
}
