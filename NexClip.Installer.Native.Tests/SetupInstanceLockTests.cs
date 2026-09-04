using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

public sealed class SetupInstanceLockTests
{
    [Fact]
    public void SecondInstanceCannotAcquireLockWhileFirstIsHeld()
    {
        using (var first = SetupInstanceLock.TryAcquire())
        {
            Assert.NotNull(first);
            Assert.Null(SetupInstanceLock.TryAcquire());
        }

        using var afterRelease = SetupInstanceLock.TryAcquire();
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var instanceLock = SetupInstanceLock.TryAcquire();
        Assert.NotNull(instanceLock);

        instanceLock.Dispose();
        instanceLock.Dispose();

        using var reacquired = SetupInstanceLock.TryAcquire();
        Assert.NotNull(reacquired);
    }
}