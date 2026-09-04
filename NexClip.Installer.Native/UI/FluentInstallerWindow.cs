using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using NexClip.Installer.Native.Rendering;
using NexClip.Installer.Native.Services;
using NexClip.Installer.Native.Win32;

namespace NexClip.Installer.Native.UI;

public enum PageState
{
    Welcome,
    Installing,
    Complete,
    Uninstall,
    UninstallComplete
}

public enum DepState
{
    Checking,
    Ready,
    Missing,
    Downloading,
    Installing,
    Failed
}

public class FluentInstallerWindow
{
    private const string InstallerVersion = "20260902.02";
    private const uint AnimationTimerId = 1;

    private IntPtr _hwnd;
    private IntPtr _gdiToken;
    private IntPtr _fontTitle;
    private IntPtr _fontHeader;
    private IntPtr _fontCardTitle;
    private IntPtr _fontBody;
    private IntPtr _fontSmall;
    private IntPtr _fontButton;
    private IntPtr _fontPill;

    private IntPtr _appIconBitmap = IntPtr.Zero;
    private uint _iconW = 0;
    private uint _iconH = 0;

    private float _scale = 1.0f;
    private uint _dpi = 96;

    private PageState _state = PageState.Welcome;
    private string _installDir = "";
    private bool _createDesktopShortcut = true;
    private bool _autoStartup = false;
    private bool _keepUserData = true;
    private bool _launchOnFinish = true;
    private readonly CancellationTokenSource _operationCancellation = new();
    private bool _operationActive;

    private string? _existingVersion = null;
    private bool _isDetecting = true;

    private readonly DepState[] _depStates = new DepState[DependencyService.Dependencies.Count];
    private readonly double[] _depPercent = new double[DependencyService.Dependencies.Count];
    private readonly double[] _displayDepPercent = new double[DependencyService.Dependencies.Count];
    private readonly string[] _depProgress = new string[DependencyService.Dependencies.Count];
    private readonly string[] _depDetailText = new string[DependencyService.Dependencies.Count];
    private readonly GdiPlus.RECTF[] _rectDepBtns = new GdiPlus.RECTF[DependencyService.Dependencies.Count];

    private double _displayProgress = 0.0;
    private double _targetProgress = 0.0;
    private string _statusText = "正在准备环境...";
    private string _subDetailText = "0%";
    private float _activityAngle;
    private long _lastAnimationTick = Environment.TickCount64;
    private bool _animationTimerRunning;
    private bool _restartRequired;
    private bool _detectionPending;
    private bool _dependenciesIncomplete;
    private readonly long _requiredInstallSpaceBytes;

    private float _mouseX = -1;
    private float _mouseY = -1;
    private bool _isLButtonDown = false;

    private GdiPlus.RECTF _rectMinBtn;
    private GdiPlus.RECTF _rectCloseBtn;
    private GdiPlus.RECTF _rectInstallBtn;
    private GdiPlus.RECTF _rectCancelBtn;
    private GdiPlus.RECTF _rectBrowseBtn;
    private GdiPlus.RECTF _rectDesktopCheck;
    private GdiPlus.RECTF _rectStartupCheck;
    private GdiPlus.RECTF _rectLaunchCheck;
    private GdiPlus.RECTF _rectDoneBtn;
    private GdiPlus.RECTF _rectConfirmUninstallBtn;
    private GdiPlus.RECTF _rectCancelUninstallBtn;
    private GdiPlus.RECTF _rectKeepDataCheck;

    private const int BaseWidth = 620;
    private const int BaseHeight = 470;

    private int WindowWidth => (int)(BaseWidth * _scale);
    private int WindowHeight => (int)(BaseHeight * _scale);

    private float S(float v) => v * _scale;

    public FluentInstallerWindow(bool isUninstallMode)
    {
        _state = isUninstallMode ? PageState.Uninstall : PageState.Welcome;

        _installDir = InstallerPathHelper.ResolveInstallDirectory(
            InstallerPathHelper.TryGetRegisteredInstallDirectory(),
            InstallerPathHelper.GetDefaultInstallDirectory());

        var input = new GdiPlus.GdiplusStartupInput { GdiplusVersion = 1 };
        GdiPlus.GdiplusStartup(out _gdiToken, ref input, IntPtr.Zero);

        _dpi = NativeMethods.GetDpiForSystem();
        if (_dpi == 0) _dpi = 96;
        _scale = _dpi / 96.0f;
        if (_scale < 1.0f) _scale = 1.0f;

        InitFonts();
        InitAppIcon();

        _existingVersion = DetectExistingInstalledVersion();
        _requiredInstallSpaceBytes = SetupPolicy.CalculateRequiredSpaceBytes(
            PayloadService.GetExpandedPayloadSizeBytes(),
            0);
        StartDependencyDetection();
    }

