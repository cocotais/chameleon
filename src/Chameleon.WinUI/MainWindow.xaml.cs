using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Chameleon.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        SetWindowIcon();
        _windowHandle = WindowNative.GetWindowHandle(this);
        _subclassProc = WindowSubclassProc;
        SetWindowSubclass(_windowHandle, _subclassProc, WindowSubclassId, UIntPtr.Zero);
        ConfigureTitleBar();

        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        Closed += MainWindow_Closed;
        SizeChanged += MainWindow_SizeChanged;

        JobsList.ItemsSource = Jobs;
        _worker.NotificationReceived += OnWorkerNotification;
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarTheme();
    }

    private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.Size.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width < 900)
        {
            PaneColumn.Width = new GridLength(260);
            PaneContent.Padding = new Thickness(14, 12, 12, 14);
            ContentPage.Padding = new Thickness(14, 12, 14, 14);
            ShellGrid.ColumnSpacing = 0;
            return;
        }

        if (width < 1200)
        {
            PaneColumn.Width = new GridLength(288);
            PaneContent.Padding = new Thickness(16, 14, 14, 16);
            ContentPage.Padding = new Thickness(18, 14, 18, 16);
            ShellGrid.ColumnSpacing = 0;
            return;
        }

        PaneColumn.Width = new GridLength(320);
        PaneContent.Padding = new Thickness(20, 16, 16, 20);
        ContentPage.Padding = new Thickness(24, 18, 24, 20);
        ShellGrid.ColumnSpacing = 0;
    }
}
