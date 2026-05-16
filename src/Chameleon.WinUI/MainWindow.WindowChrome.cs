using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.UI;
using WinRT.Interop;

namespace Chameleon.WinUI;

public sealed partial class MainWindow
{
    private bool _openingAnimationStarted;
    private bool _closeAnimationRunning;
    private bool _windowStateAnimationRunning;
    private bool _awaitingMinimizedRestore;
    private bool _allowClose;
    private readonly IntPtr _windowHandle;
    private readonly SubclassProc _subclassProc;
    private AppWindowTitleBar? _appWindowTitleBar;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTitleBarTheme();

        if (_openingAnimationStarted)
        {
            return;
        }

        _openingAnimationStarted = true;
        CreateWindowAnimation(0, 1, 0.985, 1, TimeSpan.FromMilliseconds(180), EasingMode.EaseOut).Begin();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        RemoveWindowSubclass(_windowHandle, _subclassProc, WindowSubclassId);
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmSysCommand && TryHandleWindowStateCommand(wParam))
        {
            return IntPtr.Zero;
        }

        if (message == WmSize
            && wParam != (UIntPtr)SizeMinimized
            && _awaitingMinimizedRestore
            && !_windowStateAnimationRunning)
        {
            BeginMinimizedRestoreTransition();
        }

        if (message == WmGetMinMaxInfo)
        {
            EnforceMinimumTrackSize(lParam);
        }

        if (message == WmClose && !_allowClose)
        {
            BeginCloseAnimation();
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveWindowSubclass(hWnd, _subclassProc, WindowSubclassId);
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ConfigureTitleBar()
    {
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindowTitleBar = AppWindow.GetFromWindowId(windowId).TitleBar;
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        if (_appWindowTitleBar is null)
        {
            return;
        }

        var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var foreground = isDark ? Colors.White : Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
        var inactiveForeground = isDark
            ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x72, 0x00, 0x00, 0x00);
        var hoverBackground = isDark
            ? Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
        var pressedBackground = isDark
            ? Color.FromArgb(0x23, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x18, 0x00, 0x00, 0x00);

        _appWindowTitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindowTitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindowTitleBar.ButtonHoverBackgroundColor = hoverBackground;
        _appWindowTitleBar.ButtonPressedBackgroundColor = pressedBackground;
        _appWindowTitleBar.ButtonForegroundColor = foreground;
        _appWindowTitleBar.ButtonHoverForegroundColor = foreground;
        _appWindowTitleBar.ButtonPressedForegroundColor = foreground;
        _appWindowTitleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private void EnforceMinimumTrackSize(IntPtr lParam)
    {
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var scale = GetDpiForWindow(_windowHandle) / 96.0;

        minMaxInfo.MinTrackSize.X = (int)Math.Ceiling(MinimumWindowWidth * scale);
        minMaxInfo.MinTrackSize.Y = (int)Math.Ceiling(MinimumWindowHeight * scale);

        Marshal.StructureToPtr(minMaxInfo, lParam, fDeleteOld: false);
    }

    private bool TryHandleWindowStateCommand(UIntPtr wParam)
    {
        var command = wParam.ToUInt32() & 0xFFF0;
        return command switch
        {
            ScMinimize => BeginWindowStateTransition(SwMinimize, fadeOutOnly: true),
            ScMaximize => BeginMaximizeTransition(),
            ScRestore => _awaitingMinimizedRestore
                ? BeginMinimizedRestoreTransition()
                : BeginRestoreOpacityTransition(),
            _ => false
        };
    }

    private bool BeginMaximizeTransition()
    {
        if (_windowStateAnimationRunning || _closeAnimationRunning)
        {
            return true;
        }

        _windowStateAnimationRunning = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowWindow(_windowHandle, SwMaximize);
            RootGrid.Opacity = 0.78;
            RootScaleTransform.ScaleX = 0.965;
            RootScaleTransform.ScaleY = 0.965;
            StartWindowStateRevealAnimation(0.965);
        });

        return true;
    }

    private bool BeginRestoreOpacityTransition()
    {
        if (_windowStateAnimationRunning || _closeAnimationRunning)
        {
            return true;
        }

        _windowStateAnimationRunning = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowWindow(_windowHandle, SwRestore);
            RootGrid.Opacity = 0.68;
            RootScaleTransform.ScaleX = 1.015;
            RootScaleTransform.ScaleY = 1.015;
            StartWindowStateRevealAnimation(1.015);
        });

        return true;
    }

    private bool BeginMinimizedRestoreTransition()
    {
        if (_windowStateAnimationRunning || _closeAnimationRunning)
        {
            return true;
        }

        _awaitingMinimizedRestore = false;
        _windowStateAnimationRunning = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            var storyboard = CreateWindowAnimation(
                RootGrid.Opacity,
                1,
                0.985,
                1,
                WindowResizeAnimationDuration,
                EasingMode.EaseOut);

            storyboard.Completed += (_, _) =>
            {
                _windowStateAnimationRunning = false;
            };
            storyboard.Begin();
            ShowWindow(_windowHandle, SwRestore);
        });

        return true;
    }

    private bool BeginWindowStateTransition(
        int showCommand,
        bool fadeOutOnly = false,
        double previewScale = 0.985,
        double revealScale = 0.985)
    {
        if (_windowStateAnimationRunning || _closeAnimationRunning)
        {
            return true;
        }

        _windowStateAnimationRunning = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!fadeOutOnly)
            {
                RootGrid.Opacity = 0.78;
                RootScaleTransform.ScaleX = previewScale;
                RootScaleTransform.ScaleY = previewScale;

                ShowWindow(_windowHandle, showCommand);
                StartWindowStateRevealAnimation(revealScale);
                return;
            }

            var storyboard = CreateWindowAnimation(
                RootGrid.Opacity,
                0.5,
                RootScaleTransform.ScaleX,
                0.94,
                MinimizeAnimationDuration,
                EasingMode.EaseIn);

            storyboard.Begin();
            ShowWindow(_windowHandle, showCommand);
            _awaitingMinimizedRestore = true;
            _windowStateAnimationRunning = false;
        });

        return true;
    }

    private void StartWindowStateRevealAnimation(double scaleFrom = 1, TimeSpan? duration = null)
    {
        var storyboard = CreateWindowAnimation(
            RootGrid.Opacity,
            1,
            scaleFrom,
            1,
            duration ?? WindowResizeAnimationDuration,
            EasingMode.EaseOut);

        storyboard.Completed += (_, _) =>
        {
            _windowStateAnimationRunning = false;
        };
        storyboard.Begin();
    }

    private void StartOpacityRevealAnimation(double opacityFrom, TimeSpan? duration = null)
    {
        RootScaleTransform.ScaleX = 1;
        RootScaleTransform.ScaleY = 1;

        var storyboard = CreateOpacityAnimation(
            opacityFrom,
            1,
            duration ?? TimeSpan.FromMilliseconds(140),
            EasingMode.EaseOut);

        storyboard.Completed += (_, _) =>
        {
            _windowStateAnimationRunning = false;
        };
        storyboard.Begin();
    }

    private void BeginCloseAnimation()
    {
        if (_closeAnimationRunning)
        {
            return;
        }

        _closeAnimationRunning = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            var storyboard = CreateWindowAnimation(
                RootGrid.Opacity,
                0,
                RootScaleTransform.ScaleX,
                0.985,
                TimeSpan.FromMilliseconds(120),
                EasingMode.EaseIn);

            storyboard.Completed += (_, _) =>
            {
                _allowClose = true;
                Close();
            };
            storyboard.Begin();
        });
    }

    private Storyboard CreateWindowAnimation(
        double opacityFrom,
        double opacityTo,
        double scaleFrom,
        double scaleTo,
        TimeSpan duration,
        EasingMode easingMode)
    {
        RootGrid.Opacity = opacityFrom;
        RootScaleTransform.ScaleX = scaleFrom;
        RootScaleTransform.ScaleY = scaleFrom;

        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = easingMode };

        AddDoubleAnimation(storyboard, RootGrid, "Opacity", opacityFrom, opacityTo, duration, easing);
        AddDoubleAnimation(storyboard, RootScaleTransform, "ScaleX", scaleFrom, scaleTo, duration, easing);
        AddDoubleAnimation(storyboard, RootScaleTransform, "ScaleY", scaleFrom, scaleTo, duration, easing);

        return storyboard;
    }

    private Storyboard CreateOpacityAnimation(
        double opacityFrom,
        double opacityTo,
        TimeSpan duration,
        EasingMode easingMode)
    {
        RootGrid.Opacity = opacityFrom;

        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = easingMode };

        AddDoubleAnimation(storyboard, RootGrid, "Opacity", opacityFrom, opacityTo, duration, easing);

        return storyboard;
    }

    private static void AddDoubleAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        TimeSpan duration,
        EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Chameleon.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow.GetFromWindowId(windowId).SetIcon(iconPath);
    }

    private const uint WmClose = 0x0010;
    private const uint WmSize = 0x0005;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmSysCommand = 0x0112;
    private const uint WmNcDestroy = 0x0082;
    private const uint WindowSubclassId = 1;
    private const uint SizeMinimized = 1;
    private const uint ScMinimize = 0xF020;
    private const uint ScMaximize = 0xF030;
    private const uint ScRestore = 0xF120;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private const int MinimumWindowWidth = 900;
    private const int MinimumWindowHeight = 600;
    private static readonly TimeSpan WindowResizeAnimationDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimizeAnimationDuration = TimeSpan.FromMilliseconds(80);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        uint subclassId,
        UIntPtr refData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        uint subclassId);

    [DllImport("Comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
