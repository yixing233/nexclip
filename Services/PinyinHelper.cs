using System.Text;

namespace NexClip.Desktop.Services;

/// <summary>
/// 中文拼音检索工具。基于嵌入的「音节 → 汉字」表（源自 Unicode Unihan 的 kMandarin / kXHC1983），
/// 同时提供全拼与首字母两种检索形态。
///
/// 相对旧实现的三处关键变化：
/// 1. 不再用 GB2312 编码转换逐字推首字母。旧做法每次调用都对整段文本做编码转换（每次分配字节数组），
///    且只能覆盖 GB2312 的 6763 字；现改为一次性加载映射表 + O(1) 数组查表，覆盖基本区 2 万余字。
/// 2. 新增全拼支持。旧实现注释写着"全拼匹配"，实际只取了首字母，用户输入 wode/wenjian 无法命中。
/// 3. 拼音串跳过标点与空白，保证音节连续，使跨标点的查询（如文本 "我的文件 v2" 搜 wenjian）也能命中。
///
/// 多音字处理：每个汉字同时给出「主读音」与「一个最常用备选读音」两组结果。
/// 例如「重庆」主读音串为 zhongqing、备选串为 chongqing，两种输入都能命中；
/// 「银行」主读音串 yinxing、备选串 yinhang。备选只取一个，不做读音组合爆炸。
/// </summary>
public static class PinyinHelper
{
    /// <summary>嵌入资源名（由 NexClip.Desktop.csproj 的 LogicalName 指定）。</summary>
    private const string ResourceName = "NexClip.Desktop.pinyin.txt";

    /// <summary>CJK 统一汉字基本区起始码点。</summary>
    private const int Base = 0x4E00;

    /// <summary>基本区码点跨度（U+4E00..U+9FFF）。</summary>
    private const int Span = 0x9FFF - 0x4E00 + 1;

    /// <summary>音节表；下标 0 保留表示"无数据"。</summary>
    private static string[] _syllables = { "" };

    /// <summary>码点 → 主读音音节下标（0 表示无数据）。</summary>
    private static readonly ushort[] Primary = new ushort[Span];

    /// <summary>码点 → 备选读音音节下标（0 表示无备选）。</summary>
    private static readonly ushort[] Alt = new ushort[Span];

    private static readonly Lazy<bool> Loaded = new(LoadTable, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>映射表是否可用。表缺失时拼音检索整体降级为"无命中"，字面检索不受影响。</summary>
    public static bool IsAvailable => Loaded.Value;

    private static bool LoadTable()
    {
        try
        {
            using var stream = typeof(PinyinHelper).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Log.Warn($"拼音表资源缺失({ResourceName})，拼音检索不可用");
                return false;
            }

            var syllables = new List<string> { "" };
            var index = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
            // 0=未进入任何段；1=主读音段；2=备选读音段
            var section = 0;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    section = line.Contains("alt", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                    continue;
                }

                var sep = line.IndexOf(':');
                if (sep <= 0) continue;

                var pinyin = line[..sep];
                if (!index.TryGetValue(pinyin, out var slot))
                {
                    slot = syllables.Count;
                    if (slot > ushort.MaxValue)
                    {
                        Log.Warn("拼音表音节数超出索引上限，后续音节将被忽略");
                        break;
                    }
                    syllables.Add(pinyin);
                    index[pinyin] = slot;
                }

                var target = section == 2 ? Alt : Primary;
                for (var i = sep + 1; i < line.Length; i++)
                {
                    var offset = line[i] - Base;
                    if ((uint)offset >= (uint)Span) continue;
                    // 每个字在每个段内只应出现一次；保留首次出现的音节，后续重复忽略
                    if (target[offset] == 0) target[offset] = (ushort)slot;
                }
            }

            _syllables = syllables.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"加载拼音表失败，拼音检索不可用: {ex.Message}");
            return false;
        }
    }

    /// <summary>全拼串（主读音）。标点与空白被跳过，保证音节连续。</summary>
    public static string GetFullPinyin(string? text) => Build(text, useAlt: false, asInitial: false);

    /// <summary>全拼串的备选读音变体；文本不含多音字时与 <see cref="GetFullPinyin"/> 结果相同。</summary>
    public static string GetFullPinyinVariant(string? text) => Build(text, useAlt: true, asInitial: false);

    /// <summary>首字母串（主读音）。</summary>
    public static string GetInitials(string? text) => Build(text, useAlt: false, asInitial: true);

    /// <summary>首字母串的备选读音变体；文本不含多音字时与 <see cref="GetInitials"/> 结果相同。</summary>
    public static string GetInitialsVariant(string? text) => Build(text, useAlt: true, asInitial: true);

    private static string Build(string? text, bool useAlt, bool asInitial)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // 全拼按音节平均 3 个字符预留，首字母按 1 个预留，减少扩容次数
        var sb = new StringBuilder(asInitial ? text.Length : text.Length * 3);
        foreach (var c in text)
        {
            if (c < 128)
            {
                // 半角字母数字原样保留（小写），使 "v2"、"git" 这类混合内容也能被拼音串命中
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (!Loaded.Value) continue;
            var offset = c - Base;
            if ((uint)offset >= (uint)Span) continue;

            var slot = useAlt && Alt[offset] != 0 ? Alt[offset] : Primary[offset];
            if (slot == 0) continue;

            var pinyin = _syllables[slot];
            if (pinyin.Length == 0) continue;
            if (asInitial) sb.Append(pinyin[0]);
            else sb.Append(pinyin);
        }
        return sb.ToString();
    }
}
