namespace NexClip.Installer.Native.Services;

internal enum DependencyKind
{
    VisualCppRuntime,
    DotNetDesktopRuntime,
    WindowsAppRuntime
}

internal enum DependencyInstallStage
{
    Downloading,
    Verifying,
    Installing,
    Completed
}

/// <summary>
/// 单个下载源。<paramref name="Sha256"/> 非空时下载完成后执行强校验；
/// evergreen 链接（内容随微软发布滚动变化）留空，仅做 Authenticode 签名校验。
/// </summary>
internal sealed record DependencySource(Uri Uri, string Sha256 = "")
{
    internal bool HasPinnedHash => Sha256.Length == 64;
}

/// <summary>
/// <paramref name="Sources"/> 按优先级排列：首选带哈希的固定链接，失败后回退 evergreen 链接。
/// <paramref name="RepairArguments"/> 用于首次静默安装未生效时的强制修复重试。
/// </summary>
internal sealed record DependencyDefinition(
    DependencyKind Kind,
    string DisplayName,
    IReadOnlyList<DependencySource> Sources,
    string FileName,
    string SilentArguments,
    long MaximumDownloadBytes,
    long ExpectedDownloadBytes,
    Uri ManualDownloadPage,
    Version? MinimumVersion = null,
    int RequiredMajorVersion = 0,
    string RequiredPackageName = "",
    string RequiredMainPackageName = "",
    string RepairArguments = "")
{
    internal Uri DownloadUri => Sources[0].Uri;

    internal IReadOnlyList<Uri> DownloadUris => Sources.Select(source => source.Uri).ToArray();
}

internal sealed record DependencyProgress(
    DependencyDefinition Dependency,
    DependencyInstallStage Stage,
    double Progress,
    string Detail);

/// <summary>
/// <paramref name="DetectionConfirmed"/> 为 false 表示安装器返回成功但系统尚未完成注册，
/// 通常重启后生效，不应视为安装失败。
/// </summary>
internal sealed record DependencyInstallResult(bool RestartRequired, bool DetectionConfirmed = true);

/// <summary>
/// 汇总一轮依赖配置中所有失败项，便于 UI 一次性列出需要手动补装的组件。
/// </summary>
internal sealed class DependencyConfigurationException(
    IReadOnlyList<DependencyDefinition> failedDependencies,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    internal IReadOnlyList<DependencyDefinition> FailedDependencies { get; } = failedDependencies;
}
