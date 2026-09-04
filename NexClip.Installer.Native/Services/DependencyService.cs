using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;

namespace NexClip.Installer.Native.Services;

internal static class DependencyService
{
    /// <summary>
    /// 运行环境依赖定义来自随安装器打包的 installer\setup-dependencies.json，
    /// 由构建脚本在打包前完成地址与哈希校验，运行时不再硬编码版本号。
    /// </summary>
    internal static IReadOnlyList<DependencyDefinition> Dependencies { get; } = DependencyManifest.Load();

    internal static string LogDirectory => SetupLog.Directory;

    internal static string LogFilePath => SetupLog.FilePath;

    internal static void WriteLog(string message) => SetupLog.Write(message);

    internal static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = true
        };

        try
        {
            var environmentProxy = Environment.GetEnvironmentVariable("https_proxy") ??
                Environment.GetEnvironmentVariable("http_proxy") ??
                Environment.GetEnvironmentVariable("all_proxy");
            handler.Proxy = string.IsNullOrWhiteSpace(environmentProxy)
                ? WebRequest.GetSystemWebProxy()
                : new WebProxy(environmentProxy);
            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
        }
        catch
        {
        }

        // 单次请求不再设置总超时，改由连接超时与停滞超时精细控制，避免大文件被强行中断
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexClip-Installer/20260904.01");
        return client;
    }

    /// <summary>离线环境下立即给出可操作提示，避免用户干等数分钟的连接重试。</summary>
    private static bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }

    internal static bool IsInstalled(DependencyDefinition dependency) => dependency.Kind switch
    {
        DependencyKind.VisualCppRuntime => IsVCRedistInstalled(dependency.MinimumVersion),
        DependencyKind.DotNetDesktopRuntime =>
            IsDotNet9Installed(dependency.RequiredMajorVersion, dependency.MinimumVersion),
        DependencyKind.WindowsAppRuntime => IsWindowsAppSdkInstalled(
            dependency.RequiredPackageName,
            dependency.RequiredMainPackageName,
            dependency.MinimumVersion),
        _ => false
    };

    internal static DependencyDefinition GetDependency(DependencyKind kind) =>
        Dependencies.Single(dependency => dependency.Kind == kind);

    public static bool IsVCRedistInstalled() =>
        IsVCRedistInstalled(GetDependency(DependencyKind.VisualCppRuntime).MinimumVersion);

    private static bool IsVCRedistInstalled(Version? minimumVersion)
    {
        // 64 位视图为权威来源；部分系统仅在 WOW6432Node 留有可用版本号，两者取其一满足即可
        string[] runtimeKeys =
        [
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
            @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"
        ];

        foreach (var path in runtimeKeys)
        {
            if (TryReadVCRedistVersion(path, out var version) &&
                (minimumVersion is null || version >= minimumVersion))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadVCRedistVersion(string keyPath, out Version version)
    {
        version = new Version(0, 0);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key is null || Convert.ToInt32(key.GetValue("Installed", 0)) != 1)
            {
                return false;
            }

            var versionText = Convert.ToString(key.GetValue("Version"))?.TrimStart('v', 'V');
            if (Version.TryParse(versionText, out var parsed))
            {
                version = parsed;
                return true;
            }

            // 少数系统缺失 Version 字符串，改用 Major/Minor/Bld 数值组合
            var major = Convert.ToInt32(key.GetValue("Major", 0));
            var minor = Convert.ToInt32(key.GetValue("Minor", 0));
            var build = Convert.ToInt32(key.GetValue("Bld", 0));
            if (major <= 0)
            {
                return false;
            }

            version = new Version(major, minor, build);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or FormatException or
                InvalidCastException or OverflowException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool IsDotNet9Installed()
    {
        var dependency = GetDependency(DependencyKind.DotNetDesktopRuntime);
        return IsDotNet9Installed(dependency.RequiredMajorVersion, dependency.MinimumVersion);
    }

    private static bool IsDotNet9Installed(int requiredMajorVersion, Version? minimumVersion)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddDotNetRoot(roots, Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));
        AddDotNetRoot(roots, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        AddDotNetRoot(roots, ReadRegistryString(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64", "InstallLocation"));
        AddDotNetRoot(roots, ReadRegistryString(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost", "Path"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "dotnet"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            roots.Add(Path.Combine(localAppData, "Microsoft", "dotnet"));
        }

        if (roots.Any(root => AreDotNetFrameworksSupported(root, requiredMajorVersion, minimumVersion)))
        {
            return true;
        }

        // 目录布局非标准（自定义安装位置 / 企业镜像）时改用注册表 sharedfx 清单
        return AreDotNetFrameworksRegistered(requiredMajorVersion, minimumVersion);
    }

    private static string? ReadRegistryString(string keyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static bool AreDotNetFrameworksRegistered(int requiredMajorVersion, Version? minimumVersion)
    {
        static bool HasFramework(string frameworkName, int major, Version? minimum)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(
                    $@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\{frameworkName}");
                return key?.GetValueNames()
                    .Any(name => IsSupportedRuntimeDirectory(name, major, minimum)) == true;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                return false;
            }
        }

        return HasFramework("Microsoft.NETCore.App", requiredMajorVersion, minimumVersion) &&
            HasFramework("Microsoft.WindowsDesktop.App", requiredMajorVersion, minimumVersion);
    }

    private static void AddDotNetRoot(ISet<string> roots, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.Add(path.Trim());
        }
    }

    internal static bool IsSupportedRuntimeDirectory(
        string directoryName,
        int requiredMajorVersion,
        Version? minimumVersion = null)
    {
        if (directoryName.Contains('-', StringComparison.Ordinal) ||
            !Version.TryParse(directoryName, out var version))
        {
            return false;
        }

        return version.Major == requiredMajorVersion &&
            (minimumVersion is null || version >= minimumVersion);
    }

    internal static bool AreDotNetFrameworksSupported(
        string root,
        int requiredMajorVersion,
        Version? minimumVersion = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        static bool HasSupportedVersion(string path, int major, Version? minimum)
        {
            try
            {
                return Directory.Exists(path) && Directory.EnumerateDirectories(path)
                    .Select(Path.GetFileName)
                    .Any(name => name is not null && IsSupportedRuntimeDirectory(name, major, minimum));
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                return false;
            }
        }

        return HasSupportedVersion(
                Path.Combine(root, "shared", "Microsoft.NETCore.App"),
                requiredMajorVersion,
                minimumVersion) &&
            HasSupportedVersion(
                Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App"),
                requiredMajorVersion,
                minimumVersion);
    }

    public static bool IsWindowsAppSdkInstalled()
    {
        var dependency = GetDependency(DependencyKind.WindowsAppRuntime);
        return IsWindowsAppSdkInstalled(
            dependency.RequiredPackageName,
            dependency.RequiredMainPackageName,
            dependency.MinimumVersion);
    }

    private static bool IsWindowsAppSdkInstalled(
        string packageName,
        string mainPackageName,
        Version? minimumVersion)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        var packageNames = EnumerateInstalledAppPackageNames();
        if (!packageNames.Any(name => IsSupportedWindowsAppRuntimePackage(name, packageName, minimumVersion)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(mainPackageName))
        {
            return true;
        }

        // 框架包提供 WinUI 运行时，Main 包负责为非打包应用注册 DDLM；两者缺一都会导致启动失败
        return packageNames.Any(name =>
            IsSupportedWindowsAppRuntimePackage(name, mainPackageName, minimumVersion));
    }

    /// <summary>
    /// 宽松判定：仅检查核心框架包是否存在，用于安装后验收，避免因 Main 包注册滞后而误报失败。
    /// </summary>
    internal static bool IsCoreRuntimePresent(DependencyDefinition dependency) => dependency.Kind switch
    {
        DependencyKind.WindowsAppRuntime => IsWindowsAppSdkInstalled(
            dependency.RequiredPackageName,
            string.Empty,
            dependency.MinimumVersion),
        _ => IsInstalled(dependency)
    };

    /// <summary>
    /// 枚举可用的 MSIX 包全名。优先当前用户已注册的包（HKCU Repository），
    /// 机器级仓库（HKLM PackageRepository）可能残留已卸载或仅暂存的版本，因此额外要求包目录真实存在，
    /// 避免检测通过但应用启动时解析不到运行时。
    /// </summary>
    private static IReadOnlyCollection<string> EnumerateInstalledAppPackageNames()
    {
        var perUser = ReadPackageRepository(
            RegistryHive.CurrentUser,
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
            requireExistingPayload: false);
        if (perUser.Count > 0)
        {
            return perUser;
        }

        var machineWide = ReadPackageRepository(
            RegistryHive.LocalMachine,
            @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages",
            requireExistingPayload: true);
        if (machineWide.Count > 0)
        {
            return machineWide;
        }

        var fromDisk = EnumerateWindowsAppsDirectories();
        return fromDisk.Count > 0 ? fromDisk : QueryAppxPackageFullNamesWithPowerShell();
    }

    private static HashSet<string> ReadPackageRepository(
        RegistryHive hive,
        string path,
        bool requireExistingPayload)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key is null)
            {
                return names;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (!requireExistingPayload || PackagePayloadExists(key, name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }

        return names;
    }

    private static bool PackagePayloadExists(RegistryKey repositoryKey, string packageFullName)
    {
        try
        {
            using var packageKey = repositoryKey.OpenSubKey(packageFullName);
            if (packageKey?.GetValue("Path") is string path && !string.IsNullOrWhiteSpace(path))
            {
                return Directory.Exists(path);
            }

            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps",
                packageFullName);
            return Directory.Exists(windowsApps);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>注册表被组策略限制时回退到 WindowsApps 目录清单。</summary>
    private static HashSet<string> EnumerateWindowsAppsDirectories()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");
            if (!Directory.Exists(windowsApps))
            {
                return names;
            }

            foreach (var pattern in new[] { "*WindowsAppRuntime*", "*WinAppRuntime*" })
            {
                foreach (var directory in Directory.EnumerateDirectories(windowsApps, pattern))
                {
                    names.Add(Path.GetFileName(directory));
                }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
        }

        return names;
    }

    internal static bool IsSupportedWindowsAppRuntimePackage(
        string packageFullName,
        string packageName,
        Version? minimumVersion)
    {
        var prefix = packageName + "_";
        if (!packageFullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = packageFullName.Split('_');
        return parts.Length >= 3 &&
            (parts[2].Equals("x64", StringComparison.OrdinalIgnoreCase) ||
             parts[2].Equals("neutral", StringComparison.OrdinalIgnoreCase)) &&
            IsSupportedPackageVersion(parts[1], minimumVersion);
    }

    internal static bool IsSupportedPackageVersion(string? versionText, Version? minimumVersion)
    {
        return Version.TryParse(versionText?.Trim(), out var version) &&
            (minimumVersion is null || version >= minimumVersion);
    }

    private static IReadOnlyCollection<string> QueryAppxPackageFullNamesWithPowerShell()
    {
        const string command =
            "Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*','MicrosoftCorporationII.WinAppRuntime.*' " +
            "-ErrorAction SilentlyContinue | Select-Object -ExpandProperty PackageFullName";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\""
            });
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(true); } catch { }
                return [];
            }

            return process.ExitCode != 0
                ? []
                : output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return [];
        }
    }

    internal static async Task<DependencyInstallResult> InstallDependencyAsync(
        DependencyDefinition dependency,
        Action<DependencyProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled(dependency))
        {
            onProgress?.Invoke(new(dependency, DependencyInstallStage.Completed, 1.0, "运行环境已就绪"));
            return new DependencyInstallResult(false);
        }

        if (!IsNetworkAvailable())
        {
            throw new InvalidOperationException(
                $"当前设备没有可用网络连接，无法下载 {dependency.DisplayName}。" +
                $"请连接网络后重试，或手动安装：{dependency.ManualDownloadPage}");
        }

        Directory.CreateDirectory(DownloadCacheDirectory);
        PruneDownloadCache();
        var installerPath = Path.Combine(DownloadCacheDirectory, dependency.FileName);

        try
        {
            WriteLog($"开始配置依赖：{dependency.DisplayName}");
            using var client = CreateHttpClient();
            await DownloadVerifier.DownloadAsync(
                client,
                dependency.Sources,
                installerPath,
                dependency.MaximumDownloadBytes,
                dependency.ExpectedDownloadBytes,
                (progress, detail) => onProgress?.Invoke(new(
                    dependency,
                    DependencyInstallStage.Downloading,
                    progress * 0.82,
                    detail)),
                cancellationToken).ConfigureAwait(false);

            onProgress?.Invoke(new(
                dependency,
                DependencyInstallStage.Verifying,
                0.86,
                "正在验证 Microsoft 数字签名..."));
            DownloadVerifier.VerifyTrustedMicrosoftSignature(installerPath);

            onProgress?.Invoke(new(
                dependency,
                DependencyInstallStage.Installing,
                0.90,
                "正在静默安装，请稍候..."));
            var restartRequired = await RunDependencyInstallerAsync(
                dependency,
                installerPath,
                dependency.SilentArguments,
                cancellationToken,
                status => onProgress?.Invoke(new(
                    dependency,
                    DependencyInstallStage.Installing,
                    0.90,
                    status))).ConfigureAwait(false);

            onProgress?.Invoke(new(
                dependency,
                DependencyInstallStage.Installing,
                0.93,
                "正在等待系统完成组件注册..."));
            var detected = await WaitForDependencyAsync(dependency, cancellationToken).ConfigureAwait(false);
            if (!detected && !string.IsNullOrEmpty(dependency.RepairArguments))
            {
                // 静默安装可能因残留旧版本被跳过，用修复/强制参数再执行一次
                WriteLog($"{dependency.DisplayName} 首次安装后未检测到，尝试修复安装。");
                onProgress?.Invoke(new(
                    dependency,
                    DependencyInstallStage.Installing,
                    0.94,
                    "正在修复安装组件..."));
                restartRequired |= await RunDependencyInstallerAsync(
                    dependency,
                    installerPath,
                    dependency.RepairArguments,
                    cancellationToken,
                    status => onProgress?.Invoke(new(
                        dependency,
                        DependencyInstallStage.Installing,
                        0.94,
                        status))).ConfigureAwait(false);
                detected = await WaitForDependencyAsync(dependency, cancellationToken).ConfigureAwait(false);
            }

            if (!detected && !IsCoreRuntimePresent(dependency))
            {
                throw new InvalidOperationException(
                    $"{dependency.DisplayName} 安装后仍未检测到，请手动安装后重试：{dependency.ManualDownloadPage}");
            }

            var detail = detected
                ? restartRequired ? "配置完成，系统需要重启" : "配置完成"
                : "已安装，重启后生效";
            WriteLog($"{dependency.DisplayName} 配置结果：{detail}");
            onProgress?.Invoke(new(dependency, DependencyInstallStage.Completed, 1.0, detail));
            // 安装成功后立即回收缓存；失败时保留安装包与断点续传残片，供用户重试时直接复用
            TryDeleteCachedInstaller(dependency);
            return new DependencyInstallResult(restartRequired, detected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WriteLog($"{dependency.DisplayName} 配置失败：{exception}");
            throw;
        }
    }

    /// <summary>
    /// 依赖安装包缓存目录。使用固定路径（而非一次性随机目录）让下载在重试之间可续传、可复用，
    /// 避免网络中断后重复下载上百 MB 的运行时安装包。
    /// </summary>
    internal static string DownloadCacheDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "NexClip-Setup", "cache");

    /// <summary>缓存文件最长保留时长；超期条目通常来自已被新版安装器替换的旧依赖版本。</summary>
    internal static TimeSpan DownloadCacheRetention { get; } = TimeSpan.FromDays(7);

    /// <summary>清理过期缓存，避免版本更迭后旧安装包与续传残片长期占用磁盘。</summary>
    internal static void PruneDownloadCache() => PruneDownloadCache(DownloadCacheDirectory);

    internal static void PruneDownloadCache(string directory)
    {
        try
        {
            var expiry = DateTime.UtcNow - DownloadCacheRetention;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (File.GetLastWriteTimeUtc(file) < expiry)
                {
                    File.Delete(file);
                    WriteLog($"已清理过期依赖缓存：{Path.GetFileName(file)}");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private static void TryDeleteCachedInstaller(DependencyDefinition dependency)
    {
        try
        {
            var installerPath = Path.Combine(DownloadCacheDirectory, dependency.FileName);
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }

            foreach (var partial in Directory.EnumerateFiles(
                DownloadCacheDirectory,
                dependency.FileName + ".*.partial"))
            {
                File.Delete(partial);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// 按清单顺序补齐缺失依赖。单项失败不再中断整体流程：
    /// 其余组件继续安装，最后汇总失败项抛出 <see cref="DependencyConfigurationException"/>，
    /// 避免第一个组件失败导致其他本可自动装好的组件也留给用户手动处理。
    /// </summary>
    public static async Task<DependencyInstallResult> EnsureDependenciesAsync(
        Action<double, string>? onProgress,
        CancellationToken cancellationToken = default)
    {
        var missing = Dependencies.Where(dependency => !IsInstalled(dependency)).ToArray();
        if (missing.Length == 0)
        {
            onProgress?.Invoke(1.0, "系统运行环境已就绪");
            return new DependencyInstallResult(false);
        }

        WriteLog($"待配置依赖：{string.Join("、", missing.Select(item => item.DisplayName))}");
        var restartRequired = false;
        var detectionConfirmed = true;
        var failures = new List<(DependencyDefinition Dependency, Exception Error)>();
        for (var index = 0; index < missing.Length; index++)
        {
            var completed = index;
            var dependency = missing[index];
            try
            {
                var result = await InstallDependencyAsync(
                    dependency,
                    report =>
                    {
                        var overall = (completed + Math.Clamp(report.Progress, 0.0, 1.0)) / missing.Length;
                        onProgress?.Invoke(overall, $"{report.Dependency.DisplayName} · {report.Detail}");
                    },
                    cancellationToken).ConfigureAwait(false);
                restartRequired |= result.RestartRequired;
                detectionConfirmed &= result.DetectionConfirmed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add((dependency, exception));
                onProgress?.Invoke(
                    (completed + 1.0) / missing.Length,
                    $"{dependency.DisplayName} · 配置失败，继续处理其余组件");
            }
        }

        if (failures.Count > 0)
        {
            throw new DependencyConfigurationException(
                failures.Select(failure => failure.Dependency).ToArray(),
                string.Join(
                    Environment.NewLine,
                    failures.Select(failure => $"· {failure.Dependency.DisplayName}：{failure.Error.Message}")),
                failures[0].Error);
        }

        onProgress?.Invoke(1.0, restartRequired || !detectionConfirmed
            ? "环境依赖配置完成，系统需要重启"
            : "环境依赖配置完成");
        return new DependencyInstallResult(restartRequired, detectionConfirmed);
    }

    /// <summary>
    /// 执行依赖安装并返回是否需要重启；“已安装/更高版本”类退出码视为成功，
    /// “另一个安装正在进行”（1618 / 0x80073D00）会退避后重试，避免与系统更新抢锁时直接失败。
    /// </summary>
    private static async Task<bool> RunDependencyInstallerAsync(
        DependencyDefinition dependency,
        string installerPath,
        string arguments,
        CancellationToken cancellationToken,
        Action<string>? onStatus = null)
    {
        var fullArguments = AppendInstallerLogArgument(dependency, arguments);
        for (var attempt = 1; ; attempt++)
        {
            var exitCode = await RunInstallerAsync(installerPath, fullArguments, cancellationToken)
                .ConfigureAwait(false);
            WriteLog($"{dependency.DisplayName} 安装程序退出码：0x{exitCode:X8} ({exitCode}) (参数: {fullArguments})");

            if (SetupPolicy.IsSuccessfulInstallerExitCode(exitCode))
            {
                return SetupPolicy.RequiresRestart(exitCode);
            }

            if (SetupPolicy.IsUserCancelledExitCode(exitCode))
            {
                throw new OperationCanceledException($"{dependency.DisplayName} 安装被用户取消。");
            }

            if (SetupPolicy.IsInstallerBusyExitCode(exitCode) && attempt < SetupPolicy.InstallerBusyMaxAttempts)
            {
                var delay = SetupPolicy.GetRetryDelay(attempt);
                onStatus?.Invoke($"系统正忙于其他安装，{delay.TotalSeconds:F0} 秒后重试...");
                WriteLog($"{dependency.DisplayName} 安装被系统占用，{delay.TotalSeconds:F0} 秒后重试 ({attempt}/{SetupPolicy.InstallerBusyMaxAttempts})。");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new InvalidOperationException(
                $"{dependency.DisplayName} 安装失败，退出代码 0x{exitCode:X8}。诊断日志：{LogFilePath}");
        }
    }

    /// <summary>为支持 /log 的 Microsoft Bootstrapper 附加日志路径，便于失败后取证。</summary>
    private static string AppendInstallerLogArgument(DependencyDefinition dependency, string arguments)
    {
        if (dependency.Kind is not (DependencyKind.VisualCppRuntime or DependencyKind.DotNetDesktopRuntime))
        {
            return arguments;
        }

        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(
                LogDirectory,
                Path.GetFileNameWithoutExtension(dependency.FileName) + ".log");
            return arguments + " /log \"" + logPath + "\"";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return arguments;
        }
    }

    /// <summary>
    /// 启动依赖安装程序并捕获控制台输出。WindowsAppRuntimeInstall 不支持 /log，
    /// 逐包部署结果只出现在 stdout 上，落盘后才能定位“哪个 MSIX 包部署失败”。
    /// </summary>
    private static async Task<int> RunInstallerAsync(
        string installerPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => AppendInstallerOutput(output, args.Data);
        process.ErrorDataReceived += (_, args) => AppendInstallerOutput(output, args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(installerPath)}。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            return await SetupPolicy.WaitForInstallerAsync(process, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (output.Length > 0)
            {
                WriteLog($"{Path.GetFileName(installerPath)} 输出：{Environment.NewLine}{output}");
            }
        }
    }

    private static void AppendInstallerOutput(StringBuilder buffer, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (buffer)
        {
            // 只保留末尾的关键输出，避免个别安装程序刷屏导致日志失控
            if (buffer.Length < 16 * 1024)
            {
                buffer.AppendLine(line.Trim());
            }
        }
    }

    /// <summary>组件注册可能滞后于安装程序退出，轮询等待直到检测超时。</summary>
    private static async Task<bool> WaitForDependencyAsync(
        DependencyDefinition dependency,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)SetupPolicy.DependencyDetectionTimeout.TotalMilliseconds;
        var delayMilliseconds = 250;
        while (true)
        {
            if (IsInstalled(dependency))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            delayMilliseconds = Math.Min(2_000, delayMilliseconds * 2);
        }
    }

}
