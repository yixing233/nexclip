using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Windows.UI;

namespace NexClip.Desktop.Services;

/// <summary>
/// 智能动作解析引擎：支持色值微预览与转换、本地文件/路径直达、GitHub 与网盘提取码 DeepLink、通用网址等。
/// </summary>
public static class SmartActionService
{
    private static readonly Regex HexColorRegex = new(@"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private static readonly Regex RgbColorRegex = new(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*([\d.]+))?\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HslColorRegex = new(@"^hsla?\(\s*(\d{1,3})\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%(?:\s*,\s*([\d.]+))?\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GitHubRepoRegex = new(@"https?://(?:www\.)?github\.com/([a-zA-Z0-9_\-\.]+)/([a-zA-Z0-9_\-\.]+)(?:/.*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtractionCodeRegex = new(@"(?:提取码|密码|访问码|code|pwd)[:：\s]*([a-zA-Z0-9]{4,8})|[?&]pwd=([a-zA-Z0-9]{4,8})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UrlRegex = new(@"(https?://[a-zA-Z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 检测剪贴板文本并构造对应的智能动作。
    /// 当 forToast = true 时，遵循设置页面中各项智能直达浮窗的启停开关；
    /// 当 forToast = false (默认) 时，始终识别完整元数据(供列表历史卡片色块预览与右键菜单展示)。
    /// </summary>
    public static SmartAction? Detect(string rawText, bool forToast = false)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var text = rawText.Trim();
        if (text.Length > 2000) return null; // 过长文本忽略

        var s = App.Services?.Settings;
        var colorEnabled = !forToast || (s?.SmartColorEnabled ?? true);
        var pathEnabled = !forToast || (s?.SmartPathEnabled ?? true);
        var deepLinkEnabled = !forToast || (s?.SmartDeepLinkEnabled ?? true);
        var netDiskEnabled = !forToast || (s?.SmartNetDiskEnabled ?? true);
        var urlEnabled = !forToast || (s?.SmartUrlEnabled ?? true);

        // 1. 颜色色值微预览与格式转换 (历史卡片始终展示色块与互转按钮，浮窗开关控制是否弹窗)
        if (DetectColor(text) is { } colorAction)
        {
            return colorEnabled ? colorAction : null;
        }

        // 2. 本地文件 / 路径直达
        if (DetectLocalPath(text) is { } pathAction)
        {
            return pathEnabled ? pathAction : null;
        }

        // 3. 平台深度链接 (Deep Link: GitHub / 网盘)
        if (DetectDeepLink(text, true) is { } deepLinkAction)
        {
            if (deepLinkAction.Kind == SmartActionKind.GitHub && !deepLinkEnabled) return null;
            if (deepLinkAction.Kind == SmartActionKind.NetDisk && !netDiskEnabled) return null;
            return deepLinkAction;
        }

        // 4. 通用网址
        if (DetectGeneralUrl(text) is { } urlAction)
        {
            return urlEnabled ? urlAction : null;
        }

        return null;
    }

    #region 1. 颜色色值识别与转换

    private static SmartAction? DetectColor(string text)
    {
        // 1.1 HEX (#RGB, #RGBA, #RRGGBB, #RRGGBBAA)
        if (HexColorRegex.IsMatch(text))
        {
            if (TryParseHexColor(text, out var color))
            {
                var hexStr = ToHex(color);
                var rgbStr = ToRgb(color);
                var hslStr = ToHsl(color);

                return new SmartAction
                {
                    Kind = SmartActionKind.Color,
                    Title = $"色值预览 · {hexStr}",
                    Subtitle = $"{rgbStr}  ·  {hslStr}",
                    Icon = Lucide.Palette,
                    PreviewColor = color,
                    HexColorString = hexStr,
                    RgbColorString = rgbStr,
                    HslColorString = hslStr,
                    PrimaryButtonText = $"复制 RGB",
                    PrimaryButtonIcon = Lucide.CopyAccent,
                    PrimaryAction = () => CopyToClipboard(rgbStr),
                    SecondaryButtonText = "复制 HSL",
                    SecondaryButtonIcon = Lucide.Copy,
                    SecondaryAction = () => CopyToClipboard(hslStr)
                };
            }
        }

        // 1.2 RGB / RGBA
        var rgbMatch = RgbColorRegex.Match(text);
        if (rgbMatch.Success)
        {
            var r = Math.Clamp(byte.Parse(rgbMatch.Groups[1].Value), (byte)0, (byte)255);
            var g = Math.Clamp(byte.Parse(rgbMatch.Groups[2].Value), (byte)0, (byte)255);
            var b = Math.Clamp(byte.Parse(rgbMatch.Groups[3].Value), (byte)0, (byte)255);
            byte a = 255;
            if (rgbMatch.Groups[4].Success && double.TryParse(rgbMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alphaVal))
            {
                a = (byte)Math.Round(Math.Clamp(alphaVal, 0.0, 1.0) * 255);
            }
            var color = Color.FromArgb(a, r, g, b);
            var hexStr = ToHex(color);
            var rgbStr = ToRgb(color);
            var hslStr = ToHsl(color);

            return new SmartAction
            {
                Kind = SmartActionKind.Color,
                Title = $"色值预览 · {hexStr}",
                Subtitle = $"{rgbStr}  ·  {hslStr}",
                Icon = Lucide.Palette,
                PreviewColor = color,
                HexColorString = hexStr,
                RgbColorString = rgbStr,
                HslColorString = hslStr,
                PrimaryButtonText = $"复制 HEX",
                PrimaryButtonIcon = Lucide.CopyAccent,
                PrimaryAction = () => CopyToClipboard(hexStr),
                SecondaryButtonText = "复制 HSL",
                SecondaryButtonIcon = Lucide.Copy,
                SecondaryAction = () => CopyToClipboard(hslStr)
            };
        }

        // 1.3 HSL / HSLA
        var hslMatch = HslColorRegex.Match(text);
        if (hslMatch.Success)
        {
            var h = double.Parse(hslMatch.Groups[1].Value, CultureInfo.InvariantCulture) % 360;
            var s = Math.Clamp(double.Parse(hslMatch.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0, 0.0, 1.0);
            var l = Math.Clamp(double.Parse(hslMatch.Groups[3].Value, CultureInfo.InvariantCulture) / 100.0, 0.0, 1.0);
            byte a = 255;
            if (hslMatch.Groups[4].Success && double.TryParse(hslMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alphaVal))
            {
                a = (byte)Math.Round(Math.Clamp(alphaVal, 0.0, 1.0) * 255);
            }
            var color = HslToRgb(h, s, l, a);
            var hexStr = ToHex(color);
            var rgbStr = ToRgb(color);
            var hslStr = ToHsl(color);

            return new SmartAction
            {
                Kind = SmartActionKind.Color,
                Title = $"色值预览 · {hexStr}",
                Subtitle = $"{rgbStr}  ·  {hslStr}",
                Icon = Lucide.Palette,
                PreviewColor = color,
                HexColorString = hexStr,
                RgbColorString = rgbStr,
                HslColorString = hslStr,
                PrimaryButtonText = $"复制 HEX",
                PrimaryButtonIcon = Lucide.CopyAccent,
                PrimaryAction = () => CopyToClipboard(hexStr),
                SecondaryButtonText = "复制 RGB",
                SecondaryButtonIcon = Lucide.Copy,
                SecondaryAction = () => CopyToClipboard(rgbStr)
            };
        }

        return null;
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = default;
        var clean = hex.TrimStart('#');
        try
        {
            if (clean.Length == 3) // #RGB
            {
                var r = Convert.ToByte(new string(clean[0], 2), 16);
                var g = Convert.ToByte(new string(clean[1], 2), 16);
                var b = Convert.ToByte(new string(clean[2], 2), 16);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (clean.Length == 4) // #RGBA
            {
                var r = Convert.ToByte(new string(clean[0], 2), 16);
                var g = Convert.ToByte(new string(clean[1], 2), 16);
                var b = Convert.ToByte(new string(clean[2], 2), 16);
                var a = Convert.ToByte(new string(clean[3], 2), 16);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
            if (clean.Length == 6) // #RRGGBB
            {
                var r = Convert.ToByte(clean.Substring(0, 2), 16);
                var g = Convert.ToByte(clean.Substring(2, 2), 16);
                var b = Convert.ToByte(clean.Substring(4, 2), 16);
                color = Color.FromArgb(255, r, g, b);
                return true;
            }
            if (clean.Length == 8) // #RRGGBBAA
            {
                var r = Convert.ToByte(clean.Substring(0, 2), 16);
                var g = Convert.ToByte(clean.Substring(2, 2), 16);
                var b = Convert.ToByte(clean.Substring(4, 2), 16);
                var a = Convert.ToByte(clean.Substring(6, 2), 16);
                color = Color.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string ToHex(Color c) => c.A == 255
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    private static string ToRgb(Color c) => c.A == 255
        ? $"rgb({c.R}, {c.G}, {c.B})"
        : $"rgba({c.R}, {c.G}, {c.B}, {Math.Round(c.A / 255.0, 2).ToString(CultureInfo.InvariantCulture)})";

    private static string ToHsl(Color c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        double h, s;

        if (Math.Abs(max - min) < 0.00001)
        {
            h = 0;
            s = 0;
        }
        else
        {
            var d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (Math.Abs(max - r) < 0.00001)
                h = (g - b) / d + (g < b ? 6.0 : 0.0);
            else if (Math.Abs(max - g) < 0.00001)
                h = (b - r) / d + 2.0;
            else
                h = (r - g) / d + 4.0;
            h /= 6.0;
        }

        var hDeg = (int)Math.Round(h * 360.0);
        var sPct = (int)Math.Round(s * 100.0);
        var lPct = (int)Math.Round(l * 100.0);

        return c.A == 255
            ? $"hsl({hDeg}, {sPct}%, {lPct}%)"
            : $"hsla({hDeg}, {sPct}%, {lPct}%, {Math.Round(c.A / 255.0, 2).ToString(CultureInfo.InvariantCulture)})";
    }

    private static Color HslToRgb(double h, double s, double l, byte a)
    {
        double r, g, b;
        if (Math.Abs(s) < 0.00001)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            var p = 2.0 * l - q;
            var hk = h / 360.0;
            r = HueToRgb(p, q, hk + 1.0 / 3.0);
            g = HueToRgb(p, q, hk);
            b = HueToRgb(p, q, hk - 1.0 / 3.0);
        }

        return Color.FromArgb(a,
            (byte)Math.Round(Math.Clamp(r * 255.0, 0, 255)),
            (byte)Math.Round(Math.Clamp(g * 255.0, 0, 255)),
            (byte)Math.Round(Math.Clamp(b * 255.0, 0, 255)));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1.0;
        if (t > 1) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    #endregion

    #region 2. 本地文件与路径识别

    private static SmartAction? DetectLocalPath(string text)
    {
        var raw = text.Trim().Trim('"', '\'');
        if (raw.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            raw = Uri.UnescapeDataString(raw.Substring(8).Replace('/', '\\'));
        }

        // 必须为绝对驱动器盘符路径或网络共享 UNC 路径
        if (!Regex.IsMatch(raw, @"^[a-zA-Z]:[\\/].*") && !raw.StartsWith(@"\\"))
        {
            return null;
        }

        var normalized = raw.Replace('/', '\\');

        // 2.1 判定为目录
        if (Directory.Exists(normalized))
        {
            var dirName = Path.GetFileName(normalized.TrimEnd('\\'));
            if (string.IsNullOrEmpty(dirName)) dirName = normalized;

            return new SmartAction
            {
                Kind = SmartActionKind.LocalFolder,
                Title = $"打开文件夹 · {dirName}",
                Subtitle = normalized,
                Icon = Lucide.FolderOpen,
                TargetPath = normalized,
                PrimaryButtonText = "打开文件夹",
                PrimaryButtonIcon = Lucide.FolderOpenAccent,
                PrimaryAction = () => OpenInExplorer(normalized),
                SecondaryButtonText = "在终端中打开",
                SecondaryButtonIcon = Lucide.ExternalLink,
                SecondaryAction = () => OpenInTerminal(normalized)
            };
        }

        // 2.2 判定为文件
        if (File.Exists(normalized))
        {
            var fileName = Path.GetFileName(normalized);
            var fileInfo = new FileInfo(normalized);
            var sizeStr = FormatFileSize(fileInfo.Length);

            return new SmartAction
            {
                Kind = SmartActionKind.LocalFile,
                Title = $"定位文件 · {fileName}",
                Subtitle = $"{normalized} ({sizeStr})",
                Icon = Lucide.FileText,
                TargetPath = normalized,
                PrimaryButtonText = "定位文件",
                PrimaryButtonIcon = Lucide.FolderOpenAccent,
                PrimaryAction = () => LocateInExplorer(normalized),
                SecondaryButtonText = "直接打开",
                SecondaryButtonIcon = Lucide.ExternalLink,
                SecondaryAction = () => OpenFile(normalized)
            };
        }

        return null;
    }

    private static void OpenInExplorer(string path) => NativeMethods.OpenFolderInExplorer(path);

    private static void LocateInExplorer(string filePath) => NativeMethods.LocateInExplorer(filePath);

    private static void OpenInTerminal(string folderPath)
    {
        try
        {
            // 优先启动 Windows Terminal (wt.exe)，回退启动 PowerShell
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folderPath}\"") { UseShellExecute = true });
                return;
            }
            catch { }

            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command \"Set-Location -LiteralPath '{folderPath}'\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"在终端中打开目录失败: {folderPath}", ex);
        }
    }

    private static void OpenFile(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开文件失败: {filePath}", ex);
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }

    #endregion

    #region 3. 平台深度识别 (DeepLink)

    private static SmartAction? DetectDeepLink(string text, bool netDiskEnabled)
    {
        // 3.1 GitHub 仓库
        var ghMatch = GitHubRepoRegex.Match(text);
        if (ghMatch.Success)
        {
            var owner = ghMatch.Groups[1].Value;
            var repo = ghMatch.Groups[2].Value.TrimEnd('/');
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                repo = repo.Substring(0, repo.Length - 4);
            }
            var repoUrl = $"https://github.com/{owner}/{repo}";
            var cloneCmd = $"git clone {repoUrl}.git";

            return new SmartAction
            {
                Kind = SmartActionKind.GitHub,
                Title = $"GitHub 仓库 · {owner}/{repo}",
                Subtitle = repoUrl,
                Icon = Lucide.ExternalLink,
                PrimaryButtonText = "打开仓库",
                PrimaryButtonIcon = Lucide.ExternalLinkAccent,
                PrimaryAction = () => OpenBrowser(repoUrl),
                SecondaryButtonText = "复制 Git Clone",
                SecondaryButtonIcon = Lucide.Copy,
                SecondaryAction = () => CopyToClipboard(cloneCmd)
            };
        }

        // 3.2 网盘识别与提取码提取
        if (netDiskEnabled)
        {
            var netDiskAction = DetectNetDisk(text);
            if (netDiskAction != null) return netDiskAction;
        }

        return null;
    }

    private static SmartAction? DetectNetDisk(string text)
    {
        string? netDiskName = null;
        if (text.Contains("pan.baidu.com", StringComparison.OrdinalIgnoreCase) || text.Contains("百度网盘"))
            netDiskName = "百度网盘";
        else if (text.Contains("aliyundrive.com", StringComparison.OrdinalIgnoreCase) || text.Contains("alipan.com", StringComparison.OrdinalIgnoreCase) || text.Contains("阿里云盘"))
            netDiskName = "阿里云盘";
        else if (text.Contains("pan.quark.cn", StringComparison.OrdinalIgnoreCase) || text.Contains("夸克网盘"))
            netDiskName = "夸克网盘";
        else if (text.Contains("123pan.com", StringComparison.OrdinalIgnoreCase) || text.Contains("123684.com", StringComparison.OrdinalIgnoreCase) || text.Contains("123云盘"))
            netDiskName = "123云盘";
        else if (text.Contains("lanzou", StringComparison.OrdinalIgnoreCase) || text.Contains("蓝奏云"))
            netDiskName = "蓝奏云";
        else if (text.Contains("115.com", StringComparison.OrdinalIgnoreCase) || text.Contains("115网盘"))
            netDiskName = "115网盘";
        else if (text.Contains("cloud.189.cn", StringComparison.OrdinalIgnoreCase) || text.Contains("天翼云盘"))
            netDiskName = "天翼云盘";
        else if (text.Contains("pan.xunlei.com", StringComparison.OrdinalIgnoreCase) || text.Contains("迅雷云盘"))
            netDiskName = "迅雷云盘";

        if (netDiskName == null) return null;

        // 提取网盘链接
        var urlMatch = UrlRegex.Match(text);
        var netDiskUrl = urlMatch.Success ? urlMatch.Groups[1].Value : "";

        // 提取提取码
        string? extractionCode = null;
        var codeMatch = ExtractionCodeRegex.Match(text);
        if (codeMatch.Success)
        {
            extractionCode = codeMatch.Groups[1].Success ? codeMatch.Groups[1].Value : codeMatch.Groups[2].Value;
        }

        if (string.IsNullOrEmpty(netDiskUrl) && string.IsNullOrEmpty(extractionCode)) return null;

        var title = extractionCode != null ? $"{netDiskName} · 提取码: {extractionCode}" : $"{netDiskName} 链接";
        var subtitle = !string.IsNullOrEmpty(netDiskUrl) ? netDiskUrl : text;

        return new SmartAction
        {
            Kind = SmartActionKind.NetDisk,
            Title = title,
            Subtitle = subtitle,
            Icon = Lucide.Download,
            ExtractionCode = extractionCode,
            PrimaryButtonText = "打开网盘",
            PrimaryButtonIcon = Lucide.ExternalLinkAccent,
            PrimaryAction = () =>
            {
                if (!string.IsNullOrEmpty(netDiskUrl)) OpenBrowser(netDiskUrl);
                // 打开时顺便自动复制提取码到剪贴板，极大方便用户粘贴
                if (!string.IsNullOrEmpty(extractionCode)) CopyToClipboard(extractionCode);
            },
            SecondaryButtonText = extractionCode != null ? $"复制提取码 ({extractionCode})" : null,
            SecondaryButtonIcon = extractionCode != null ? Lucide.Copy : null,
            SecondaryAction = extractionCode != null ? () => CopyToClipboard(extractionCode) : null
        };
    }

    #endregion

    #region 4. 通用网址识别

    private static SmartAction? DetectGeneralUrl(string text)
    {
        if (!UrlUtil.IsUrl(text) && !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var url = text.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host;
        var title = !string.IsNullOrEmpty(host) ? $"访问 {host}" : "打开网页链接";

        return new SmartAction
        {
            Kind = SmartActionKind.Url,
            Title = title,
            Subtitle = url,
            Icon = Lucide.ExternalLink,
            PrimaryButtonText = "打开链接",
            PrimaryButtonIcon = Lucide.ExternalLinkAccent,
            PrimaryAction = () => OpenBrowser(url)
        };
    }

    #endregion

    private static void OpenBrowser(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"打开浏览器链接失败: {url}", ex);
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Log.Warn($"复制到剪贴板失败: {ex.Message}");
        }
    }
}
