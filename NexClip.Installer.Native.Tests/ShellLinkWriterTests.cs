using NexClip.Installer.Native.Win32;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// .lnk 快捷方式的二进制写入。
///
/// 这里守护的是一个**曾经写错过的真实 bug**：LinkInfo 里把 VolumeLabelOffset
/// 写成 0，快捷方式文件能生成、大小正常，但 Shell 解析后 TargetPath 为空 ——
/// 用户双击快捷方式毫无反应，且没有任何报错。
///
/// 因此测试的重点不是"文件存在"，而是**能被 Shell 正确解析**：
/// 用 COM(WScript.Shell)读回来比对 TargetPath / WorkingDirectory / Description。
/// 注意测试项目不是 AOT，COM 可以正常使用。
/// </summary>
public sealed class ShellLinkWriterTests : IDisposable
{
    private readonly string _directory;

    public ShellLinkWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NexClip.LnkTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>用 Shell 解析 .lnk，返回 (TargetPath, WorkingDirectory, Description)。</summary>
    private static (string Target, string WorkingDirectory, string Description) ReadWithShell(string lnkPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(type);
        dynamic shell = Activator.CreateInstance(type!)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnkPath);
            return (
                (string)(link.TargetPath ?? string.Empty),
                (string)(link.WorkingDirectory ?? string.Empty),
                (string)(link.Description ?? string.Empty));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    [Fact]
    public void CreatedShortcutResolvesToTheTargetExecutable()
    {
        // 用一个真实存在的 exe 作为目标，确保 Shell 不会因为"目标不存在"而走别的解析分支
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var lnk = Path.Combine(_directory, "Test.lnk");

        Assert.True(ShellLinkWriter.TryCreate(target, lnk, "测试快捷方式"));

        Assert.True(File.Exists(lnk));
        var (resolvedTarget, workingDir, description) = ReadWithShell(lnk);

        // 这条是回归的核心：写错 VolumeLabelOffset 时这里会是空字符串
        Assert.Equal(target, resolvedTarget, ignoreCase: true);
        Assert.Equal(Path.GetDirectoryName(target), workingDir, ignoreCase: true);
        Assert.Equal("测试快捷方式", description);
    }

    [Fact]
    public void ShortcutSupportsNonAsciiTargetAndDescription()
    {
        // 中文安装路径 + 中文描述：必须走 Unicode 分支，ANSI 路径会丢字符
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var lnk = Path.Combine(_directory, "NexClip 卸载.lnk");

        Assert.True(ShellLinkWriter.TryCreate(target, lnk, "卸载 NexClip 程序"));

        var (resolvedTarget, _, description) = ReadWithShell(lnk);
        Assert.Equal(target, resolvedTarget, ignoreCase: true);
        Assert.Equal("卸载 NexClip 程序", description);
    }

    [Fact]
    public void ShortcutCreatesMissingParentDirectory()
    {
        // 开始菜单里带子目录的情况（...\Programs\NexClip\NexClip.lnk）
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var lnk = Path.Combine(_directory, "sub", "deeper", "Nested.lnk");

        Assert.True(ShellLinkWriter.TryCreate(target, lnk));

        Assert.True(File.Exists(lnk));
        Assert.Equal(target, ReadWithShell(lnk).Target, ignoreCase: true);
    }

    [Fact]
    public void ExplicitWorkingDirectoryOverridesTargetFolder()
    {
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var lnk = Path.Combine(_directory, "WithWorkDir.lnk");
        var workDir = Path.Combine(_directory, "AppRoot");
        Directory.CreateDirectory(workDir);

        Assert.True(ShellLinkWriter.TryCreate(target, lnk, null, workDir));

        Assert.Equal(workDir, ReadWithShell(lnk).WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void OverwritingExistingShortcutLeavesNoStrayTempFile()
    {
        // 覆盖安装会重复创建同名快捷方式，不能留下 .tmp 残片
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var lnk = Path.Combine(_directory, "Repeat.lnk");

        Assert.True(ShellLinkWriter.TryCreate(target, lnk, "第一次"));
        Assert.True(ShellLinkWriter.TryCreate(target, lnk, "第二次"));

        Assert.Equal("第二次", ReadWithShell(lnk).Description);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void UnwritableShortcutLocationIsReportedAsFailureWithoutThrowing()
    {
        // 调用方不应因为快捷方式失败而中断安装：失败必须表现为返回值 false，而不是抛异常。
        // 注意 COM 的 SetPath 允许指向不存在的目标（快捷方式可以先建、目标后装），
        // 所以这里故意让**快捷方式路径本身**非法来制造失败。
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var illegalShortcut = Path.Combine(_directory, "bad" + (char)0 + "name.lnk");

        var result = ShellLinkWriter.TryCreate(target, illegalShortcut);

        Assert.False(result);
    }
}
