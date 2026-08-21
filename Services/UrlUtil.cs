namespace SyncClipboard.Desktop.Services;

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