    private string? DetectExistingInstalledVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexClip");
            if (key != null)
            {
                var ver = key.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrEmpty(ver)) return ver;
            }

            if (Directory.Exists(_installDir))
            {
                var exe = Path.Combine(_installDir, "NexClip.exe");
                if (File.Exists(exe))
                {
                    var vi = FileVersionInfo.GetVersionInfo(exe);
                    return vi.FileVersion ?? InstallerVersion;
                }
            }
        }
        catch { }
        return null;
    }

    private void StartDependencyDetection()
    {
        _isDetecting = true;
        for (var index = 0; index < _depStates.Length; index++)
        {
            _depStates[index] = DepState.Checking;
            _depDetailText[index] = "检测中...";
            _depPercent[index] = 0.0;
            _displayDepPercent[index] = 0.0;
        }
        EnsureAnimationTimer();
        Invalidate();

        Task.Run(async () =>
        {
            try
            {
                // 并行检测：Appx 枚举可能回退到 PowerShell，串行等待会明显拖慢欢迎页
                var detections = DependencyService.Dependencies
                    .Select((dependency, index) => Task.Run(() =>
                    {
                        try
                        {
                            var installed = DependencyService.IsInstalled(dependency);
                            _depStates[index] = installed ? DepState.Ready : DepState.Missing;
                            _depPercent[index] = installed ? 1.0 : 0.0;
                            _displayDepPercent[index] = _depPercent[index];
                            _depDetailText[index] = installed
                                ? "已就绪"
                                : $"需下载约 {FormatMegabytes(dependency.ExpectedDownloadBytes)}";
                        }
                        catch (Exception exception)
                        {
                            DependencyService.WriteLog($"{dependency.DisplayName} 检测失败：{exception}");
                            _depStates[index] = DepState.Missing;
                            _depDetailText[index] = "检测异常，将尝试安装";
                        }

                        Invalidate();
                    }))
                    .ToArray();

                await Task.WhenAll(detections);
            }
            finally
            {
                _isDetecting = false;
                Invalidate();
                if (!_depStates.Any(state => state is DepState.Downloading or DepState.Installing))
                {
                    StopAnimationTimer();
                }
            }
        });
    }

    private void InitAppIcon()
    {
        try
        {
            var asm = typeof(FluentInstallerWindow).Assembly;
            using var s = asm.GetManifestResourceStream("NexClip.Installer.Native.Resources.app_icon.png");
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                var pStream = GdiPlus.SHCreateMemStream(bytes, (uint)bytes.Length);
                if (pStream != IntPtr.Zero)
                {
                    GdiPlus.GdipCreateBitmapFromStream(pStream, out _appIconBitmap);
                    if (_appIconBitmap != IntPtr.Zero)
                    {
                        GdiPlus.GdipGetImageWidth(_appIconBitmap, out _iconW);
                        GdiPlus.GdipGetImageHeight(_appIconBitmap, out _iconH);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private void InitFonts()
    {
        GdiPlus.GdipCreateFontFamilyFromName("Microsoft YaHei UI", IntPtr.Zero, out var family);
        if (family == IntPtr.Zero)
        {
            GdiPlus.GdipCreateFontFamilyFromName("Segoe UI", IntPtr.Zero, out family);
        }

        GdiPlus.GdipCreateFont(family, 18.5f * _scale, 1, 0, out _fontTitle);
        GdiPlus.GdipCreateFont(family, 15.0f * _scale, 1, 0, out _fontHeader);
        GdiPlus.GdipCreateFont(family, 12.0f * _scale, 1, 0, out _fontCardTitle);
        GdiPlus.GdipCreateFont(family, 11.0f * _scale, 0, 0, out _fontBody);
        GdiPlus.GdipCreateFont(family, 10.5f * _scale, 0, 0, out _fontSmall);
        GdiPlus.GdipCreateFont(family, 11.5f * _scale, 1, 0, out _fontButton);
        GdiPlus.GdipCreateFont(family, 10.0f * _scale, 1, 0, out _fontPill);
    }

    private static string GetDiskFreeSpaceText(string dirPath)
    {
        try
        {
            var root = Path.GetPathRoot(dirPath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    string driveName = root.TrimEnd('\\');
                    double freeBytes = drive.AvailableFreeSpace;
                    if (freeBytes >= 1024L * 1024 * 1024 * 1024)
                    {
                        return $"{(freeBytes / (1024.0 * 1024.0 * 1024.0 * 1024.0)):F1} TB ({driveName})";
                    }
                    if (freeBytes >= 1024L * 1024 * 1024)
                    {
                        return $"{(freeBytes / (1024.0 * 1024.0 * 1024.0)):F1} GB ({driveName})";
                    }
                    return $"{(freeBytes / (1024.0 * 1024.0)):F0} MB ({driveName})";
                }
            }
        }
        catch { }
        return "未知";
    }

    public void Run()
    {
        var hInst = NativeMethods.GetModuleHandleW(null);
        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(WndProc),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInst,
            hIcon = IntPtr.Zero,
            hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = "NexClip.Installer.NativeWindow",
            hIconSm = IntPtr.Zero
        };

        NativeMethods.RegisterClassExW(ref wndClass);

        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (screenW <= 0) screenW = 1920;
        if (screenH <= 0) screenH = 1080;

        int x = (screenW - WindowWidth) / 2;
        int y = (screenH - WindowHeight) / 2;

        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_APPWINDOW,
            "NexClip.Installer.NativeWindow",
            "NexClip 安装向导",
            NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            x, y, WindowWidth, WindowHeight,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        uint winDpi = NativeMethods.GetDpiForWindow(_hwnd);
        if (winDpi > 0 && winDpi != _dpi)
        {
            _dpi = winDpi;
            _scale = _dpi / 96.0f;
            InitFonts();
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, (screenW - WindowWidth) / 2, (screenH - WindowHeight) / 2, WindowWidth, WindowHeight, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int corner = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int backdrop = 2; // Mica
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_hwnd);
        EnsureAnimationTimer();

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        if (_appIconBitmap != IntPtr.Zero)
        {
            GdiPlus.GdipDisposeImage(_appIconBitmap);
        }

        GdiPlus.GdiplusShutdown(_gdiToken);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam)
    {
        switch (uMsg)
        {
            case NativeMethods.WM_PAINT:
                NativeMethods.BeginPaint(hWnd, out var ps);
                PaintDoubleBuffered(ps.hdc);
                NativeMethods.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case NativeMethods.WM_ERASEBKGND:
                return (IntPtr)1;

            case NativeMethods.WM_MOUSEMOVE:
                _mouseX = (short)((uint)lParam & 0xFFFF);
                _mouseY = (short)(((uint)lParam >> 16) & 0xFFFF);
                Invalidate();
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONDOWN:
                _mouseX = (short)((uint)lParam & 0xFFFF);
                _mouseY = (short)(((uint)lParam >> 16) & 0xFFFF);
                _isLButtonDown = true;
                NativeMethods.SetCapture(hWnd);

                if (_mouseY >= 0 && _mouseY <= S(36) && _mouseX < WindowWidth - S(92))
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessageW(hWnd, 0x00A1, (UIntPtr)2, IntPtr.Zero);
                    return IntPtr.Zero;
                }

                HandleClick();
                Invalidate();
                return IntPtr.Zero;

            case NativeMethods.WM_LBUTTONUP:
                _isLButtonDown = false;
                NativeMethods.ReleaseCapture();
                Invalidate();
                return IntPtr.Zero;

            case NativeMethods.WM_DPICHANGED:
                _dpi = (uint)(wParam.ToUInt32() & 0xFFFF);
                _scale = _dpi / 96.0f;
                InitFonts();
                NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, WindowWidth, WindowHeight, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                Invalidate();
                return IntPtr.Zero;

            case NativeMethods.WM_TIMER:
                if (wParam.ToUInt32() == AnimationTimerId)
                {
                    OnAnimationTick();
                }
                return IntPtr.Zero;

            case NativeMethods.WM_CLOSE:
                if (_operationActive)
                {
                    NativeMethods.MessageBoxW(_hwnd, "当前操作正在进行，请等待完成后再关闭安装器。", "NexClip 安装向导", 0x30);
                    return IntPtr.Zero;
                }

                // 依赖下载可能长时间运行，关闭窗口时主动取消以避免后台进程残留
                if (_depStates.Any(state => state is DepState.Downloading or DepState.Installing))
                {
                    const uint YesNoWarning = 0x04 | 0x30;
                    if (NativeMethods.MessageBoxW(
                            _hwnd,
                            "运行环境组件正在下载或安装，确定要取消并退出吗？",
                            "NexClip 安装向导",
                            YesNoWarning) != 6)
                    {
                        return IntPtr.Zero;
                    }

                    try { _operationCancellation.Cancel(); } catch { }
                }

                return NativeMethods.DefWindowProcW(hWnd, uMsg, wParam, lParam);

            case NativeMethods.WM_DESTROY:
                NativeMethods.KillTimer(hWnd, (UIntPtr)AnimationTimerId);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    private void OnAnimationTick()
    {
        var now = Environment.TickCount64;
        var elapsedSeconds = Math.Clamp((now - _lastAnimationTick) / 1000.0, 0.001, 0.1);
        _lastAnimationTick = now;
        var needsRedraw = false;

        if (_isDetecting || _depStates.Any(state => state is DepState.Downloading or DepState.Installing))
        {
            _activityAngle = (_activityAngle + (float)(elapsedSeconds * 240.0)) % 360.0f;
            needsRedraw = true;
        }

        for (var index = 0; index < _displayDepPercent.Length; index++)
        {
            var next = SetupPolicy.AnimateTowards(
                _displayDepPercent[index],
                _depPercent[index],
                elapsedSeconds,
                response: 18.0);
            if (Math.Abs(next - _displayDepPercent[index]) > 0.00001)
            {
                _displayDepPercent[index] = next;
                _depProgress[index] = $"{(int)(next * 100)}%";
                needsRedraw = true;
            }
        }

        if (_state == PageState.Installing)
        {
            var next = SetupPolicy.AnimateTowards(
                _displayProgress,
                _targetProgress,
                elapsedSeconds);
            if (Math.Abs(next - _displayProgress) > 0.00001)
            {
                _displayProgress = next;
                _subDetailText = $"{(int)(_displayProgress * 100)}%";
                needsRedraw = true;
            }
        }

        if (needsRedraw)
        {
            Invalidate();
        }
    }

    private void EnsureAnimationTimer()
    {
        if (_hwnd == IntPtr.Zero || _animationTimerRunning)
        {
            return;
        }

        _lastAnimationTick = Environment.TickCount64;
        NativeMethods.SetTimer(_hwnd, (UIntPtr)AnimationTimerId, 16, IntPtr.Zero);
        _animationTimerRunning = true;
    }

    private void StopAnimationTimer()
    {
        if (_hwnd == IntPtr.Zero || !_animationTimerRunning)
        {
            return;
        }

        NativeMethods.KillTimer(_hwnd, (UIntPtr)AnimationTimerId);
        _animationTimerRunning = false;
    }

    private void Invalidate()
    {
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void PaintDoubleBuffered(IntPtr hdc)
    {
        var memDC = GdiPlus.CreateCompatibleDC(hdc);
        var memBitmap = GdiPlus.CreateCompatibleBitmap(hdc, WindowWidth, WindowHeight);
        var oldBmp = GdiPlus.SelectObject(memDC, memBitmap);

        GdiPlus.GdipCreateFromHDC(memDC, out var graphics);
        GdiPlus.GdipSetSmoothingMode(graphics, GdiPlus.SmoothingMode.AntiAlias);
        GdiPlus.GdipSetInterpolationMode(graphics, 7 /* HighQualityBicubic */);
        GdiPlus.GdipSetPixelOffsetMode(graphics, 2 /* HighQuality */);
        GdiPlus.GdipSetTextRenderingHint(graphics, GdiPlus.TextRenderingHint.ClearTypeGridFit);

        Render(graphics);

        GdiPlus.GdipDeleteGraphics(graphics);

        GdiPlus.BitBlt(hdc, 0, 0, WindowWidth, WindowHeight, memDC, 0, 0, 0x00CC0020);

        GdiPlus.SelectObject(memDC, oldBmp);
        GdiPlus.DeleteObject(memBitmap);
        GdiPlus.DeleteDC(memDC);
    }
    private void HandleClick()
    {
        if (_rectCloseBtn.Contains(_mouseX, _mouseY))
        {
            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
            return;
        }

        if (_rectMinBtn.Contains(_mouseX, _mouseY))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_MINIMIZE);
            return;
        }

        if (_state == PageState.Welcome)
        {
            // 单独点击某一项的下载安装按钮
            for (int i = 0; i < _rectDepBtns.Length; i++)
            {
                if (_rectDepBtns[i].Contains(_mouseX, _mouseY))
                {
                    if (_depStates[i] == DepState.Missing || _depStates[i] == DepState.Failed)
                    {
                        InstallSingleDependency(i);
                        return;
                    }
                }
            }

            if (_rectInstallBtn.Contains(_mouseX, _mouseY) && CanStartInstallation())
            {
                StartInstallation();
                return;
            }

            if (_rectCancelBtn.Contains(_mouseX, _mouseY))
            {
                NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
                return;
            }

            if (_rectBrowseBtn.Contains(_mouseX, _mouseY))
            {
                Task.Run(() =>
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            Arguments = "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.FolderBrowserDialog; $f.SelectedPath = '" + _installDir + "'; if ($f.ShowDialog() -eq 'OK') { Write-Output $f.SelectedPath }\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        var path = proc?.StandardOutput.ReadToEnd().Trim();
                        proc?.WaitForExit();
                        if (!string.IsNullOrEmpty(path))
                        {
                            _installDir = path;
                            Invalidate();
                        }
                    }
                    catch { }
                });
                return;
            }

            if (_rectDesktopCheck.Contains(_mouseX, _mouseY))
            {
                _createDesktopShortcut = !_createDesktopShortcut;
                Invalidate();
                return;
            }

            if (_rectStartupCheck.Contains(_mouseX, _mouseY))
            {
                _autoStartup = !_autoStartup;
                Invalidate();
                return;
            }
        }
        else if (_state == PageState.Complete)
        {
            if (_rectLaunchCheck.Contains(_mouseX, _mouseY))
            {
                _launchOnFinish = !_launchOnFinish;
                Invalidate();
                return;
            }

            if (_rectDoneBtn.Contains(_mouseX, _mouseY))
            {
                // 依赖未就绪时启动主程序只会立即崩溃，此处直接跳过启动
                if (_launchOnFinish && !_restartRequired && !_detectionPending && !_dependenciesIncomplete)
                {
                    ProcessHelper.LaunchApp(_installDir);
                }
                NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
            }
        }
        else if (_state == PageState.Uninstall)
        {
            if (_rectKeepDataCheck.Contains(_mouseX, _mouseY))
            {
                _keepUserData = !_keepUserData;
                Invalidate();
                return;
            }

            if (_rectConfirmUninstallBtn.Contains(_mouseX, _mouseY))
            {
                StartUninstallation();
            }
            else if (_rectCancelUninstallBtn.Contains(_mouseX, _mouseY))
            {
                NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
            }
        }
        else if (_state == PageState.UninstallComplete)
        {
            if (_rectDoneBtn.Contains(_mouseX, _mouseY))
            {
                NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_CLOSE, UIntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private void InstallSingleDependency(int index)
    {
        if (_depStates[index] == DepState.Ready ||
            _depStates.Any(state => state is DepState.Downloading or DepState.Installing))
            return;

        EnsureAnimationTimer();
        Task.Run(async () =>
        {
            var dependency = DependencyService.Dependencies[index];
            try
            {
                _depStates[index] = DepState.Downloading;
                _depPercent[index] = 0.0;
                _displayDepPercent[index] = 0.0;
                _depProgress[index] = "0%";
                _depDetailText[index] = "正在连接...";
                Invalidate();

                var result = await DependencyService.InstallDependencyAsync(
                    dependency,
                    report => ApplyDependencyReport(index, report),
                    _operationCancellation.Token);

                _restartRequired |= result.RestartRequired;
                _detectionPending |= !result.DetectionConfirmed;
                _depPercent[index] = 1.0;
                _depStates[index] = DepState.Ready;
                _depDetailText[index] = !result.DetectionConfirmed
                    ? "已安装，重启后生效"
                    : result.RestartRequired ? "已就绪，需重启" : "已就绪";
                Invalidate();
            }
            catch (OperationCanceledException)
            {
                _depStates[index] = DepState.Missing;
                _depDetailText[index] = "已取消";
                Invalidate();
            }
            catch (Exception exception)
            {
                _depStates[index] = DepState.Failed;
                _depDetailText[index] = TruncateDetail(exception.Message);
                Invalidate();
                ReportDependencyFailure(dependency, exception);
            }
            finally
            {
                if (!_isDetecting && !_depStates.Any(state => state is DepState.Downloading or DepState.Installing))
                {
                    StopAnimationTimer();
                }
            }
        });
    }

    private void ApplyDependencyReport(int index, DependencyProgress report)
    {
        _depPercent[index] = Math.Clamp(report.Progress, 0.0, 1.0);
        _depProgress[index] = $"{(int)(_depPercent[index] * 100)}%";
        _depStates[index] = report.Stage switch
        {
            DependencyInstallStage.Downloading => DepState.Downloading,
            DependencyInstallStage.Completed => DepState.Ready,
            _ => DepState.Installing
        };
        _depDetailText[index] = TruncateDetail(report.Detail);
        Invalidate();
    }

    private static string TruncateDetail(string detail)
    {
        detail = detail.Replace("\r", " ").Replace("\n", " ").Trim();
        return detail.Length > 60 ? detail[..57] + "..." : detail;
    }

    private void ReportDependencyFailure(DependencyDefinition dependency, Exception exception)
    {
        DependencyService.WriteLog($"{dependency.DisplayName} 配置失败（UI 上报）：{exception}");
        NativeMethods.MessageBoxW(
            _hwnd,
            $"{dependency.DisplayName} 自动配置失败：\n{exception.Message}\n\n" +
            $"可稍后点击“重试”，或手动下载安装：\n{dependency.ManualDownloadPage}\n\n" +
            $"诊断日志：\n{DependencyService.LogFilePath}",
            "NexClip 运行环境配置失败",
            0x10);
    }

    private bool CanStartInstallation() =>
        !_isDetecting &&
        !_depStates.Any(state => state is DepState.Downloading or DepState.Installing);

    private void EnsureSufficientDiskSpace()
    {
        PayloadService.ValidateEmbeddedPayload();
        if (SetupPolicy.TryGetAvailableDiskSpace(_installDir, out var targetAvailable) &&
            !SetupPolicy.HasSufficientSpace(targetAvailable, _requiredInstallSpaceBytes))
        {
            throw new IOException(
                $"目标磁盘空间不足，至少需要 {FormatMegabytes(_requiredInstallSpaceBytes)} 可用空间。");
        }

        // 依赖下载临时空间按真实包体估算（含 25% 余量），避免用上限值误判空间不足
        var dependencyBytes = PendingDependencies
            .Sum(dependency => dependency.ExpectedDownloadBytes + dependency.ExpectedDownloadBytes / 4);
        if (dependencyBytes <= 0)
        {
            return;
        }

        var temporaryRequired = SetupPolicy.CalculateRequiredSpaceBytes(0, dependencyBytes);
        if (SetupPolicy.TryGetAvailableDiskSpace(DependencyService.DownloadCacheDirectory, out var temporaryAvailable) &&
            !SetupPolicy.HasSufficientSpace(temporaryAvailable, temporaryRequired))
        {
            throw new IOException(
                $"临时文件磁盘空间不足，运行环境配置至少需要 {FormatMegabytes(temporaryRequired)} 可用空间。");
        }
    }

    /// <summary>欢迎页检测结果中尚未就绪的依赖项，避免重复触发昂贵的系统枚举。</summary>
    private IEnumerable<DependencyDefinition> PendingDependencies =>
        DependencyService.Dependencies.Where((_, index) => _depStates[index] != DepState.Ready);

    private static string FormatMegabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):F0} MB";

    private void StartInstallation()
    {
        try
        {
            EnsureSufficientDiskSpace();
        }
        catch (Exception exception)
        {
            NativeMethods.MessageBoxW(_hwnd, exception.Message, "NexClip 无法开始安装", 0x30);
            return;
        }

        _state = PageState.Installing;
        _operationActive = true;
        _displayProgress = 0.0;
        _targetProgress = 0.05;
        _statusText = "正在检查系统运行环境...";
        _subDetailText = "0%";
        Invalidate();

        // 启动 60fps 缓动动画定时器
        EnsureAnimationTimer();

        Task.Run(async () =>
        {
            try
            {
                // 1. 运行环境配置 (0% -> 25%)：失败时允许用户选择继续部署主程序并稍后手动补装依赖
                if (!await ConfigureDependenciesAsync())
                {
                    return;
                }

                // 2. 进程检查与释放 (25% -> 32%)
                _targetProgress = 0.26;
                _statusText = "正在检查并释放后台运行中的旧版本进程...";
                await ProcessHelper.TerminateRunningInstancesAsync(_operationCancellation.Token);
                
                _targetProgress = 0.32;
                _statusText = "正在准备安装目录与工作区...";
                await Task.Delay(150);

                // 3. 解压核心组件 (32% -> 88%)
                await PayloadService.InstallPayloadWithRollbackAsync(_installDir, (p, fileName) =>
                {
                    _targetProgress = 0.32 + Math.Clamp(p, 0.0, 1.0) * 0.56;
                    _statusText = $"正在释放: {fileName}";
                }, _operationCancellation.Token);

                // 4. 部署卸载与快捷方式 (88% -> 98%)
                _targetProgress = 0.90;
                _statusText = "正在配置应用程序与卸载服务...";
                if (!PayloadService.DeployUninstaller(_installDir))
                {
                    throw new IOException("无法部署卸载程序，安装已中止。");
                }
                await Task.Delay(100);

                _targetProgress = 0.94;
                _statusText = "正在配置桌面与开始菜单快捷方式...";
                ShortcutHelper.CreateStartMenuShortcut(_installDir);
                if (_createDesktopShortcut) ShortcutHelper.CreateDesktopShortcut(_installDir);
                if (_autoStartup) ShortcutHelper.SetStartupShortcut(_installDir, true);
                await Task.Delay(100);

                _targetProgress = 0.98;
                _statusText = "正在注册系统卸载信息...";
                RegistryHelper.RegisterUninstall(_installDir, InstallerVersion);
                await Task.Delay(100);

                // 5. 完成过渡 (98% -> 100%)
                _targetProgress = 1.0;
                _statusText = "安装完成，正在加载就绪...";

                // 等待视觉平滑进度条平滑滑动到 100%
                var waitTicks = Environment.TickCount64;
                while (_displayProgress < 0.99 && Environment.TickCount64 - waitTicks < 2000)
                {
                    await Task.Delay(20);
                }
                _displayProgress = 1.0;
                _subDetailText = "100%";
                Invalidate();
                await Task.Delay(350);

                StopAnimationTimer();
                _state = PageState.Complete;
                Invalidate();
            }
            catch (OperationCanceledException)
            {
                StopAnimationTimer();
                _statusText = "安装已取消";
                _state = PageState.Welcome;
                Invalidate();
            }
            catch (Exception ex)
            {
                StopAnimationTimer();
                var logPath = Path.Combine(Path.GetTempPath(), "nexclip_install_error.log");
                try { File.WriteAllText(logPath, ex.ToString()); } catch { }

                _statusText = $"安装遇到错误: {ex.Message}";
                _state = PageState.Welcome;
                Invalidate();

                NativeMethods.MessageBoxW(_hwnd, $"安装过程中遇到错误:\n{ex.Message}\n\n详细信息已记录至:\n{logPath}", "NexClip 安装失败", 0x10);
            }
            finally
            {
                _operationActive = false;
            }
        });
    }

    /// <summary>
    /// 配置运行环境依赖。返回 false 表示用户选择中止安装并回到欢迎页。
    /// </summary>
    private async Task<bool> ConfigureDependenciesAsync()
    {
        try
        {
            var result = await DependencyService.EnsureDependenciesAsync(
                (progress, text) =>
                {
                    _targetProgress = 0.05 + Math.Clamp(progress, 0.0, 1.0) * 0.20;
                    _statusText = TruncateDetail(text);
                },
                _operationCancellation.Token);
            _restartRequired |= result.RestartRequired;
            _detectionPending |= !result.DetectionConfirmed;
            RefreshDependencyStates();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DependencyService.WriteLog($"依赖配置失败：{exception}");
            RefreshDependencyStates();
            MarkFailedDependencies(exception);

            var manualList = string.Join(
                Environment.NewLine,
                PendingDependencies.Select(dependency =>
                    $"· {dependency.DisplayName}：{dependency.ManualDownloadPage}"));
            const uint YesNoWarning = 0x04 | 0x30;
            var choice = NativeMethods.MessageBoxW(
                _hwnd,
                $"运行环境依赖自动配置失败：\n{exception.Message}\n\n" +
                $"仍要继续安装 NexClip 主程序吗？\n（选择“是”后需手动安装以下组件，NexClip 才能启动）\n\n{manualList}\n\n" +
                $"诊断日志：\n{DependencyService.LogFilePath}",
                "NexClip 运行环境配置失败",
                YesNoWarning);
            if (choice != 6)
            {
                StopAnimationTimer();
                _statusText = "运行环境依赖未就绪，安装已中止";
                _state = PageState.Welcome;
                Invalidate();
                return false;
            }

            _dependenciesIncomplete = true;
            return true;
        }
    }

    /// <summary>把批量配置中真正失败的组件标记为 Failed，其余未就绪项保持 Missing 以便单独重试。</summary>
    private void MarkFailedDependencies(Exception exception)
    {
        if (exception is not DependencyConfigurationException configurationFailure)
        {
            return;
        }

        for (var index = 0; index < DependencyService.Dependencies.Count; index++)
        {
            var dependency = DependencyService.Dependencies[index];
            if (_depStates[index] != DepState.Ready &&
                configurationFailure.FailedDependencies.Any(failed => failed.Kind == dependency.Kind))
            {
                _depStates[index] = DepState.Failed;
                _depDetailText[index] = "自动配置失败，可重试";
            }
        }

        Invalidate();
    }

    private void RefreshDependencyStates()
    {
        for (var index = 0; index < DependencyService.Dependencies.Count; index++)
        {
            var installed = DependencyService.IsInstalled(DependencyService.Dependencies[index]);
            if (installed)
            {
                _depStates[index] = DepState.Ready;
                _depPercent[index] = 1.0;
                _depDetailText[index] = "已就绪";
            }
            else if (_depStates[index] != DepState.Failed)
            {
                _depStates[index] = DepState.Missing;
            }
        }

        Invalidate();
    }

    private void StartUninstallation()
    {
        _state = PageState.Installing;
        _operationActive = true;
        _displayProgress = 0.0;
        _targetProgress = 0.15;
        _statusText = "正在安全清理 NexClip 组件...";
        _subDetailText = "0%";
        Invalidate();

        EnsureAnimationTimer();

        Task.Run(async () =>
        {
            try
            {
                _targetProgress = 0.30;
                _statusText = "正在结束后台进程...";
                await ProcessHelper.TerminateRunningInstancesAsync(_operationCancellation.Token);

                _targetProgress = 0.60;
                _statusText = "正在清理系统快捷方式与注册表...";
                ShortcutHelper.RemoveAllShortcuts();
                RegistryHelper.UnregisterUninstall();

                if (!_keepUserData)
                {
                    foreach (var appData in InstallerPathHelper.GetUserDataDirectories(
                                 InstallerPathHelper.TryGetConfiguredStorageDirectory()))
                    {
                        try { if (Directory.Exists(appData)) Directory.Delete(appData, true); } catch { }
                    }
                }

                _targetProgress = 0.85;
                _statusText = "正在移除安装目录文件...";
                ProcessHelper.ScheduleDirectoryDeletion(_installDir);

                _targetProgress = 1.0;
                _statusText = "卸载已完成";

                var waitTicks = Environment.TickCount64;
                while (_displayProgress < 0.99 && Environment.TickCount64 - waitTicks < 2000)
                {
                    await Task.Delay(20);
                }
                _displayProgress = 1.0;
                _subDetailText = "100%";
                Invalidate();
                await Task.Delay(350);

                StopAnimationTimer();
                _state = PageState.UninstallComplete;
                Invalidate();
            }
            catch (Exception ex)
            {
                StopAnimationTimer();
                _statusText = $"卸载遇到错误: {ex.Message}";
                Invalidate();
            }
            finally
            {
                _operationActive = false;
            }
        });
    }
    private void Render(IntPtr g)
    {
        GdiPlus.GdipGraphicsClear(g, GdiPlus.FromHex("#121214"));

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#27272A"), 1.0f, 0, out var borderPen);
        LucideGdiPlus.DrawRoundedRect(g, borderPen, 0.5f, 0.5f, WindowWidth - 1.0f, WindowHeight - 1.0f, S(12.0f));
        GdiPlus.GdipDeletePen(borderPen);

        RenderTitleBar(g);

        switch (_state)
        {
            case PageState.Welcome:
                RenderWelcomePage(g);
                break;
            case PageState.Installing:
                RenderInstallingPage(g);
                break;
            case PageState.Complete:
                RenderCompletePage(g);
                break;
            case PageState.Uninstall:
                RenderUninstallPage(g);
                break;
            case PageState.UninstallComplete:
                RenderUninstallCompletePage(g);
                break;
        }
    }

    private void RenderTitleBar(IntPtr g)
    {
        float topBarH = S(36.0f);

        float iconSize = S(16.0f);
        float iconX = S(14.0f);
        float iconY = (topBarH - iconSize) / 2.0f;

        if (_appIconBitmap != IntPtr.Zero)
        {
            GdiPlus.GdipDrawImageRectRect(
                g, _appIconBitmap,
                iconX, iconY, iconSize, iconSize,
                0, 0, _iconW, _iconH,
                2 /* UnitPixel */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Clipboard, iconX, iconY, iconSize, GdiPlus.FromHex("#006EFF"), 2.0f);
        }

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#CBD5E1"), out var titleBrush);
        var titleRect = new GdiPlus.RECTF(iconX + iconSize + S(8.0f), 0, S(300), topBarH);
        GdiPlus.GdipCreateStringFormat(0, 0, out var titleFmt);
        GdiPlus.GdipSetStringFormatLineAlign(titleFmt, GdiPlus.StringAlignment.Center);
        string winTitle = (_state == PageState.Uninstall || _state == PageState.UninstallComplete) ? "NexClip 卸载向导" : "NexClip 安装向导";
        GdiPlus.GdipDrawString(g, winTitle, winTitle.Length, _fontSmall, ref titleRect, titleFmt, titleBrush);
        GdiPlus.GdipDeleteBrush(titleBrush);
        GdiPlus.GdipDeleteStringFormat(titleFmt);

        float btnW = S(46.0f);
        float btnH = topBarH;

        _rectCloseBtn = new GdiPlus.RECTF(WindowWidth - btnW, 0, btnW, btnH);
        _rectMinBtn = new GdiPlus.RECTF(WindowWidth - btnW * 2, 0, btnW, btnH);

        bool isHoverMin = _rectMinBtn.Contains(_mouseX, _mouseY);
        bool isHoverClose = _rectCloseBtn.Contains(_mouseX, _mouseY);

        if (isHoverMin)
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#2A2A30"), out var hBrush);
            GdiPlus.GdipFillRectangle(g, hBrush, _rectMinBtn.X, _rectMinBtn.Y, _rectMinBtn.Width, _rectMinBtn.Height);
            GdiPlus.GdipDeleteBrush(hBrush);
        }

        if (isHoverClose)
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#E81123"), out var hCloseBrush);
            LucideGdiPlus.FillTopRightRoundedRect(g, hCloseBrush, _rectCloseBtn.X, _rectCloseBtn.Y, _rectCloseBtn.Width, _rectCloseBtn.Height, S(12.0f));
            GdiPlus.GdipDeleteBrush(hCloseBrush);
        }

        float capIconSize = S(15.0f);
        uint minColor = isHoverMin ? GdiPlus.FromHex("#FFFFFF") : GdiPlus.FromHex("#CBD5E1");
        uint closeColor = isHoverClose ? GdiPlus.FromHex("#FFFFFF") : GdiPlus.FromHex("#CBD5E1");

        float minIconX = _rectMinBtn.X + (_rectMinBtn.Width - capIconSize) / 2.0f;
        float minIconY = _rectMinBtn.Y + (_rectMinBtn.Height - capIconSize) / 2.0f;
        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Minus, minIconX, minIconY, capIconSize, minColor, 2.2f);

        float closeIconX = _rectCloseBtn.X + (_rectCloseBtn.Width - capIconSize) / 2.0f;
        float closeIconY = _rectCloseBtn.Y + (_rectCloseBtn.Height - capIconSize) / 2.0f;
        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.X, closeIconX, closeIconY, capIconSize, closeColor, 2.2f);
    }

    private void RenderWelcomePage(IntPtr g)
    {
        float startX = S(32.0f);
        float contentW = WindowWidth - startX * 2;

        // 1. 顶部 Header 区 (Logo + 标题 + 版本 + 描述)
        float headerY = S(48.0f);
        float logoBoxSize = S(50.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#1C1C21"), out var cardBgBrush);
        LucideGdiPlus.FillRoundedRect(g, cardBgBrush, startX, headerY, logoBoxSize, logoBoxSize, S(12.0f));
        GdiPlus.GdipDeleteBrush(cardBgBrush);

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#2E2E36"), 1.0f, 0, out var cardStrokePen);
        LucideGdiPlus.DrawRoundedRect(g, cardStrokePen, startX, headerY, logoBoxSize, logoBoxSize, S(12.0f));
        GdiPlus.GdipDeletePen(cardStrokePen);

        if (_appIconBitmap != IntPtr.Zero)
        {
            float appDrawSize = S(36.0f);
            float appX = startX + (logoBoxSize - appDrawSize) / 2.0f;
            float appY = headerY + (logoBoxSize - appDrawSize) / 2.0f;

            GdiPlus.GdipDrawImageRectRect(
                g, _appIconBitmap,
                appX, appY, appDrawSize, appDrawSize,
                0, 0, _iconW, _iconH,
                2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Clipboard, startX + S(12), headerY + S(12), S(26), GdiPlus.FromHex("#006EFF"), 2.2f);
        }

        string brand = "NexClip 剪贴板管理";
        string verStr = $"v{InstallerVersion}";

        var dummyLayout = new GdiPlus.RECTF(0, 0, S(600), S(100));
        GdiPlus.GdipMeasureString(g, brand, brand.Length, _fontTitle, ref dummyLayout, IntPtr.Zero, out var brandBox, out _, out _);
        GdiPlus.GdipMeasureString(g, verStr, verStr.Length, _fontPill, ref dummyLayout, IntPtr.Zero, out var verBox, out _, out _);

        float textStartX = startX + logoBoxSize + S(14.0f);
        float titleTextY = headerY + S(1.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var titleTextBrush);
        var brandRect = new GdiPlus.RECTF(textStartX, titleTextY, brandBox.Width + S(4), brandBox.Height);
        GdiPlus.GdipDrawString(g, brand, brand.Length, _fontTitle, ref brandRect, IntPtr.Zero, titleTextBrush);
        GdiPlus.GdipDeleteBrush(titleTextBrush);

        float pillPadX = S(8.0f);
        float pillW = verBox.Width + pillPadX * 2;
        float pillH = S(20.0f);
        float pillX = textStartX + brandBox.Width + S(8.0f);
        float pillY = titleTextY + (brandBox.Height - pillH) / 2.0f;

        GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(40, 245, 158, 11), out var pillBgBrush);
        LucideGdiPlus.FillRoundedRect(g, pillBgBrush, pillX, pillY, pillW, pillH, S(4.0f));
        GdiPlus.GdipDeleteBrush(pillBgBrush);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F59E0B"), out var pillTextBrush);
        GdiPlus.GdipCreateStringFormat(0, 0, out var centerFmt);
        GdiPlus.GdipSetStringFormatAlign(centerFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipSetStringFormatLineAlign(centerFmt, GdiPlus.StringAlignment.Center);
        var verRect = new GdiPlus.RECTF(pillX, pillY, pillW, pillH);
        GdiPlus.GdipDrawString(g, verStr, verStr.Length, _fontPill, ref verRect, centerFmt, pillTextBrush);
        GdiPlus.GdipDeleteBrush(pillTextBrush);
        GdiPlus.GdipDeleteStringFormat(centerFmt);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#A1A1AA"), out var descBrush);
        var descRect = new GdiPlus.RECTF(textStartX, titleTextY + brandBox.Height + S(5.0f), contentW - logoBoxSize - S(14), S(20));
        string descStr = !string.IsNullOrEmpty(_existingVersion)
            ? $"检测到已安装版本 v{_existingVersion}，将为您无缝覆盖升级至最新版"
            : "现代化跨平台剪贴板管理与同步工具";
        GdiPlus.GdipDrawString(g, descStr, descStr.Length, _fontBody, ref descRect, IntPtr.Zero, descBrush);
        GdiPlus.GdipDeleteBrush(descBrush);

        // 2. 运行环境智能检测卡片
        float cardY = S(112.0f);
        float cardH = S(146.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#19191D"), out var dCardBg);
        LucideGdiPlus.FillRoundedRect(g, dCardBg, startX, cardY, contentW, cardH, S(10.0f));
        GdiPlus.GdipDeleteBrush(dCardBg);

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#2A2A30"), 1.0f, 0, out var dCardPen);
        LucideGdiPlus.DrawRoundedRect(g, dCardPen, startX, cardY, contentW, cardH, S(10.0f));
        GdiPlus.GdipDeletePen(dCardPen);

        float shieldSize = S(18.0f);
        float shieldY = cardY + S(12.0f);
        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Shield, startX + S(16), shieldY, shieldSize, GdiPlus.FromHex("#F59E0B"), 2.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var cardTitleBrush);
        var cardTitleRect = new GdiPlus.RECTF(startX + S(40), cardY + S(10), S(220), S(22));
        GdiPlus.GdipCreateStringFormat(0, 0, out var cTitleFmt);
        GdiPlus.GdipSetStringFormatLineAlign(cTitleFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipDrawString(g, "运行环境智能检测", 8, _fontCardTitle, ref cardTitleRect, cTitleFmt, cardTitleBrush);
        GdiPlus.GdipDeleteBrush(cardTitleBrush);
        GdiPlus.GdipDeleteStringFormat(cTitleFmt);

        // 渲染各环境检测项 (右侧支持独立下载/实时速度/安装/重试按钮交互)
        for (var depIndex = 0; depIndex < _depStates.Length; depIndex++)
        {
            RenderDepItemRow(
                g,
                depIndex,
                startX + S(16),
                cardY + S(42) + depIndex * S(32),
                contentW - S(32),
                S(28),
                DependencyService.Dependencies[depIndex].DisplayName,
                _depStates[depIndex]);
        }

        // 3. 安装路径选择区
        float pathAreaY = S(272.0f);
        float inputH = S(36.0f);

        float folderSize = S(18.0f);
        float folderY = pathAreaY + (inputH - folderSize) / 2.0f;
        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Folder, startX, folderY, folderSize, GdiPlus.FromHex("#94A3B8"), 2.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#CBD5E1"), out var pLabelBrush);
        var pLblRect = new GdiPlus.RECTF(startX + S(24), pathAreaY, S(76), inputH);
        GdiPlus.GdipCreateStringFormat(0, 0, out var pLblFmt);
        GdiPlus.GdipSetStringFormatLineAlign(pLblFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipDrawString(g, "安装路径:", 5, _fontBody, ref pLblRect, pLblFmt, pLabelBrush);
        GdiPlus.GdipDeleteBrush(pLabelBrush);
        GdiPlus.GdipDeleteStringFormat(pLblFmt);

        float inputX = startX + S(96.0f);
        float inputW = contentW - S(96.0f) - S(88.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#202025"), out var ibBg);
        LucideGdiPlus.FillRoundedRect(g, ibBg, inputX, pathAreaY, inputW, inputH, S(6.0f));
        GdiPlus.GdipDeleteBrush(ibBg);

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#2E2E36"), 1.0f, 0, out var ibPen);
        LucideGdiPlus.DrawRoundedRect(g, ibPen, inputX, pathAreaY, inputW, inputH, S(6.0f));
        GdiPlus.GdipDeletePen(ibPen);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var pathTxtBrush);
        var pTxtRect = new GdiPlus.RECTF(inputX + S(12), pathAreaY, inputW - S(24), inputH);
        GdiPlus.GdipCreateStringFormat(0, 0, out var pTxtFmt);
        GdiPlus.GdipSetStringFormatLineAlign(pTxtFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipDrawString(g, _installDir, _installDir.Length, _fontBody, ref pTxtRect, pTxtFmt, pathTxtBrush);
        GdiPlus.GdipDeleteBrush(pathTxtBrush);
        GdiPlus.GdipDeleteStringFormat(pTxtFmt);

        _rectBrowseBtn = new GdiPlus.RECTF(startX + contentW - S(80), pathAreaY, S(80), inputH);
        RenderButton(g, _rectBrowseBtn, "浏览...", null, isPrimary: false);

        // 4. 磁盘空间信息行
        float spaceY = S(320.0f);
        float diskIconSize = S(16.0f);
        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.HardDrive, startX, spaceY + S(2), diskIconSize, GdiPlus.FromHex("#71717A"), 2.0f);

        string freeSpace = GetDiskFreeSpaceText(_installDir);
        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#94A3B8"), out var spaceBrush);
        var spaceRect = new GdiPlus.RECTF(startX + S(24), spaceY, contentW - S(24), S(20));
        string spaceText = $"所需空间: 约 {FormatMegabytes(_requiredInstallSpaceBytes)}       可用空间: {freeSpace}";
        GdiPlus.GdipDrawString(g, spaceText, spaceText.Length, _fontSmall, ref spaceRect, IntPtr.Zero, spaceBrush);
        GdiPlus.GdipDeleteBrush(spaceBrush);

        // 5. 选项复选框
        float checkY = S(352.0f);
        _rectDesktopCheck = new GdiPlus.RECTF(startX, checkY, S(190), S(26));
        RenderCheckBox(g, _rectDesktopCheck, "创建桌面快捷方式", _createDesktopShortcut);

        _rectStartupCheck = new GdiPlus.RECTF(startX + S(210), checkY, S(190), S(26));
        RenderCheckBox(g, _rectStartupCheck, "开机自动启动", _autoStartup);

        // 6. 底部操作按钮行 (取消 + 一键安装)
        float bottomBtnY = S(404.0f);
        float actionBtnH = S(42.0f);

        _rectCancelBtn = new GdiPlus.RECTF(WindowWidth - startX - S(150) - S(12) - S(96), bottomBtnY, S(96), actionBtnH);
        RenderButton(g, _rectCancelBtn, "取消", null, isPrimary: false);

        _rectInstallBtn = new GdiPlus.RECTF(WindowWidth - startX - S(150), bottomBtnY, S(150), actionBtnH);
        if (_isDetecting)
        {
            RenderButton(g, _rectInstallBtn, "检查环境中...", null, isPrimary: false, isDisabled: true);
        }
        else if (!CanStartInstallation())
        {
            RenderButton(g, _rectInstallBtn, "配置环境中...", null, isPrimary: false, isDisabled: true);
        }
        else if (!string.IsNullOrEmpty(_existingVersion))
        {
            RenderButton(g, _rectInstallBtn, "覆盖升级", LucideGdiPlus.IconType.Download, isPrimary: true);
        }
        else
        {
            RenderButton(g, _rectInstallBtn, "一键安装", LucideGdiPlus.IconType.Download, isPrimary: true);
        }
    }

    private void RenderDepItemRow(IntPtr g, int index, float x, float y, float w, float h, string name, DepState state)
    {
        LucideGdiPlus.IconType icon;
        uint iconColor;

        if (state == DepState.Ready)
        {
            icon = LucideGdiPlus.IconType.Check;
            iconColor = GdiPlus.FromHex("#10B981");
        }
        else if (state == DepState.Downloading)
        {
            icon = LucideGdiPlus.IconType.Download;
            iconColor = GdiPlus.FromHex("#38BDF8");
        }
        else if (state == DepState.Installing || state == DepState.Checking)
        {
            icon = LucideGdiPlus.IconType.RefreshCw;
            iconColor = GdiPlus.FromHex("#F59E0B");
        }
        else if (state == DepState.Failed)
        {
            icon = LucideGdiPlus.IconType.X;
            iconColor = GdiPlus.FromHex("#EF4444");
        }
        else // Missing
        {
            icon = LucideGdiPlus.IconType.AlertCircle;
            iconColor = GdiPlus.FromHex("#38BDF8");
        }

        float iconSize = S(16.0f);
        float iconY = y + (h - iconSize) / 2.0f;
        if (state is DepState.Installing or DepState.Checking)
        {
            LucideGdiPlus.DrawLoaderCircle(g, x, iconY, iconSize, iconColor, _activityAngle, 2.0f);
        }
        else
        {
            LucideGdiPlus.DrawIcon(g, icon, x, iconY, iconSize, iconColor, 2.0f);
        }

        // 下载/安装中右侧胶囊更宽，待安装状态需要给下载体积提示留出空间
        float nameWidth = state switch
        {
            DepState.Downloading or DepState.Installing => w - S(200),
            DepState.Missing or DepState.Failed => w - S(218),
            _ => w - S(100)
        };
        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#E2E8F0"), out var nameBrush);
        var nameRect = new GdiPlus.RECTF(x + S(24), y, nameWidth, h);
        GdiPlus.GdipCreateStringFormat(0, 0, out var nameFmt);
        GdiPlus.GdipSetStringFormatLineAlign(nameFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipDrawString(g, name, name.Length, _fontBody, ref nameRect, nameFmt, nameBrush);
        GdiPlus.GdipDeleteBrush(nameBrush);
        GdiPlus.GdipDeleteStringFormat(nameFmt);

        // 待安装/失败态把检测结论（预计下载体积或失败原因）直接呈现在胶囊左侧
        if (state is DepState.Missing or DepState.Failed && !string.IsNullOrWhiteSpace(_depDetailText[index]))
        {
            var hintColor = state == DepState.Failed ? "#F87171" : "#94A3B8";
            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex(hintColor), out var hintBrush);
            GdiPlus.GdipCreateStringFormat(0, 0, out var hintFmt);
            GdiPlus.GdipSetStringFormatAlign(hintFmt, GdiPlus.StringAlignment.Far);
            GdiPlus.GdipSetStringFormatLineAlign(hintFmt, GdiPlus.StringAlignment.Center);
            var hintRect = new GdiPlus.RECTF(x + S(24) + nameWidth, y, S(126), h);
            var hintText = _depDetailText[index];
            GdiPlus.GdipDrawString(g, hintText, hintText.Length, _fontPill, ref hintRect, hintFmt, hintBrush);
            GdiPlus.GdipDeleteBrush(hintBrush);
            GdiPlus.GdipDeleteStringFormat(hintFmt);
        }

        float badgeW = S(84.0f);
        float badgeH = S(24.0f);

        if (state is DepState.Downloading or DepState.Installing)
        {
            badgeW = S(185.0f);
            badgeH = S(24.0f);
        }

        float badgeX = x + w - badgeW;
        float badgeY = y + (h - badgeH) / 2.0f;

        _rectDepBtns[index] = new GdiPlus.RECTF(badgeX, badgeY, badgeW, badgeH);

        if (state == DepState.Ready)
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(40, 16, 185, 129), out var bBgBrush);
            LucideGdiPlus.FillRoundedRect(g, bBgBrush, badgeX, badgeY, badgeW, badgeH, S(6.0f));
            GdiPlus.GdipDeleteBrush(bBgBrush);

            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#10B981"), out var bTxtBrush);
            GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
            GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
            GdiPlus.GdipSetStringFormatLineAlign(cFmt, GdiPlus.StringAlignment.Center);
            var bRect = new GdiPlus.RECTF(badgeX, badgeY, badgeW, badgeH);
            string readyText = "已就绪";
            GdiPlus.GdipDrawString(g, readyText, readyText.Length, _fontPill, ref bRect, cFmt, bTxtBrush);
            GdiPlus.GdipDeleteBrush(bTxtBrush);
            GdiPlus.GdipDeleteStringFormat(cFmt);
        }
        else if (state == DepState.Missing)
        {
            RenderButton(
                g,
                _rectDepBtns[index],
                "安装",
                LucideGdiPlus.IconType.Download,
                isPrimary: true,
                isDisabled: !CanStartInstallation());
        }
        else if (state == DepState.Downloading)
        {
            // 胶囊背景底色
            GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(36, 14, 165, 233), out var bBgBrush);
            LucideGdiPlus.FillRoundedRect(g, bBgBrush, badgeX, badgeY, badgeW, badgeH, S(6.0f));
            GdiPlus.GdipDeleteBrush(bBgBrush);

            // 进度条内部高亮填充
            float barW = (badgeW - S(2.0f)) * (float)Math.Clamp(_displayDepPercent[index], 0.0, 1.0);
            if (barW > S(4.0f))
            {
                GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(60, 56, 189, 248), out var fillBrush);
                LucideGdiPlus.FillRoundedRect(g, fillBrush, badgeX + S(1), badgeY + S(1), barW, badgeH - S(2), S(5.0f));
                GdiPlus.GdipDeleteBrush(fillBrush);
            }

            // 边框
            GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#0284C7"), 1.0f, 0, out var bPen);
            LucideGdiPlus.DrawRoundedRect(g, bPen, badgeX, badgeY, badgeW, badgeH, S(6.0f));
            GdiPlus.GdipDeletePen(bPen);

            // 实时速率与进度文本
            string dlText = !string.IsNullOrEmpty(_depDetailText[index]) ? _depDetailText[index] : $"下载 {_depProgress[index]}";
            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#38BDF8"), out var bTxtBrush);
            GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
            GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
            GdiPlus.GdipSetStringFormatLineAlign(cFmt, GdiPlus.StringAlignment.Center);
            var bRect = new GdiPlus.RECTF(badgeX, badgeY, badgeW, badgeH);
            GdiPlus.GdipDrawString(g, dlText, dlText.Length, _fontPill, ref bRect, cFmt, bTxtBrush);
            GdiPlus.GdipDeleteBrush(bTxtBrush);
            GdiPlus.GdipDeleteStringFormat(cFmt);
        }
        else if (state == DepState.Installing)
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(40, 245, 158, 11), out var bBgBrush);
            LucideGdiPlus.FillRoundedRect(g, bBgBrush, badgeX, badgeY, badgeW, badgeH, S(6.0f));
            GdiPlus.GdipDeleteBrush(bBgBrush);

            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F59E0B"), out var bTxtBrush);
            GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
            GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
            GdiPlus.GdipSetStringFormatLineAlign(cFmt, GdiPlus.StringAlignment.Center);
            var bRect = new GdiPlus.RECTF(badgeX, badgeY, badgeW, badgeH);
            string instText = !string.IsNullOrWhiteSpace(_depDetailText[index])
                ? _depDetailText[index]
                : "安装中...";
            GdiPlus.GdipDrawString(g, instText, instText.Length, _fontPill, ref bRect, cFmt, bTxtBrush);
            GdiPlus.GdipDeleteBrush(bTxtBrush);
            GdiPlus.GdipDeleteStringFormat(cFmt);
        }
        else if (state == DepState.Failed)
        {
            RenderButton(
                g,
                _rectDepBtns[index],
                "重试",
                LucideGdiPlus.IconType.RefreshCw,
                isPrimary: false,
                isDanger: true,
                isDisabled: !CanStartInstallation());
        }
        else // Checking
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(40, 245, 158, 11), out var bBgBrush);
            LucideGdiPlus.FillRoundedRect(g, bBgBrush, badgeX, badgeY, badgeW, badgeH, S(6.0f));
            GdiPlus.GdipDeleteBrush(bBgBrush);

            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F59E0B"), out var bTxtBrush);
            GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
            GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
            GdiPlus.GdipSetStringFormatLineAlign(cFmt, GdiPlus.StringAlignment.Center);
            var bRect = new GdiPlus.RECTF(badgeX, badgeY, badgeW, badgeH);
            string chkText = "检测中...";
            GdiPlus.GdipDrawString(g, chkText, chkText.Length, _fontPill, ref bRect, cFmt, bTxtBrush);
            GdiPlus.GdipDeleteBrush(bTxtBrush);
            GdiPlus.GdipDeleteStringFormat(cFmt);
        }
    }
    private void RenderInstallingPage(IntPtr g)
    {
        float iconBoxW = S(64.0f);
        float iconBoxH = S(64.0f);
        float iconBoxX = (WindowWidth - iconBoxW) / 2.0f;
        float iconBoxY = S(80.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#1C1C21"), out var cardBgBrush);
        LucideGdiPlus.FillRoundedRect(g, cardBgBrush, iconBoxX, iconBoxY, iconBoxW, iconBoxH, S(16.0f));
        GdiPlus.GdipDeleteBrush(cardBgBrush);

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#2E2E36"), 1.2f, 0, out var cardStrokePen);
        LucideGdiPlus.DrawRoundedRect(g, cardStrokePen, iconBoxX, iconBoxY, iconBoxW, iconBoxH, S(16.0f));
        GdiPlus.GdipDeletePen(cardStrokePen);

        if (_appIconBitmap != IntPtr.Zero)
        {
            float appIconDrawW = S(44.0f);
            float appIconDrawH = S(44.0f);
            float appIconDrawX = (WindowWidth - appIconDrawW) / 2.0f;
            float appIconDrawY = iconBoxY + (iconBoxH - appIconDrawH) / 2.0f;

            GdiPlus.GdipDrawImageRectRect(
                g, _appIconBitmap,
                appIconDrawX, appIconDrawY, appIconDrawW, appIconDrawH,
                0, 0, _iconW, _iconH,
                2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Download, iconBoxX + S(16), iconBoxY + S(16), S(32), GdiPlus.FromHex("#006EFF"), 2.2f);
        }

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var whiteBrush);
        var titleRect = new GdiPlus.RECTF(S(30), S(164), WindowWidth - S(60), S(28));
        GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
        GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
        string head = (_state == PageState.Uninstall) ? "正在卸载 NexClip..." : "正在部署 NexClip...";
        GdiPlus.GdipDrawString(g, head, head.Length, _fontHeader, ref titleRect, cFmt, whiteBrush);
        GdiPlus.GdipDeleteBrush(whiteBrush);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#94A3B8"), out var textSecBrush);
        var statusRect = new GdiPlus.RECTF(S(30), S(200), WindowWidth - S(60), S(22));
        GdiPlus.GdipDrawString(g, _statusText, _statusText.Length, _fontBody, ref statusRect, cFmt, textSecBrush);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#27272A"), out var trackBrush);
        LucideGdiPlus.FillRoundedRect(g, trackBrush, S(60), S(246), WindowWidth - S(120), S(8), S(4));
        GdiPlus.GdipDeleteBrush(trackBrush);

        float progressWidth = (float)(Math.Clamp(_displayProgress, 0.0, 1.0) * (WindowWidth - S(120)));
        if (progressWidth > S(4))
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#006EFF"), out var indBrush);
            LucideGdiPlus.FillRoundedRect(g, indBrush, S(60), S(246), progressWidth, S(8), S(4));
            GdiPlus.GdipDeleteBrush(indBrush);
        }

        var pctRect = new GdiPlus.RECTF(S(60), S(262), WindowWidth - S(120), S(20));
        GdiPlus.GdipCreateStringFormat(0, 0, out var rFmt);
        GdiPlus.GdipSetStringFormatAlign(rFmt, GdiPlus.StringAlignment.Far);
        GdiPlus.GdipDrawString(g, _subDetailText, _subDetailText.Length, _fontSmall, ref pctRect, rFmt, textSecBrush);

        GdiPlus.GdipDeleteBrush(textSecBrush);
        GdiPlus.GdipDeleteStringFormat(cFmt);
        GdiPlus.GdipDeleteStringFormat(rFmt);
    }

    private void RenderCompletePage(IntPtr g)
    {
        // 1. 顶部成功图标 (CheckCircle 绿色圆圈矢量图标)
        float iconSize = S(58.0f);
        float iconX = (WindowWidth - iconSize) / 2.0f;
        float iconY = S(80.0f);
        var needsAttention = _restartRequired || _detectionPending || _dependenciesIncomplete;
        var iconColor = needsAttention ? GdiPlus.FromHex("#F59E0B") : GdiPlus.FromHex("#10B981");

        LucideGdiPlus.DrawIcon(
            g,
            needsAttention ? LucideGdiPlus.IconType.RefreshCw : LucideGdiPlus.IconType.CheckCircle,
            iconX,
            iconY,
            iconSize,
            iconColor,
            2.8f);

        // 2. 主标题：NexClip 升级完成！ / NexClip 安装完成！
        string appName = "NexClip";
        bool isUpgrade = !string.IsNullOrEmpty(_existingVersion);
        string head = _dependenciesIncomplete
            ? $"{appName} 已安装，依赖待补装"
            : _restartRequired || _detectionPending
                ? $"{appName} 安装完成，需要重启"
                : (isUpgrade ? $"{appName} 升级完成！" : $"{appName} 安装完成！");

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var titleBrush);
        var titleRect = new GdiPlus.RECTF(S(20), S(160), WindowWidth - S(40), S(36));
        GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
        GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipDrawString(g, head, head.Length, _fontTitle, ref titleRect, cFmt, titleBrush);
        GdiPlus.GdipDeleteBrush(titleBrush);

        // 3. 副标题：已成功升级至版本 v{InstallerVersion}，您的剪贴板历史与配置已完整保留。
        string sub = _dependenciesIncomplete
            ? "请手动安装缺失的运行环境组件后再启动 NexClip。"
            : _restartRequired || _detectionPending
                ? "运行环境已完成配置，请重启 Windows 后使用 NexClip。"
                : (isUpgrade
                    ? $"已成功升级至版本 v{InstallerVersion}，您的剪贴板历史与配置已完整保留。"
                    : $"已成功安装至版本 v{InstallerVersion}，跨端同步与剪贴板历史已就绪。");

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#94A3B8"), out var textSecBrush);
        var subRect = new GdiPlus.RECTF(S(20), S(202), WindowWidth - S(40), S(24));
        GdiPlus.GdipDrawString(g, sub, sub.Length, _fontBody, ref subRect, cFmt, textSecBrush);

        // 4. “立即启动 NexClip” 复选框 (居中对齐)
        string checkLabel = $"立即启动 {appName}";
        var dummyLayout = new GdiPlus.RECTF(0, 0, S(400), S(100));
        GdiPlus.GdipMeasureString(g, checkLabel, checkLabel.Length, _fontBody, ref dummyLayout, IntPtr.Zero, out var checkSize, out _, out _);

        float checkTotalW = S(19.0f) + S(8.0f) + checkSize.Width;
        float checkX = (WindowWidth - checkTotalW) / 2.0f;
        float checkY = S(248.0f);
        float checkH = S(24.0f);

        _rectLaunchCheck = new GdiPlus.RECTF(checkX, checkY, checkTotalW, checkH);
        RenderCheckBox(g, _rectLaunchCheck, checkLabel, _launchOnFinish, checkedColor: GdiPlus.FromHex("#006EFF"), textColor: GdiPlus.FromHex("#F8FAFC"));

        // 5. 底部主操作按钮“完成” (居中主色圆角按钮)
        float btnW = S(110.0f);
        float btnH = S(38.0f);
        float btnX = (WindowWidth - btnW) / 2.0f;
        float btnY = S(390.0f);

        _rectDoneBtn = new GdiPlus.RECTF(btnX, btnY, btnW, btnH);
        RenderButton(g, _rectDoneBtn, "完成", null, isPrimary: true);

        GdiPlus.GdipDeleteBrush(textSecBrush);
        GdiPlus.GdipDeleteStringFormat(cFmt);
    }

    private void RenderUninstallPage(IntPtr g)
    {
        float iconBoxW = S(68.0f);
        float iconBoxH = S(68.0f);
        float iconBoxX = (WindowWidth - iconBoxW) / 2.0f;
        float iconBoxY = S(84.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(36, 239, 68, 68), out var dangBgBrush);
        LucideGdiPlus.FillRoundedRect(g, dangBgBrush, iconBoxX, iconBoxY, iconBoxW, iconBoxH, S(18.0f));
        GdiPlus.GdipDeleteBrush(dangBgBrush);

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#EF4444"), 1.2f, 0, out var dangPen);
        LucideGdiPlus.DrawRoundedRect(g, dangPen, iconBoxX, iconBoxY, iconBoxW, iconBoxH, S(18.0f));
        GdiPlus.GdipDeletePen(dangPen);

        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Trash2, iconBoxX + S(17), iconBoxY + S(17), S(34), GdiPlus.FromHex("#EF4444"), 2.2f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var whiteBrush);
        var titleRect = new GdiPlus.RECTF(S(30), S(170), WindowWidth - S(60), S(28));
        GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
        GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
        string head = "卸载 NexClip";
        GdiPlus.GdipDrawString(g, head, head.Length, _fontHeader, ref titleRect, cFmt, whiteBrush);
        GdiPlus.GdipDeleteBrush(whiteBrush);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#94A3B8"), out var textSecBrush);
        var subRect = new GdiPlus.RECTF(S(30), S(206), WindowWidth - S(60), S(22));
        string sub = "您确定要从当前计算机中彻底移除 NexClip 吗？";
        GdiPlus.GdipDrawString(g, sub, sub.Length, _fontBody, ref subRect, cFmt, textSecBrush);
        GdiPlus.GdipDeleteBrush(textSecBrush);
        GdiPlus.GdipDeleteStringFormat(cFmt);

        _rectKeepDataCheck = new GdiPlus.RECTF(S(180), S(242), S(260), S(24));
        RenderCheckBox(g, _rectKeepDataCheck, "保留用户本地配置与历史记录数据", _keepUserData);

        float btnW = S(146.0f);
        float btnH = S(40.0f);
        float btnGap = S(16.0f);
        float totalBtnW = btnW * 2 + btnGap;
        float startX = (WindowWidth - totalBtnW) / 2.0f;
        float btnY = S(286.0f);

        _rectConfirmUninstallBtn = new GdiPlus.RECTF(startX, btnY, btnW, btnH);
        _rectCancelUninstallBtn = new GdiPlus.RECTF(startX + btnW + btnGap, btnY, btnW, btnH);

        RenderButton(g, _rectConfirmUninstallBtn, "确认卸载", LucideGdiPlus.IconType.Trash2, isPrimary: false, isDanger: true);
        RenderButton(g, _rectCancelUninstallBtn, "取消", null, isPrimary: false);
    }

    private void RenderUninstallCompletePage(IntPtr g)
    {
        float iconBoxW = S(68.0f);
        float iconBoxH = S(68.0f);
        float iconBoxX = (WindowWidth - iconBoxW) / 2.0f;
        float iconBoxY = S(92.0f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.Argb(36, 16, 185, 129), out var succBgBrush);
        LucideGdiPlus.FillRoundedRect(g, succBgBrush, iconBoxX, iconBoxY, iconBoxW, iconBoxH, S(18.0f));
        GdiPlus.GdipDeleteBrush(succBgBrush);

        GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#10B981"), 1.2f, 0, out var succPen);
        LucideGdiPlus.DrawRoundedRect(g, succPen, iconBoxX, iconBoxY, iconBoxW, iconBoxH, S(18.0f));
        GdiPlus.GdipDeletePen(succPen);

        LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Check, iconBoxX + S(17), iconBoxY + S(17), S(34), GdiPlus.FromHex("#10B981"), 2.4f);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#F8FAFC"), out var titleBrush);
        var titleRect = new GdiPlus.RECTF(S(30), S(180), WindowWidth - S(60), S(30));
        GdiPlus.GdipCreateStringFormat(0, 0, out var cFmt);
        GdiPlus.GdipSetStringFormatAlign(cFmt, GdiPlus.StringAlignment.Center);
        string head = "卸载已完成";
        GdiPlus.GdipDrawString(g, head, head.Length, _fontHeader, ref titleRect, cFmt, titleBrush);
        GdiPlus.GdipDeleteBrush(titleBrush);

        GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#94A3B8"), out var textSecBrush);
        var subRect = new GdiPlus.RECTF(S(30), S(218), WindowWidth - S(60), S(24));
        string sub = "NexClip 已成功从您的计算机中完全移除。";
        GdiPlus.GdipDrawString(g, sub, sub.Length, _fontBody, ref subRect, cFmt, textSecBrush);
        GdiPlus.GdipDeleteBrush(textSecBrush);
        GdiPlus.GdipDeleteStringFormat(cFmt);

        _rectDoneBtn = new GdiPlus.RECTF((WindowWidth - S(146.0f)) / 2.0f, S(272.0f), S(146.0f), S(40.0f));
        RenderButton(g, _rectDoneBtn, "关闭", null, isPrimary: true);
    }

    private void RenderButton(IntPtr g, GdiPlus.RECTF rect, string text, LucideGdiPlus.IconType? icon, bool isPrimary = false, bool isDanger = false, bool isDisabled = false, uint? customColor = null)
    {
        bool isHover = rect.Contains(_mouseX, _mouseY) && !isDisabled;
        bool isPressed = isHover && _isLButtonDown;

        uint bgCol, strokeCol, textCol;

        if (isDisabled)
        {
            bgCol = GdiPlus.FromHex("#202025");
            strokeCol = GdiPlus.FromHex("#2A2A30");
            textCol = GdiPlus.FromHex("#71717A");
        }
        else if (isDanger)
        {
            bgCol = isPressed ? GdiPlus.FromHex("#DC2626") : (isHover ? GdiPlus.FromHex("#EF4444") : GdiPlus.FromHex("#DC2626"));
            strokeCol = bgCol;
            textCol = GdiPlus.FromHex("#FFFFFF");
        }
        else if (customColor.HasValue)
        {
            bgCol = isPressed ? GdiPlus.FromHex("#C33D14") : (isHover ? GdiPlus.FromHex("#E85A2A") : customColor.Value);
            strokeCol = bgCol;
            textCol = GdiPlus.FromHex("#FFFFFF");
        }
        else if (isPrimary)
        {
            bgCol = isPressed ? GdiPlus.FromHex("#005ED6") : (isHover ? GdiPlus.FromHex("#1A7DFF") : GdiPlus.FromHex("#006EFF"));
            strokeCol = bgCol;
            textCol = GdiPlus.FromHex("#FFFFFF");
        }
        else
        {
            bgCol = isPressed ? GdiPlus.FromHex("#2A2A30") : (isHover ? GdiPlus.FromHex("#24242A") : GdiPlus.FromHex("#1C1C21"));
            strokeCol = GdiPlus.FromHex("#33333C");
            textCol = GdiPlus.FromHex("#F8FAFC");
        }

        GdiPlus.GdipCreateSolidFill(bgCol, out var bgBrush);
        LucideGdiPlus.FillRoundedRect(g, bgBrush, rect.X, rect.Y, rect.Width, rect.Height, S(6.0f));
        GdiPlus.GdipDeleteBrush(bgBrush);

        GdiPlus.GdipCreatePen1(strokeCol, 1.0f, 0, out var strokePen);
        LucideGdiPlus.DrawRoundedRect(g, strokePen, rect.X, rect.Y, rect.Width, rect.Height, S(6.0f));
        GdiPlus.GdipDeletePen(strokePen);

        GdiPlus.GdipCreateSolidFill(textCol, out var txtBrush);

        var dummyLayout = new GdiPlus.RECTF(0, 0, S(600), S(100));
        GdiPlus.GdipMeasureString(g, text, text.Length, _fontButton, ref dummyLayout, IntPtr.Zero, out var textBox, out _, out _);

        if (icon.HasValue)
        {
            float iconSize = S(14.0f);
            float gap = S(6.0f);
            float totalContentW = iconSize + gap + textBox.Width;
            float startX = rect.X + (rect.Width - totalContentW) / 2.0f;
            float iconY = rect.Y + (rect.Height - iconSize) / 2.0f;

            LucideGdiPlus.DrawIcon(g, icon.Value, startX, iconY, iconSize, textCol, 2.0f);

            var txtRect = new GdiPlus.RECTF(startX + iconSize + gap, rect.Y + (rect.Height - textBox.Height) / 2.0f, textBox.Width + S(6), textBox.Height + S(2));
            GdiPlus.GdipDrawString(g, text, text.Length, _fontButton, ref txtRect, IntPtr.Zero, txtBrush);
        }
        else
        {
            float startX = rect.X + (rect.Width - textBox.Width) / 2.0f;
            var txtRect = new GdiPlus.RECTF(startX, rect.Y + (rect.Height - textBox.Height) / 2.0f, textBox.Width + S(6), textBox.Height + S(2));
            GdiPlus.GdipDrawString(g, text, text.Length, _fontButton, ref txtRect, IntPtr.Zero, txtBrush);
        }

        GdiPlus.GdipDeleteBrush(txtBrush);
    }

    private void RenderCheckBox(IntPtr g, GdiPlus.RECTF rect, string label, bool isChecked, uint? checkedColor = null, uint? textColor = null)
    {
        float boxSize = S(19.0f);
        float boxY = rect.Y + (rect.Height - boxSize) / 2.0f;

        if (isChecked)
        {
            uint checkBg = checkedColor ?? GdiPlus.FromHex("#006EFF");
            GdiPlus.GdipCreateSolidFill(checkBg, out var accentBrush);
            LucideGdiPlus.FillRoundedRect(g, accentBrush, rect.X, boxY, boxSize, boxSize, S(4.0f));
            GdiPlus.GdipDeleteBrush(accentBrush);

            LucideGdiPlus.DrawIcon(g, LucideGdiPlus.IconType.Check, rect.X + S(2.5f), boxY + S(2.5f), S(14), GdiPlus.FromHex("#FFFFFF"), 2.2f);
        }
        else
        {
            GdiPlus.GdipCreateSolidFill(GdiPlus.FromHex("#27272A"), out var boxBgBrush);
            LucideGdiPlus.FillRoundedRect(g, boxBgBrush, rect.X, boxY, boxSize, boxSize, S(4.0f));
            GdiPlus.GdipDeleteBrush(boxBgBrush);

            GdiPlus.GdipCreatePen1(GdiPlus.FromHex("#3F3F46"), 1.2f, 0, out var boxStrokePen);
            LucideGdiPlus.DrawRoundedRect(g, boxStrokePen, rect.X, boxY, boxSize, boxSize, S(4.0f));
            GdiPlus.GdipDeletePen(boxStrokePen);
        }

        uint labelCol = textColor ?? GdiPlus.FromHex("#E2E8F0");
        GdiPlus.GdipCreateSolidFill(labelCol, out var labelBrush);
        var labelRect = new GdiPlus.RECTF(rect.X + boxSize + S(8), rect.Y, rect.Width - boxSize - S(8), rect.Height);
        GdiPlus.GdipCreateStringFormat(0, 0, out var lblFmt);
        GdiPlus.GdipSetStringFormatLineAlign(lblFmt, GdiPlus.StringAlignment.Center);
        GdiPlus.GdipDrawString(g, label, label.Length, _fontBody, ref labelRect, lblFmt, labelBrush);
        GdiPlus.GdipDeleteBrush(labelBrush);
        GdiPlus.GdipDeleteStringFormat(lblFmt);
    }
}
