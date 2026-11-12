using System.Runtime.CompilerServices;
using NexClip.Desktop.Services;

namespace NexClip.Tray.Tests;

public class DeferredPreviewTests
{
    [Fact]
    public void HistoryPage_DecodesOnlyActivePreviews()
    {
        var allocations = 0;
        var previews = Enumerable.Range(0, 300)
            .Select(_ => new DeferredPreview<object>(() => { allocations++; return new object(); }))
            .ToArray();

        foreach (var preview in previews) Assert.Null(preview.Value);
        Assert.Equal(0, allocations);

        foreach (var preview in previews.Take(6))
        {
            preview.SetActive(true);
            Assert.NotNull(preview.Value);
            Assert.Same(preview.Value, preview.Value);
        }
        Assert.Equal(6, allocations);
    }

    [Fact]
    public void Activation_DoesNotDecodeUntilRequested()
    {
        var allocations = 0;
        var preview = new DeferredPreview<object>(() => { allocations++; return new object(); });

        Assert.True(preview.SetActive(true));
        Assert.False(preview.SetActive(true));
        Assert.Equal(0, allocations);
        Assert.NotNull(preview.Value);
        Assert.Equal(1, allocations);
    }

    [Fact]
    public void SuspendedPreview_DoesNotDecodeDuringBindingUpdates()
    {
        var allocations = 0;
        var preview = new DeferredPreview<object>(() => { allocations++; return new object(); }, isActive: true);
        var first = preview.Value;

        Assert.True(preview.SetActive(false));
        Assert.Null(preview.Value);
        preview.Invalidate();
        Assert.Null(preview.Value);
        Assert.Equal(1, allocations);

        preview.SetActive(true);
        Assert.NotSame(first, preview.Value);
        Assert.Equal(2, allocations);
    }

    [Fact]
    public void InvalidatedCurrentPreview_DecodesOnlyLatestContent()
    {
        var content = "first";
        var allocations = 0;
        var preview = new DeferredPreview<string>(() => { allocations++; return content; }, isActive: true);
        Assert.Equal("first", preview.Value);

        content = "second";
        preview.Invalidate();
        content = "latest";
        preview.Invalidate();
        Assert.Equal(1, allocations);
        Assert.Equal("latest", preview.Value);
        Assert.Equal(2, allocations);
    }

    [Fact]
    public void MissingImage_IsNotRepeatedlyLoadedUntilInvalidated()
    {
        var attempts = 0;
        var preview = new DeferredPreview<object>(() => { attempts++; return null; }, isActive: true);
        Assert.Null(preview.Value);
        Assert.Null(preview.Value);
        Assert.Equal(1, attempts);

        preview.Invalidate();
        Assert.Null(preview.Value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Deactivation_ReleasesCachedImageReference()
    {
        var (preview, image) = CreateCachedPreview();
        preview.SetActive(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(image.TryGetTarget(out _));
        GC.KeepAlive(preview);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DeferredPreview<byte[]> Preview, WeakReference<byte[]> Image) CreateCachedPreview()
    {
        var preview = new DeferredPreview<byte[]>(() => new byte[1024 * 1024], isActive: true);
        return (preview, new WeakReference<byte[]>(preview.Value!));
    }
}
