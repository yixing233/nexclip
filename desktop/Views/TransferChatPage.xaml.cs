using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexClip.Desktop.Services;
using NexClip.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace NexClip.Desktop.Views;

public sealed partial class TransferChatPage : UserControl
{
    public TransferChatViewModel ViewModel => App.Services.ChatVm;

    private ScrollViewer? _chatScroller;

    public TransferChatPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += async (_, _) =>
        {
            await ViewModel.InitializeAsync();
            OnActivated();
        };

        ChatListView.Loaded += (_, _) =>
        {
            AttachScroller();
            ScrollToBottom(instant: true);
        };

        ViewModel.Messages.CollectionChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() => ScrollToBottom(instant: false));
        };
    }

    public async void OnActivated()
    {
        await ViewModel.LoadHistoryMessagesAsync();
        _ = ViewModel.RefreshDevicesAsync();
        FocusInput();
        ScrollToBottom(instant: true);
        _ = ScrollToBottomDelayedAsync();
    }

    private async Task ScrollToBottomDelayedAsync()
    {
        await Task.Delay(50);
        DispatcherQueue.TryEnqueue(() => ScrollToBottom(instant: true));
        await Task.Delay(180);
        DispatcherQueue.TryEnqueue(() => ScrollToBottom(instant: true));
    }

    private void AttachScroller()
    {
        _chatScroller ??= FindChild<ScrollViewer>(ChatListView);
    }

    public void FocusInput()
    {
        MessageTextBox.Focus(FocusState.Programmatic);
    }

    public void ScrollToBottom(bool instant = false)
    {
        if (ViewModel.Messages.Count == 0) return;

        AttachScroller();
        if (_chatScroller is { } scroller)
        {
            scroller.ChangeView(null, double.MaxValue, null, instant);
        }
        try
        {
            ChatListView.ScrollIntoView(ViewModel.Messages[^1]);
        }
        catch { }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T target) return target;
            var sub = FindChild<T>(child);
            if (sub != null) return sub;
        }
        return null;
    }

    private void SelectAllToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSelectAll(true);
        DispatcherQueue.TryEnqueue(() => ScrollToBottom(instant: true));
    }

    private void DevicePill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeviceSelectViewModel dvm)
        {
            ViewModel.OnDevicePillClicked(dvm);
            DispatcherQueue.TryEnqueue(() => ScrollToBottom(instant: true));
        }
    }

    private async void ClearMessages_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Messages.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "清空互传消息",
            Content = "确定要清空即时互传的所有聊天记录吗？此操作仅清空互传消息流，不会影响普通剪贴板历史。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ClearMessages();
            App.Services.Tray?.Notify("NexClip 互传", "已清空互传消息");
        }
    }

    private async void PickImageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.ClipboardWindow!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var bytes = await File.ReadAllBytesAsync(file.Path);
                ViewModel.SetSelectedImage(bytes, file.Path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("选择图片失败", ex);
        }
    }

    private async void MessageTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isCtrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.V && isCtrlDown)
        {
            // 检测剪贴板是否含有图片/截图
            try
            {
                var pngBytes = await ImageCodec.CaptureClipboardPngAsync();
                if (pngBytes is { Length: > 0 })
                {
                    e.Handled = true;
                    var tempFile = Path.Combine(Path.GetTempPath(), $"pasted_chat_{DateTime.UtcNow.Ticks}.png");
                    await File.WriteAllBytesAsync(tempFile, pngBytes);
                    ViewModel.SetSelectedImage(pngBytes, tempFile);
                    App.Services.Tray?.Notify("NexClip 互传", "已附加剪贴板截图，可继续输入描述一并发送");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("粘贴截图失败", ex);
            }
        }

        if (e.Key == VirtualKey.Enter)
        {
            var isAltDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (isAltDown)
            {
                // Alt+Enter 允许强制换行
                var tb = sender as TextBox;
                if (tb != null)
                {
                    e.Handled = true;
                    var start = tb.SelectionStart;
                    tb.Text = tb.Text.Insert(start, Environment.NewLine);
                    tb.SelectionStart = start + Environment.NewLine.Length;
                }
                return;
            }

            // Enter 与 Shift+Enter 均触发即时发送
            e.Handled = true;
            if (!ViewModel.IsSending)
            {
                _ = ViewModel.SendMessageAsync();
            }
        }
    }

    private void MessageBubble_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 记录右键点击的气泡项
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageItem msg)
        {
            if (msg.IsText && !string.IsNullOrEmpty(msg.Text))
            {
                var dp = new DataPackage();
                dp.SetText(msg.Text);
                Clipboard.SetContent(dp);
                App.Services.Tray?.Notify("NexClip", "已复制文本内容");
            }
            else if (msg.IsImage && !string.IsNullOrEmpty(msg.ImagePath) && File.Exists(msg.ImagePath))
            {
                _ = ImageCodec.SetClipboardImageAsync(msg.ImagePath);
                App.Services.Tray?.Notify("NexClip", "已复制图片");
            }
        }
    }

    public event Action<string?, ImageSource?>? ImagePreviewRequested;

    private void ImageBubble_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessageItem msg)
        {
            ImagePreviewRequested?.Invoke(msg.ImagePath, msg.Thumbnail);
        }
    }
}
