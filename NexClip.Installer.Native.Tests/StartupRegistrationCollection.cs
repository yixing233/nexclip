using Microsoft.Win32;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 串行执行器：所有会改动 <see cref="StartupRegistration.Root"/> 的测试类必须归到同一个集合。
///
/// 为什么需要：<c>Root</c> 是静态属性，两个测试类若并行运行会互相覆盖对方的
/// 沙箱子键，表现为随机的 Assert 失败（实测已发生）。xUnit 默认让不同测试类并行，
/// 因此这里用 Collection 强制串行，而不是把测试改成各自加锁——
/// 串行更简单，且这些测试本身很快（注册表操作，毫秒级）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StartupRegistrationCollection
{
    public const string Name = "StartupRegistration";
}
