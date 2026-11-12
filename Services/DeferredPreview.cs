namespace NexClip.Desktop.Services;

/// <summary>在 UI 线程按需创建预览；停用时释放缓存引用，供列表容器回收及托盘空闲使用。</summary>
internal sealed class DeferredPreview<T>(Func<T?> factory, bool isActive = false) where T : class
{
    private bool _isActive = isActive;
    private bool _loaded;
    private T? _value;

    public T? Value
    {
        get
        {
            if (!_isActive) return null;
            if (!_loaded)
            {
                _value = factory();
                _loaded = true;
            }
            return _value;
        }
    }

    public bool SetActive(bool active)
    {
        if (_isActive == active) return false;
        _isActive = active;
        if (!active) Invalidate();
        return true;
    }

    public void Invalidate()
    {
        _value = null;
        _loaded = false;
    }
}
