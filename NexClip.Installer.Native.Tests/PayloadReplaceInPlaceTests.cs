using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 覆盖安装的回退路径：整个安装目录改不动名时（有进程把它当作当前工作目录，或在里面开着文件），
/// 必须能逐文件覆盖，而且中途失败不能留下半新半旧的安装目录。
/// </summary>
public sealed class PayloadReplaceInPlaceTests
{
    [Fact]
    public void ReplaceInPlaceOverwritesPayloadFilesAndKeepsUnrelatedOnes()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteStaging("NexClip.exe", "new");
        sandbox.WriteStaging(Path.Combine("runtimes", "extra.dll"), "brand new");
        sandbox.WriteDestination("NexClip.exe", "old");
        sandbox.WriteDestination("Uninstall.exe", "keep me");

        PayloadService.ReplaceInPlace(sandbox.Staging, sandbox.Destination, (_, _) => { }, CancellationToken.None);

        Assert.Equal("new", sandbox.ReadDestination("NexClip.exe"));
        Assert.Equal("brand new", sandbox.ReadDestination(Path.Combine("runtimes", "extra.dll")));
        // payload 之外的文件不属于本次替换范围，删掉反而会毁掉卸载器
        Assert.Equal("keep me", sandbox.ReadDestination("Uninstall.exe"));
        Assert.Empty(sandbox.AsideFiles());
    }

    [Fact]
    public void ReplaceInPlaceMovesLockedFileAsideSoTheNewOneCanTakeItsName()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteStaging("NexClip.Tray.dll", "new");
        sandbox.WriteDestination("NexClip.Tray.dll", "old");

        // FileShare.Read | FileShare.Delete 就是 Windows 映射 exe/dll 镜像时的共享方式：
        // 不允许写入，但允许改名与删除——安装器正是靠这一点给新文件腾出名字。
        using (var locked = new FileStream(
                   sandbox.DestinationPath("NexClip.Tray.dll"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read | FileShare.Delete))
        {
            PayloadService.ReplaceInPlace(sandbox.Staging, sandbox.Destination, (_, _) => { }, CancellationToken.None);

            Assert.Equal("new", sandbox.ReadDestination("NexClip.Tray.dll"));

            // 旧句柄仍然指向改名后的文件，正在运行的旧进程不受影响
            using var reader = new StreamReader(locked);
            Assert.Equal("old", reader.ReadToEnd());

            // 让位文件在句柄还开着的时候就删掉了：NTFS 立刻摘掉目录项，句柄照旧可读。
            // 真删不掉才会登记成重启后删除。
            Assert.Empty(sandbox.AsideFiles());
        }
    }

    [Fact]
    public void PurgeAbandonedFilesClearsLeftoversFromAnEarlierFailedInstall()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteDestination($"NexClip.exe.nexclip-old-{Guid.NewGuid():N}", "leftover");
        sandbox.WriteDestination(Path.Combine("runtimes", $"extra.dll.nexclip-old-{Guid.NewGuid():N}"), "leftover");
        sandbox.WriteDestination("NexClip.exe", "keep");

        PayloadService.PurgeAbandonedFiles(sandbox.Destination);

        Assert.Empty(sandbox.AsideFiles());
        Assert.Equal("keep", sandbox.ReadDestination("NexClip.exe"));
    }

    [Fact]
    public void ReplaceInPlaceRollsBackEveryReplacedFileWhenOneFails()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteStaging("01-first.dll", "new first");
        sandbox.WriteStaging("02-second.dll", "new second");
        sandbox.WriteDestination("01-first.dll", "old first");
        // 目标位置是个同名目录，File.Copy 必然失败，用来模拟“替换到一半出错”
        Directory.CreateDirectory(sandbox.DestinationPath("02-second.dll"));

        Assert.ThrowsAny<Exception>(() => PayloadService.ReplaceInPlace(
            sandbox.Staging, sandbox.Destination, (_, _) => { }, CancellationToken.None));

        Assert.Equal("old first", sandbox.ReadDestination("01-first.dll"));
        Assert.Empty(sandbox.AsideFiles());
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "NexClip.ReplaceInPlaceTest", Guid.NewGuid().ToString("N"));

        public Sandbox()
        {
            Staging = Path.Combine(_root, "staging");
            Destination = Path.Combine(_root, "install");
            Directory.CreateDirectory(Staging);
            Directory.CreateDirectory(Destination);
        }

        public string Staging { get; }

        public string Destination { get; }

        public string DestinationPath(string relative) => Path.Combine(Destination, relative);

        public void WriteStaging(string relative, string content) => Write(Path.Combine(Staging, relative), content);

        public void WriteDestination(string relative, string content) => Write(DestinationPath(relative), content);

        public string ReadDestination(string relative) => File.ReadAllText(DestinationPath(relative));

        public string[] AsideFiles() =>
            Directory.GetFiles(Destination, "*.nexclip-old-*", SearchOption.AllDirectories);

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
