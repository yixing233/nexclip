using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;

namespace NexClip.Installer.Native.Services;

public static class PayloadService
{
    private static Stream? GetPayloadStream()
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("NexClip.Installer.Native.Resources.payload.zip")
                  ?? asm.GetManifestResourceStream("NexClip.Installer.Resources.payload.zip");
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

    /// <summary>
    /// 解压嵌入的 payload.zip 到目标安装目录
    /// </summary>
    public static async Task ExtractPayloadAsync(string destDir, Action<double, string> onProgress)
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
            var entry = entries[i];
            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")))
            {
                // 目录条目
                var dirPath = Path.Combine(destDir, entry.FullName);
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var targetPath = Path.Combine(destDir, entry.FullName);
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // 解压文件 (支持覆盖重试)
            await Task.Run(() =>
            {
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
                        System.Threading.Thread.Sleep(200);
                    }
                }
            });

            double progress = (double)(i + 1) / total;
            onProgress?.Invoke(progress, entry.Name);
        }
    }

    /// <summary>
    /// 生成/复制卸载器到安装目录
    /// </summary>
    public static void DeployUninstaller(string destDir)
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                var targetUninstaller = Path.Combine(destDir, "Uninstall.exe");
                File.Copy(currentExe, targetUninstaller, overwrite: true);
            }
        }
        catch
        {
        }
    }
}

