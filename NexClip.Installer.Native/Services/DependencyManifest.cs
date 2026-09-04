using System.Text.Json;

namespace NexClip.Installer.Native.Services;

/// <summary>
/// 解析随安装器一起打包的运行环境依赖清单（installer\setup-dependencies.json）。
/// 固定版本地址与 SHA-256 集中在清单中维护，构建脚本负责在打包前校验，
/// 运行时只做严格解析，避免把易漂移的哈希散落在代码里。
/// </summary>
internal static class DependencyManifest
{
    internal const string ResourceName = "NexClip.Installer.Native.Resources.setup-dependencies.json";

    private const int SupportedSchemaVersion = 1;

    private static readonly string[] ApprovedPrimaryHosts =
    [
        "download.microsoft.com",
        "download.visualstudio.microsoft.com",
        "builds.dotnet.microsoft.com"
    ];

    private static readonly string[] ApprovedFallbackHosts =
    [
        "aka.ms",
        "dotnet.microsoft.com",
        "learn.microsoft.com"
    ];

    internal static IReadOnlyList<DependencyDefinition> Load()
    {
        using var stream = typeof(DependencyManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("安装器缺少运行环境依赖清单资源。");
        return Parse(stream);
    }

    internal static IReadOnlyList<DependencyDefinition> Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("依赖清单格式无效。");
        }

        var schemaVersion = ReadInt32(root, "schemaVersion", "依赖清单");
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException($"依赖清单 schemaVersion {schemaVersion} 不受支持。");
        }

        return
        [
            ParseDependency(root, "visualCppRuntime", DependencyKind.VisualCppRuntime),
            ParseDependency(root, "dotNetDesktopRuntime", DependencyKind.DotNetDesktopRuntime),
            ParseDependency(root, "windowsAppRuntime", DependencyKind.WindowsAppRuntime)
        ];
    }

    private static DependencyDefinition ParseDependency(
        JsonElement root,
        string propertyName,
        DependencyKind kind)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"依赖清单缺少 {propertyName} 节点。");
        }

        var displayName = ReadString(node, "displayName", propertyName);
        var fileName = ReadFileName(node, propertyName);
        var expectedBytes = ReadInt64(node, "sizeBytes", propertyName);
        if (expectedBytes <= 0)
        {
            throw new InvalidDataException($"{propertyName}.sizeBytes 必须为正数。");
        }

        var maximumBytes = GetDownloadLimitBytes(kind);
        if (expectedBytes > maximumBytes)
        {
            throw new InvalidDataException(
                $"{propertyName}.sizeBytes 超过 {displayName} 允许的下载上限。");
        }

        var sources = new List<DependencySource>
        {
            new(
                ReadUri(node, "url", propertyName, ApprovedPrimaryHosts),
                ReadSha256(node, "sha256", propertyName))
        };

        var fallbackUri = ReadUri(node, "fallbackUrl", propertyName, ApprovedFallbackHosts, ApprovedPrimaryHosts);
        if (fallbackUri != sources[0].Uri)
        {
            sources.Add(new DependencySource(fallbackUri));
        }

        return new DependencyDefinition(
            kind,
            displayName,
            sources,
            fileName,
            ReadString(node, "silentArguments", propertyName),
            maximumBytes,
            expectedBytes,
            ReadUri(node, "manualDownloadPage", propertyName, ApprovedFallbackHosts, ApprovedPrimaryHosts),
            ReadVersion(node, "minimumVersion", propertyName),
            kind == DependencyKind.DotNetDesktopRuntime ? ReadInt32(node, "majorVersion", propertyName) : 0,
            kind == DependencyKind.WindowsAppRuntime ? ReadString(node, "packageName", propertyName) : string.Empty,
            kind == DependencyKind.WindowsAppRuntime ? ReadString(node, "mainPackageName", propertyName) : string.Empty,
            ReadString(node, "repairArguments", propertyName));
    }

    internal static long GetDownloadLimitBytes(DependencyKind kind) => kind switch
    {
        DependencyKind.VisualCppRuntime => SetupPolicy.VisualCppDownloadLimitBytes,
        DependencyKind.DotNetDesktopRuntime => SetupPolicy.DotNetDownloadLimitBytes,
        DependencyKind.WindowsAppRuntime => SetupPolicy.WindowsAppRuntimeDownloadLimitBytes,
        _ => throw new InvalidDataException($"未知的依赖类型 {kind}。")
    };

    private static string ReadString(JsonElement node, string name, string owner)
    {
        if (!node.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{owner}.{name} 缺失或为空。");
        }

        return value.GetString()!.Trim();
    }

    /// <summary>文件名会拼接到临时目录，必须排除路径分隔符与相对路径片段。</summary>
    private static string ReadFileName(JsonElement node, string owner)
    {
        var fileName = ReadString(node, "fileName", owner);
        if (fileName != Path.GetFileName(fileName) ||
            fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"{owner}.fileName 不是合法的文件名。");
        }

        return fileName;
    }

    private static int ReadInt32(JsonElement node, string name, string owner)
    {
        if (!node.TryGetProperty(name, out var value) || !value.TryGetInt32(out var parsed))
        {
            throw new InvalidDataException($"{owner}.{name} 不是有效的整数。");
        }

        return parsed;
    }

    private static long ReadInt64(JsonElement node, string name, string owner)
    {
        if (!node.TryGetProperty(name, out var value) || !value.TryGetInt64(out var parsed))
        {
            throw new InvalidDataException($"{owner}.{name} 不是有效的整数。");
        }

        return parsed;
    }

    private static Version ReadVersion(JsonElement node, string name, string owner)
    {
        if (!Version.TryParse(ReadString(node, name, owner), out var version))
        {
            throw new InvalidDataException($"{owner}.{name} 的版本号格式无效。");
        }

        return version;
    }

    private static string ReadSha256(JsonElement node, string name, string owner)
    {
        var value = ReadString(node, name, owner);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{owner}.{name} 不是合法的 SHA-256 值。");
        }

        return value.ToLowerInvariant();
    }

    private static Uri ReadUri(
        JsonElement node,
        string name,
        string owner,
        params string[][] allowedHostGroups)
    {
        var value = ReadString(node, name, owner);
        if (!SetupPolicy.TryCreateHttpsUri(value, out var uri))
        {
            throw new InvalidDataException($"{owner}.{name} 必须是绝对 HTTPS 地址。");
        }

        var allowed = allowedHostGroups.Any(group =>
            group.Contains(uri.Host, StringComparer.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidDataException($"{owner}.{name} 的主机 {uri.Host} 不在受信任的下载域名列表中。");
        }

        return uri;
    }
}