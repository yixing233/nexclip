using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NexClip.Desktop.Services;

/// <summary>链接(URL)识别工具:判断文本是否为可打开的网址。</summary>
public static class UrlUtil
{
    /// <summary>是否为 http/https 链接(整个文本就是一个网址)。</summary>
    public static bool IsUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return Uri.TryCreate(t, UriKind.Absolute, out _);
    }
}

/// <summary>富文本与格式识别助手:提供颜色解析、域名提取与代码/JSON智能识别。</summary>
public static class FormatHelper
{
    private static readonly Regex HexColorRegex = new(
        @"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
        RegexOptions.Compiled);

    private static readonly Regex RgbColorRegex = new(
        @"^rgba?\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([0-1]?(?:\.\d+)?)\s*)?\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HslColorRegex = new(
        @"^hsla?\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})%\s*,\s*(\d{1,3})%\s*(?:,\s*([0-1]?(?:\.\d+)?)\s*)?\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>尝试将文本解析为颜色(Hex、RGB、RGBA、HSL)。</summary>
    public static bool TryParseColor(string? text, out Color color, out SolidColorBrush? brush)
    {
        color = default;
        brush = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();

        // 1. Hex
        if (HexColorRegex.IsMatch(t))
        {
            if (TryParseHex(t, out color))
            {
                brush = new SolidColorBrush(color);
                return true;
            }
        }

        // 2. RGB / RGBA
        var rgbMatch = RgbColorRegex.Match(t);
        if (rgbMatch.Success)
        {
            if (int.TryParse(rgbMatch.Groups[1].Value, out var r) &&
                int.TryParse(rgbMatch.Groups[2].Value, out var g) &&
                int.TryParse(rgbMatch.Groups[3].Value, out var b))
            {
                byte a = 255;
                if (rgbMatch.Groups[4].Success && float.TryParse(rgbMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                {
                    a = (byte)Math.Clamp((int)(alpha * 255), 0, 255);
                }
                color = ColorHelper.FromArgb(a, (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
                brush = new SolidColorBrush(color);
                return true;
            }
        }

        // 3. HSL
        var hslMatch = HslColorRegex.Match(t);
        if (hslMatch.Success)
        {
            if (float.TryParse(hslMatch.Groups[1].Value, out var h) &&
                float.TryParse(hslMatch.Groups[2].Value, out var s) &&
                float.TryParse(hslMatch.Groups[3].Value, out var l))
            {
                byte a = 255;
                if (hslMatch.Groups[4].Success && float.TryParse(hslMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                {
                    a = (byte)Math.Clamp((int)(alpha * 255), 0, 255);
                }
                color = HslToRgb(h, s / 100f, l / 100f, a);
                brush = new SolidColorBrush(color);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        var clean = hex.TrimStart('#');
        try
        {
            if (clean.Length == 3) // #RGB
            {
                var r = byte.Parse(new string(clean[0], 2), NumberStyles.HexNumber);
                var g = byte.Parse(new string(clean[1], 2), NumberStyles.HexNumber);
                var b = byte.Parse(new string(clean[2], 2), NumberStyles.HexNumber);
                color = ColorHelper.FromArgb(255, r, g, b);
                return true;
            }
            if (clean.Length == 4) // #RGBA
            {
                var r = byte.Parse(new string(clean[0], 2), NumberStyles.HexNumber);
                var g = byte.Parse(new string(clean[1], 2), NumberStyles.HexNumber);
                var b = byte.Parse(new string(clean[2], 2), NumberStyles.HexNumber);
                var a = byte.Parse(new string(clean[3], 2), NumberStyles.HexNumber);
                color = ColorHelper.FromArgb(a, r, g, b);
                return true;
            }
            if (clean.Length == 6) // #RRGGBB
            {
                var r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber);
                var g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber);
                var b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber);
                color = ColorHelper.FromArgb(255, r, g, b);
                return true;
            }
            if (clean.Length == 8) // #RRGGBBAA
            {
                var r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber);
                var g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber);
                var b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber);
                var a = byte.Parse(clean.Substring(6, 2), NumberStyles.HexNumber);
                color = ColorHelper.FromArgb(a, r, g, b);
                return true;
            }
        }
        catch
        {
            // 忽略解析异常
        }
        return false;
    }

    private static Color HslToRgb(float h, float s, float l, byte a)
    {
        float r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h / 360f + 1f / 3f);
            g = HueToRgb(p, q, h / 360f);
            b = HueToRgb(p, q, h / 360f - 1f / 3f);
        }
        return ColorHelper.FromArgb(a, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    /// <summary>提取 URL 中的主机名/域名(如 github.com)。</summary>
    public static string? ExtractDomain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (!UrlUtil.IsUrl(t)) return null;
        try
        {
            var uri = new Uri(t);
            var host = uri.Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                host = host[4..];
            }
            return host;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>智能判断文本是否属于代码片段或格式化 JSON。</summary>
    public static bool IsCodeOrJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 10) return false;
        var t = text.Trim();

        // 1. JSON 结构
        if ((t.StartsWith('{') && t.EndsWith('}')) || (t.StartsWith('[') && t.EndsWith(']')))
        {
            if (t.Contains(':') || t.Contains(',')) return true;
        }

        // 2. XML / HTML
        if (t.StartsWith('<') && t.EndsWith('>') && (t.Contains("</") || t.Contains("/>") || t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 3. 多行代码关键字特征
        var lines = t.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 2)
        {
            var codeKeywords = new[]
            {
                "const ", "let ", "var ", "function ", "import ", "export ", "class ", "public ", "private ",
                "protected ", "void ", "return ", "async ", "await ", "def ", "if (", "for (", "while (",
                "namespace ", "using ", "package ", "struct ", "impl ", "fn "
            };

            var matchCount = 0;
            foreach (var line in lines)
            {
                var l = line.Trim();
                if (codeKeywords.Any(k => l.StartsWith(k, StringComparison.OrdinalIgnoreCase)) ||
                    l.EndsWith(';') || l.EndsWith('{') || l.EndsWith('}'))
                {
                    matchCount++;
                }
            }

            if (matchCount >= 2 || (lines.Length >= 3 && (float)matchCount / lines.Length >= 0.4f))
            {
                return true;
            }
        }

        return false;
    }
}

