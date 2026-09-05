using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using NexClip.Installer.Native.Win32;

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

    /// <summary>整目录改名的重试次数，间隔按 250ms × 次数递增。</summary>
    private const int MoveAttempts = 6;

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

            // 上一次安装失败可能留下改名让位的旧文件，先清掉再开始
            PurgeAbandonedFiles(fullDestination);

            if (!Directory.Exists(fullDestination))
            {
                await MoveDirectoryAsync(staging, fullDestination, cancellationToken).ConfigureAwait(false);
                return;
            }

            // 首选整目录改名：旧版本一次性让位，失败可原样搬回，最干净。
            // 但只要还有进程把安装目录当成当前工作目录（或在里面开着文件），
            // 改名就会 ERROR_SHARING_VIOLATION —— 目录句柄不带 FILE_SHARE_DELETE。
            if (await TryMoveDirectoryAsync(fullDestination, backup, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await MoveDirectoryAsync(staging, fullDestination, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (Directory.Exists(backup) && !Directory.Exists(fullDestination))
                    {
                        try { Directory.Move(backup, fullDestination); } catch { }
                    }
                    throw;
                }

                TryDeleteDirectory(backup);
                return;
            }

            // 目录改不动就退回逐文件覆盖：单个文件即使正被占用也能改名让位，
            // 原句柄继续指向改名后的文件，新文件立刻就位，旧文件随后删掉或登记重启后删。
            ReplaceInPlace(staging, fullDestination, onProgress, cancellationToken);
            ValidateDirectoryPayload(fullDestination);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>带重试的目录改名，最后一次仍失败就把原始异常抛出去。</summary>
    private static async Task MoveDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                // 杀进程后句柄释放、杀毒软件扫描都可能慢一拍，退避再试
            }

            await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同上，但失败返回 false 而不抛异常，供调用方走回退方案。</summary>
    private static async Task<bool> TryMoveDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MoveAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MoveAttempts) return false;
            }

            await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
        }

        return false;
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

    /// <summary>
    /// 逐文件把 staging 覆盖到已存在的安装目录。每替换一个文件都记账，中途失败按相反顺序回滚，
    /// 避免留下半新半旧、根本起不来的安装目录。只覆盖与新增，不删除安装目录里的其它文件
    /// （Uninstall.exe、日志等不属于 payload，删了反而出事）。
    /// </summary>
    internal static void ReplaceInPlace(
        string staging, string destination, Action<double, string> onProgress, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
        var asides = new List<(string Target, string Aside)>();
        var added = new List<string>();
        try
        {
            foreach (var directory in Directory.GetDirectories(staging, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(staging, directory)));
            }

            for (var i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(staging, files[i]);
                var target = Path.Combine(destination, relative);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (File.Exists(target))
                {
                    var aside = TryMoveAside(target);
                    if (aside != null) asides.Add((target, aside));
                }
                else
                {
                    added.Add(target);
                }

                File.Copy(files[i], target, overwrite: true);

                // 进度已经在解压阶段跑到 100%，这里只刷文件名，不把进度条拽回去
                onProgress?.Invoke(1.0, relative);
            }
        }
        catch
        {
            RollbackInPlace(asides, added);
            throw;
        }

        foreach (var (_, aside) in asides)
        {
            ScheduleFileDeletion(aside);
        }
    }

    /// <summary>
    /// 把旧文件改名让位。exe/dll 这类被映射为镜像的文件是以 FILE_SHARE_READ|FILE_SHARE_DELETE
    /// 打开的：不允许覆盖写入，但允许改名和删除，所以正在运行时也能腾出名字。
    /// 返回 null 表示连改名都不行（句柄没给 delete 共享权），交由调用方直接覆盖试试。
    /// </summary>
    private static string? TryMoveAside(string target)
    {
        TryClearReadOnly(target);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var aside = $"{target}.nexclip-old-{Guid.NewGuid():N}";
            try
            {
                File.Move(target, aside);
                return aside;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) return null;
                Thread.Sleep(150 * attempt);
            }
        }

        return null;
    }

    private static void RollbackInPlace(List<(string Target, string Aside)> asides, List<string> added)
    {
        foreach (var path in added)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }

        for (var i = asides.Count - 1; i >= 0; i--)
        {
            var (target, aside) = asides[i];
            try
            {
                if (File.Exists(target)) File.Delete(target);
                if (File.Exists(aside)) File.Move(aside, target);
            }
            catch
            {
                // 回滚也失败就只能把让位文件留在原地，下次安装的 PurgeAbandonedFiles 会清
            }
        }
    }

    /// <summary>
    /// 能删就立刻删——镜像映射允许删除，NTFS 会马上摘掉目录项，旧进程的句柄照旧可用；
    /// 真删不掉（句柄没给 delete 共享权）就登记重启后删，不留垃圾也不打断安装。
    /// </summary>
    private static void ScheduleFileDeletion(string path)
    {
        try
        {
            TryClearReadOnly(path);
            File.Delete(path);
            return;
        }
        catch
        {
        }

        try
        {
            NativeMethods.MoveFileExW(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch
        {
        }
    }

    /// <summary>清理上一次逐文件覆盖遗留的让位文件。</summary>
    internal static void PurgeAbandonedFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*.nexclip-old-*", SearchOption.AllDirectories))
            {
                ScheduleFileDeletion(file);
            }
        }
        catch
        {
        }
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch
        {
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

