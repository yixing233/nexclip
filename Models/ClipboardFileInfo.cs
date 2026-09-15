using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexClip.Desktop.Models;

/// <summary>
/// 剪贴板文件条目中单个文件/文件夹的元数据(仅路径与大小,不包含任何文件内容)。
/// 文件通常体积很大,缓存内容会显著占用磁盘与内存,因此这里只保存引用信息。
/// </summary>
/// <param name="Path">文件或文件夹的本地绝对路径。</param>
/// <param name="Name">文件名(含扩展名)或文件夹名,用于列表展示。</param>
/// <param name="IsDirectory">是否为文件夹。</param>
/// <param name="SizeBytes">文件字节数;文件夹不递归统计,固定为 0。</param>
public sealed record ClipboardFileInfo(string Path, string Name, bool IsDirectory, long SizeBytes);

/// <summary>
/// 文件条目元数据的序列化与内容哈希。
/// 哈希只基于"路径 + 大小 + 目录标记",全程不读取文件内容:
/// 既保证同一批文件重复复制时可以命中同一条记录去重置顶,又避免对大文件做全量读取。
/// </summary>
public static class ClipboardFileMeta
{
    /// <summary>
    /// 单条记录最多保留的文件数。整目录选中时文件数可达数万,
    /// 不设上限会生成超大 JSON,既拖慢列表渲染也放大数据库体积。
    /// </summary>
    public const int MaxFiles = 100;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>序列化为紧凑 JSON;空列表返回空串(存储层会落为 NULL)。</summary>
    public static string Serialize(IReadOnlyList<ClipboardFileInfo> files) =>
        files.Count == 0 ? "" : JsonSerializer.Serialize(files, Options);

    /// <summary>反序列化;内容缺失或损坏时返回空列表,不抛异常。</summary>
    public static IReadOnlyList<ClipboardFileInfo> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ClipboardFileInfo>();
        try
        {
            return JsonSerializer.Deserialize<List<ClipboardFileInfo>>(json, Options)
                   ?? (IReadOnlyList<ClipboardFileInfo>)Array.Empty<ClipboardFileInfo>();
        }
        catch
        {
            // 结构损坏(手工改库/版本差异)一律降级为空列表,不影响条目其它字段的展示
            return Array.Empty<ClipboardFileInfo>();
        }
    }

    /// <summary>
    /// 内容哈希(仅元数据)。使用 SHA-256 与文本/图片哈希保持同一算法族,
    /// 但输入为元数据串而非文件字节,因此大文件也不会产生任何磁盘读取开销。
    /// </summary>
    public static string ComputeHash(IReadOnlyList<ClipboardFileInfo> files)
    {
        if (files.Count == 0) return "";
        var sb = new StringBuilder(files.Count * 64);
        foreach (var f in files)
        {
            // \u0001 分隔字段、\u0002 分隔记录:路径中不可能出现这两个控制字符,不会产生歧义拼接
            sb.Append(f.Path).Append('\u0001')
              .Append(f.SizeBytes).Append('\u0001')
              .Append(f.IsDirectory ? '1' : '0').Append('\u0002');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>字节数转可读文本(1.5 MB 形式);用于列表元信息展示。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes / 1024.0;
        if (v < 1024) return $"{v:0.#} KB";
        v /= 1024.0;
        if (v < 1024) return $"{v:0.#} MB";
        v /= 1024.0;
        return $"{v:0.##} GB";
    }
}
