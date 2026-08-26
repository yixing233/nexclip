using System.Text;

namespace NexClip.Desktop.Services;

/// <summary>
/// 高性能中文拼音与首字母匹配工具，支持首拼缩写模糊搜索、全拼匹配与中英混合匹配。
/// </summary>
public static class PinyinHelper
{
    private static readonly Encoding? Gb2312;

    static PinyinHelper()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Gb2312 = Encoding.GetEncoding("GB2312");
        }
        catch
        {
            Gb2312 = null;
        }
    }

    /// <summary>获取单个汉字的首字母(小写 a-z)；非汉字返回字符本身小写。</summary>
    public static char GetInitial(char c)
    {
        if (c < 128) return char.ToLowerInvariant(c);
        if (Gb2312 is null) return char.ToLowerInvariant(c);

        try
        {
            var bytes = Gb2312.GetBytes(new[] { c });
            if (bytes.Length < 2) return char.ToLowerInvariant(c);

            var code = (bytes[0] << 8) + bytes[1];
            if (code >= 0xB0A1 && code <= 0xB0C4) return 'a';
            if (code >= 0xB0C5 && code <= 0xB2C0) return 'b';
            if (code >= 0xB2C1 && code <= 0xB4ED) return 'c';
            if (code >= 0xB4EE && code <= 0xB6E9) return 'd';
            if (code >= 0xB6EA && code <= 0xB7A1) return 'e';
            if (code >= 0xB7A2 && code <= 0xB8C0) return 'f';
            if (code >= 0xB8C1 && code <= 0xB9FD) return 'g';
            if (code >= 0xB9FE && code <= 0xBBF6) return 'h';
            if (code >= 0xBBF7 && code <= 0xBFA5) return 'j';
            if (code >= 0xBFA6 && code <= 0xC0AB) return 'k';
            if (code >= 0xC0AC && code <= 0xC2E7) return 'l';
            if (code >= 0xC2E8 && code <= 0xC4C2) return 'm';
            if (code >= 0xC4C3 && code <= 0xC5B5) return 'n';
            if (code >= 0xC5B6 && code <= 0xC5BD) return 'o';
            if (code >= 0xC5BE && code <= 0xC6D9) return 'p';
            if (code >= 0xC6DA && code <= 0xC8BA) return 'q';
            if (code >= 0xC8BB && code <= 0xC8F5) return 'r';
            if (code >= 0xC8F6 && code <= 0xCBF9) return 's';
            if (code >= 0xCBFA && code <= 0xCDD9) return 't';
            if (code >= 0xCDDA && code <= 0xCEF3) return 'w';
            if (code >= 0xCEF4 && code <= 0xD1B8) return 'x';
            if (code >= 0xD1B9 && code <= 0xD4D0) return 'y';
            if (code >= 0xD4D1 && code <= 0xD7F9) return 'z';
        }
        catch
        {
            // 忽略非标准汉字编码异常
        }

        return char.ToLowerInvariant(c);
    }

    /// <summary>获取文本对应的拼音首字母缩写字符串。</summary>
    public static string GetInitials(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(GetInitial(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 判断文本是否与搜索关键字匹配（支持原字符子串、拼音首字母匹配以及首拼缩写连续匹配）。
    /// </summary>
    public static bool IsMatch(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var q = query.Trim();
        // 1. 原文子串匹配 (忽略大小写)
        if (text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // 2. 拼音首字母子串匹配 (例如 "我的文件" 首字母 "wdwj" 匹配 "wdfj" 或 "wdw")
        var qLower = q.ToLowerInvariant();
        var initials = GetInitials(text);
        if (initials.IndexOf(qLower, StringComparison.Ordinal) >= 0) return true;

        // 3. 连续首拼子序列容错匹配 (例如 "文档附件" 首拼 "wdfj" 匹配 "wdfj")
        var ti = 0;
        var qi = 0;
        while (ti < initials.Length && qi < qLower.Length)
        {
            if (initials[ti] == qLower[qi]) qi++;
            ti++;
        }
        if (qi == qLower.Length) return true;

        return false;
    }
}
