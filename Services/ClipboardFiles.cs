using NexClip.Desktop.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace NexClip.Desktop.Services;

/// <summary>
/// 剪贴板文件条目(CF_HDROP / StorageItems)的读取与写回。
///
/// 设计约束(重要):全程只处理路径与大小等元数据,绝不复制、缓存或上传文件内容。
/// 复制的文件往往是几百 MB 级的安装包、视频或工程目录,缓存内容会迅速占满磁盘与内存,
/// 上传则会显著消耗服务器带宽与存储。因此文件条目始终只保存在本地历史中,
/// 由 SyncEngine 在捕获管线里直接短路,不进入任何网络同步分支。
/// </summary>
public static class ClipboardFiles
{
    /// <summary>
    /// 从已取得的剪贴板视图读取文件列表。
    /// 无文件格式、列表为空或全部条目无本地路径(如压缩包内虚拟条目)时返回 null。
    /// </summary>
    public static async Task<IReadOnlyList<ClipboardFileInfo>?> ReadClipboardFilesAsync(DataPackageView content)
    {
        try
        {
            // StorageItems 即 CF_HDROP:Windows 对"复制的文件"的标准表示,只携带引用不携带内容
            if (!content.Contains(StandardDataFormats.StorageItems)) return null;
            var items = await content.GetStorageItemsAsync();
            if (items is null || items.Count == 0) return null;

            var list = new List<ClipboardFileInfo>(Math.Min(items.Count, ClipboardFileMeta.MaxFiles));
            var truncated = false;
            foreach (var item in items)
            {
                if (list.Count >= ClipboardFileMeta.MaxFiles)
                {
                    truncated = true;
                    break;
                }
                var info = await ToFileInfoAsync(item);
                if (info is not null) list.Add(info);
            }
            if (list.Count == 0) return null;
            if (truncated)
            {
                Log.Info($"剪贴板文件数超过上限,仅记录前 {ClipboardFileMeta.MaxFiles} 项(共 {items.Count} 项)");
            }
            return list;
        }
        catch (Exception ex)
        {
            // 剪贴板所有者(远程桌面/虚拟化宿主等)可能拒绝枚举:降级为"无文件",不影响其它类型捕获
            Log.Debug($"读取剪贴板文件列表失败: {ex.Message}");
            return null;
        }
    }

    private static async Task<ClipboardFileInfo?> ToFileInfoAsync(IStorageItem item)
    {
        try
        {
            var path = item.Path;
            // 虚拟条目(压缩包内文件、邮件附件等)没有本地路径,无法在资源管理器中还原,直接跳过
            if (string.IsNullOrEmpty(path)) return null;

            if (item is StorageFolder) return new ClipboardFileInfo(path, item.Name, true, 0);

            var file = (StorageFile)item;
            long size = 0;
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                size = (long)props.Size;
            }
            catch (Exception ex)
            {
                // 单个文件取大小失败(被独占锁定/权限不足)不影响整批记录,大小留 0
                Log.Debug($"读取文件大小失败: {path} ({ex.Message})");
            }
            return new ClipboardFileInfo(path, file.Name, false, size);
        }
        catch (Exception ex)
        {
            Log.Debug($"读取剪贴板文件信息失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把本地文件/文件夹路径写回系统剪贴板(复制语义)。
    /// 写回的是引用而非内容,因此即使条目包含大文件也不会产生额外的磁盘或内存开销。
    /// 已不存在(被移动/删除)的路径会被静默跳过;全部失效时保持剪贴板原内容不变。
    /// </summary>
    public static async Task<bool> WriteClipboardFilesAsync(IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else if (File.Exists(path)) items.Add(await StorageFile.GetFileFromPathAsync(path));
                else Log.Debug($"写剪贴板时跳过已不存在的路径: {path}");
            }
            catch (Exception ex)
            {
                Log.Debug($"写剪贴板时跳过无法访问的路径: {path} ({ex.Message})");
            }
        }
        if (items.Count == 0) return false;

        var pkg = new DataPackage();
        // readOnly: true 表示复制(Ctrl+C)语义;传 false 会被资源管理器识别为剪切(Ctrl+X),
        // 粘贴后原文件消失,属于破坏性行为,绝不能作为默认。
        pkg.SetStorageItems(items, true);
        pkg.Properties[ImageCodec.SelfOriginProperty] = "1";
        ImageCodec.SetContentWithRetry(pkg, flush: true);
        return true;
    }
}
