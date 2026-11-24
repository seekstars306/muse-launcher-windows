using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MuseLauncher;

/// <summary>
/// 账号自定义图片头像存储与统一渲染器（PNG/JPG/WebP/BMP 自动居中裁方存为 128×128 PNG），
/// 同时支持双中文/组合 Emoji 自适应单行文字头像，不裁切不溢出。
/// </summary>
public static class AvatarStore
{
    private const int AvatarSizePx = 128;
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string AvatarsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MuseLauncher", "avatars");

    public static string ResolvePath(string fileName) => Path.Combine(AvatarsRoot, Path.GetFileName(fileName));

    /// <summary>读取用户选择的图片，居中裁成正方形并缩放到 128×128 PNG 原子保存，返回文件名。</summary>
    public static string ImportImage(long accountId, string sourceFilePath)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("找不到所选图片文件", sourceFilePath);

        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(sourceFilePath, UriKind.Absolute);
        source.EndInit();
        source.Freeze();

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("无法解码该图片文件");

        var side = Math.Min(width, height);
        var x = (width - side) / 2;
        var y = (height - side) / 2;
        BitmapSource cropped = new CroppedBitmap(source, new Int32Rect(x, y, side, side));
        var scale = (double)AvatarSizePx / side;
        BitmapSource resized = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
        resized.Freeze();

        Directory.CreateDirectory(AvatarsRoot);
        var fileName = $"acct_{accountId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
        var targetPath = Path.Combine(AvatarsRoot, fileName);
        var tempPath = targetPath + ".tmp";

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(resized));
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(fs);
        }
        File.Move(tempPath, targetPath, overwrite: true);
        Cache[fileName] = resized;
        return fileName;
    }

    public static BitmapSource? LoadImage(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName)) return null;
        var trimmed = pathOrFileName.Trim();
        var fullPath = Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(AvatarsRoot, Path.GetFileName(trimmed));
        if (string.IsNullOrEmpty(fullPath)) return null;
        if (Cache.TryGetValue(fullPath, out var cached)) return cached;
        if (!File.Exists(fullPath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            Cache[fullPath] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.I("Avatar", "load failed: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>仅删除由 AvatarStore 托管在 avatars 目录内的文件，绝不删除用户外部路径文件。</summary>
    public static void DeleteImage(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName)) return;
        var trimmed = pathOrFileName.Trim();
        if (Path.IsPathRooted(trimmed)) return;
        var safeName = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(safeName)) return;
        var fullPath = Path.Combine(AvatarsRoot, safeName);
        Cache.TryRemove(fullPath, out _);
        Cache.TryRemove(safeName, out _);
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            Log.I("Avatar", "delete failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 创建圆角头像块：优先显示自定义图片，否则按字符数与宽字符（中文/Emoji）自适应字号单行居中显示。
    /// </summary>
    public static Border MakeAvatarTile(double size, string? text, string? avatarImage, ThemePalette palette)
    {
        var corner = size * 0.28;
        var bitmap = LoadImage(avatarImage);
        if (bitmap != null)
        {
            var imageBrush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(imageBrush, BitmapScalingMode.HighQuality);
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(corner),
                Background = imageBrush,
            };
        }

        var display = string.IsNullOrWhiteSpace(text) ? "m" : text.Trim();
        var (fontSize, isDefaultM, isWideOrMulti) = ComputeAdaptiveTypography(size, display);
        var textBlock = new TextBlock
        {
            Text = display,
            FontFamily = isDefaultM ? new FontFamily("Georgia") : new FontFamily("Segoe UI Emoji, Microsoft YaHei UI, Segoe UI"),
            FontWeight = FontWeights.Bold,
            FontStyle = isDefaultM ? FontStyles.Italic : FontStyles.Normal,
            FontSize = fontSize,
            Foreground = palette.Accent,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isDefaultM ? new Thickness(0, 0, 0, size * 0.1) : new Thickness(0),
        };
        if (isWideOrMulti)
        {
            // Viewbox 兜底：极端宽组合 Emoji 也绝不截断
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(corner),
                Background = palette.TileBg,
                Padding = new Thickness(size * 0.08),
                Child = new Viewbox
                {
                    StretchDirection = StretchDirection.DownOnly,
                    Stretch = Stretch.Uniform,
                    Child = textBlock,
                },
            };
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(corner),
            Background = palette.TileBg,
            Child = textBlock,
        };
    }

    private static (double FontSize, bool IsDefaultM, bool NeedsViewbox) ComputeAdaptiveTypography(double size, string text)
    {
        if (text == "m") return (size * 0.54, true, false);
        var count = new StringInfo(text).LengthInTextElements;
        var hasWide = text.Any(ch => ch > 0x2E7F || char.IsSurrogate(ch));
        if (count <= 1)
            return (size * (hasWide ? 0.46 : 0.50), false, hasWide);
        return (size * (hasWide ? 0.34 : 0.40), false, true);
    }
}
