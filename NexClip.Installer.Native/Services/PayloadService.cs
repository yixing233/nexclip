using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;

namespace NexClip.Installer.Native.Services;

public static class PayloadService
{
    internal static IReadOnlyList<string> RequiredPayloadFiles { get; } =
        ["NexClip.exe", "NexClip.Tray.dll", "Svg.dll"];

    private static Stream? GetPayloadStream()
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("NexClip.Installer.Native.Resources.payload.zip");
        if (stream != null) return stream;

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
            {
                return asm.GetManifestResourceStream(name);
            }
        }
        return null;
    }

    /// <summary>
    /// 检查程序集内是否内置了 payload.zip
    /// </summary>
    public static bool HasEmbeddedPayload()
    {
        using var stream = GetPayloadStream();
        return stream != null && stream.Length > 0;
    }

    internal static long GetExpandedPayloadSizeBytes()
    {
        using var stream = GetPayloadStream();
        if (stream == null)
        {
            return 0;
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        try
        {
            return archive.Entries.Aggregate(0L, (total, entry) => checked(total + entry.Length));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    internal static void ValidateEmbeddedPayload()
    {
        using var stream = GetPayloadStream() ?? throw new InvalidOperationException("安装包缺少 payload.zip。");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!TryResolveEntryPath(Path.GetTempPath(), entry.FullName, out _))
            {
                throw new InvalidDataException($"Payload 包含非法路径：{entry.FullName}");
            }

            if (!string.IsNullOrEmpty(entry.Name))
            {
                files.Add(entry.FullName.Replace('/', '\\').TrimStart('\\'));
            }
        }

        var missing = RequiredPayloadFiles.Where(required =>
            !files.Contains(required) && !files.Any(file => file.EndsWith("\\" + required, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Payload 缺少必要文件：{string.Join(", ", missing)}");
        }
    }

    internal static bool TryResolveEntryPath(string destination, string entryName, out string targetPath)
    {
        targetPath = string.Empty;
        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            targetPath = Path.GetFullPath(Path.Combine(destination, entryName.Replace('/', Path.DirectorySeparatorChar)));
            return targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解压嵌入的 payload.zip 到目标安装目录
    /// </summary>
    public static async Task ExtractPayloadAsync(string destDir, Action<double, string> onProgress, CancellationToken cancellationToken = default)
    {
        using var stream = GetPayloadStream();
        if (stream == null)
        {
            throw new InvalidOperationException("未在安装包中找到嵌入的应用程序核心数据包 (payload.zip)。");
        }

        Directory.CreateDirectory(destDir);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries;
        int total = entries.Count;
        if (total == 0) return;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")))
            {
                // 目录条目
                if (!TryResolveEntryPath(destDir, entry.FullName, out var dirPath))
                {
                    throw new InvalidDataException($"Payload 包含非法路径：{entry.FullName}");
                }
                Directory.CreateDirectory(dirPath);
                continue;
            }

            if (!TryResolveEntryPath(destDir, entry.FullName, out var targetPath))
            {
                throw new InvalidDataException($"Payload 包含非法路径：{entry.FullName}");
            }
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // 解压文件 (支持覆盖重试)
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int retries = 3;
                while (retries > 0)
                {
                    try
                    {
                        entry.ExtractToFile(targetPath, overwrite: true);
                        break;
                    }
                    catch (IOException) when (retries > 1)
                    {
                        retries--;
                        cancellationToken.ThrowIfCancellationRequested();
                        System.Threading.Thread.Sleep(200);
                    }
                }
            }, cancellationToken);

            double progress = (double)(i + 1) / total;
            onProgress?.Invoke(progress, entry.Name);
        }
    }

    internal static async Task InstallPayloadWithRollbackAsync(
        string destination,
        Action<double, string> onProgress,
        CancellationToken cancellationToken = default)
    {
        ValidateEmbeddedPayload();

        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("无法确定安装目录的父目录。");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".NexClip-staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".NexClip-backup-{Guid.NewGuid():N}");
        try
        {
            await ExtractPayloadAsync(staging, onProgress, cancellationToken).ConfigureAwait(false);
            ValidateDirectoryPayload(staging);
            cancellationToken.ThrowIfCancellationRequested();

            var hadDestination = Directory.Exists(fullDestination);
            if (hadDestination)
            {
                Directory.Move(fullDestination, backup);
            }

            try
            {
                Directory.Move(staging, fullDestination);
            }
            catch
            {
                if (hadDestination && Directory.Exists(backup) && !Directory.Exists(fullDestination))
                {
                    Directory.Move(backup, fullDestination);
                }
                throw;
            }

            TryDeleteDirectory(backup);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    internal static void ValidateDirectoryPayload(string directory)
    {
        var missing = RequiredPayloadFiles
            .Where(required => !File.Exists(Path.Combine(directory, required)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Payload 缺少必要文件：{string.Join(", ", missing)}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 生成/复制卸载器到安装目录
    /// </summary>
    public static bool DeployUninstaller(string destDir)
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                var targetUninstaller = Path.Combine(destDir, "Uninstall.exe");
                File.Copy(currentExe, targetUninstaller, overwrite: true);
                return File.Exists(targetUninstaller);
            }
        }
        catch
        {
        }

        return false;
    }
}

