using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>
/// 为指定账号创建独立的 Windows 桌面快捷方式（.lnk + 自动生成的账号头像 .ico）。
/// 双击直达该账号并全屏，仅使用 Release 允许的安全导航参数 --start_account 与 --fullscreen。
/// </summary>
public static class DesktopShortcutHelper
{
    public static string IconsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MuseLauncher", "icons");

    public static string CreateAccountShortcut(Account account, ThemePalette palette)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("无法定位当前启动器可执行文件");

        var iconPath = BuildAccountIcon(account, palette);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            throw new DirectoryNotFoundException("无法访问当前用户的桌面目录");

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(account.Name) ? $"Muse {account.Id}" : account.Name);
        var shortcutPath = Path.Combine(desktop, safeName + ".lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统缺少 WScript.Shell 组件，无法创建快捷方式");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法初始化 WScript.Shell");
        try
        {
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })
                ?? throw new InvalidOperationException("创建快捷方式对象失败");
            var scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                new object[] { $"--start_account={account.Id} --fullscreen=true" });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                new object[] { Path.GetDirectoryName(exePath) ?? "" });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                new object[] { $"Muse 登录器 · {account.Name}" });
            if (File.Exists(iconPath))
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return shortcutPath;
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static string BuildAccountIcon(Account account, ThemePalette palette)
    {
        Directory.CreateDirectory(IconsRoot);
        var iconPath = Path.Combine(IconsRoot, $"acct_{account.Id}.ico");
        const int size = 64;

        var tile = AvatarStore.MakeAvatarTile(size, account.Avatar, account.AvatarImage, palette);
        tile.Measure(new Size(size, size));
        tile.Arrange(new Rect(0, 0, size, size));
        tile.UpdateLayout();

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(tile);

        using var pngStream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        encoder.Save(pngStream);
        var pngBytes = pngStream.ToArray();

        // 构造标准单帧 PNG-in-ICO（Windows Vista+ 原生支持）
        var icoBytes = new byte[22 + pngBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(icoBytes.AsSpan(0, 2), 0); // Reserved
        BinaryPrimitives.WriteUInt16LittleEndian(icoBytes.AsSpan(2, 2), 1); // Type = ICO
        BinaryPrimitives.WriteUInt16LittleEndian(icoBytes.AsSpan(4, 2), 1); // Count = 1
        icoBytes[6] = size;                                                  // Width
        icoBytes[7] = size;                                                  // Height
        icoBytes[8] = 0;                                                     // Colors
        icoBytes[9] = 0;                                                     // Reserved
        BinaryPrimitives.WriteUInt16LittleEndian(icoBytes.AsSpan(10, 2), 1); // Planes
        BinaryPrimitives.WriteUInt16LittleEndian(icoBytes.AsSpan(12, 2), 32);// BitCount
        BinaryPrimitives.WriteUInt32LittleEndian(icoBytes.AsSpan(14, 4), (uint)pngBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(icoBytes.AsSpan(18, 4), 22);// Offset
        Buffer.BlockCopy(pngBytes, 0, icoBytes, 22, pngBytes.Length);

        var tempPath = iconPath + ".tmp";
        File.WriteAllBytes(tempPath, icoBytes);
        File.Move(tempPath, iconPath, overwrite: true);
        return iconPath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(result) ? "Muse" : result;
    }
}
