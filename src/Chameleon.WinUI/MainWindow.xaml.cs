using Chameleon.WinUI.Models;
using Chameleon.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chameleon.WinUI;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<ConversionJob> Jobs { get; } = new();

    private readonly PythonWorkerClient _worker = new();
    private readonly Dictionary<string, ConversionJob> _jobsByTaskId = new();
    private string? _activeTaskId;
    private string? _selectedOutputFolder;
    private bool _queueRunning;
    private bool _workerInitialized;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        SetWindowIcon();
        SizeChanged += MainWindow_SizeChanged;

        JobsList.ItemsSource = Jobs;
        _worker.NotificationReceived += OnWorkerNotification;
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

    private async void InitializeWorker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureWorkerInitializedAsync();
            SetStatus("Worker is ready.");
        }
        catch (Exception ex)
        {
            SetStatus($"Initialize failed: {ex.Message}", InfoBarSeverity.Error);
            AppendLog(ex.ToString());
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            await AddFilesAsync(files.Select(file => file.Path));
        }
        catch (Exception ex)
        {
            SetStatus($"Add files failed: {ex.Message}", InfoBarSeverity.Error);
            AppendLog(ex.ToString());
        }
    }

    private async void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            _selectedOutputFolder = folder.Path;
            OutputFolderText.Text = _selectedOutputFolder;
        }
        catch (Exception ex)
        {
            SetStatus($"Output folder failed: {ex.Message}", InfoBarSeverity.Error);
            AppendLog(ex.ToString());
        }
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_queueRunning)
        {
            SetStatus("Queue is already running.");
            return;
        }

        if (Jobs.Count == 0)
        {
            SetStatus("Add files before starting the queue.", InfoBarSeverity.Warning);
            return;
        }

        _queueRunning = true;
        try
        {
            await EnsureWorkerInitializedAsync();

            foreach (var job in Jobs.Where(job => job.Status is "Pending" or "Failed" or "Cancelled").ToList())
            {
                await RunJobAsync(job);
            }

            SetStatus("Queue finished.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Queue failed: {ex.Message}", InfoBarSeverity.Error);
            AppendLog(ex.ToString());
        }
        finally
        {
            _queueRunning = false;
            _activeTaskId = null;
        }
    }

    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTaskId is null)
        {
            SetStatus("No active task.");
            return;
        }

        await _worker.CancelTaskAsync(_activeTaskId);
        SetStatus("Cancellation requested.");
    }

    private async void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        await _worker.ShutdownAsync();
        _workerInitialized = false;
        SetStatus("Worker shutdown requested.");
    }

    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        LogTip.IsOpen = true;
    }

    private void OnWorkerNotification(object? sender, WorkerNotification e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Method != "task.progress")
            {
                AppendLog($"Notification {e.Method}: {e.Params}");
                return;
            }

            if (!e.Params.TryGetProperty("task_id", out var taskIdElement))
            {
                return;
            }

            var taskId = taskIdElement.GetString();
            if (taskId is null || !_jobsByTaskId.TryGetValue(taskId, out var job))
            {
                return;
            }

            ApplyStatus(job, e.Params);
            AppendLog(e.Params.ToString());
        });
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        await EnsureWorkerInitializedAsync();

        foreach (var path in paths.Where(File.Exists))
        {
            var job = new ConversionJob(path, ResolveTargetFormatForPath(path));
            Jobs.Add(job);
            await ProbeJobAsync(job);
        }

        SetStatus($"Queue contains {Jobs.Count} file(s).");
    }

    private async Task EnsureWorkerInitializedAsync()
    {
        if (_workerInitialized)
        {
            return;
        }

        SetStatus("Starting worker...");
        await _worker.StartAsync();
        var result = await _worker.InitializeAsync();
        _workerInitialized = true;
        AppendLog($"Initialized: {result}");
    }

    private async Task ProbeJobAsync(ConversionJob job)
    {
        try
        {
            var probe = await _worker.ProbeMediaAsync(job.InputPath);
            if (probe.TryGetProperty("extension", out var extension))
            {
                job.SourceFormat = extension.GetString() ?? job.SourceFormat;
            }

            if (probe.TryGetProperty("duration_seconds", out var duration)
                && duration.ValueKind == JsonValueKind.Number)
            {
                job.Duration = FormatDuration(duration.GetDouble());
            }

            job.Message = "Ready";
        }
        catch (Exception ex)
        {
            job.Status = "Failed";
            job.Message = "Probe failed";
            job.Error = ex.Message;
            AppendLog($"Probe failed for {job.FileName}: {ex.Message}");
        }
    }

    private async Task RunJobAsync(ConversionJob job)
    {
        var outputFolder = _selectedOutputFolder
            ?? Path.Combine(Path.GetDirectoryName(job.InputPath) ?? Environment.CurrentDirectory, "Converted");

        Directory.CreateDirectory(outputFolder);

        job.Status = "Running";
        job.Progress = 0;
        job.Message = "Starting";
        job.Error = "";

        _activeTaskId = await _worker.RunConversionTaskAsync(
            inputPath: job.InputPath,
            outputDir: outputFolder,
            targetFormat: job.TargetFormat,
            preset: GetSelectedPreset());

        job.TaskId = _activeTaskId;
        _jobsByTaskId[_activeTaskId] = job;
        SetStatus($"Converting {job.FileName}");

        while (job.Status == "Running")
        {
            await Task.Delay(350);
            if (job.TaskId is null)
            {
                break;
            }

            var status = await _worker.GetStatusAsync(job.TaskId);
            ApplyStatus(job, status);
        }
    }

    private void ApplyStatus(ConversionJob job, JsonElement payload)
    {
        var state = payload.GetProperty("state").GetString() ?? "running";
        var message = payload.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? state
            : state;

        job.Status = TitleCase(state);
        job.Message = message;

        if (payload.TryGetProperty("progress", out var progressElement)
            && progressElement.ValueKind == JsonValueKind.Number)
        {
            job.Progress = progressElement.GetDouble() * 100;
        }

        if (payload.TryGetProperty("error", out var errorElement)
            && errorElement.ValueKind == JsonValueKind.String)
        {
            job.Error = errorElement.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(job.Error))
            {
                job.Message = job.Error;
            }
        }

        if (payload.TryGetProperty("result", out var resultElement)
            && resultElement.ValueKind == JsonValueKind.Object
            && resultElement.TryGetProperty("output_path", out var outputPathElement))
        {
            job.OutputPath = outputPathElement.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(job.OutputPath))
            {
                job.Message = job.OutputPath;
            }
        }

        SetStatus($"{job.FileName}: {job.Status} - {job.Message}");
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        await AddFilesAsync(items.OfType<StorageFile>().Select(file => file.Path));
    }

    private string GetSelectedTargetFormat()
    {
        return (TargetFormatBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "mp4";
    }

    private string ResolveTargetFormatForPath(string path)
    {
        var selected = GetSelectedTargetFormat();
        if (!string.Equals(selected, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        return SuggestTargetFormat(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
    }

    private static string SuggestTargetFormat(string sourceFormat)
    {
        string[] videoFormats = new[] { "mp4", "mkv", "mov", "webm", "avi" };
        string[] audioFormats = new[] { "mp3", "wav", "flac", "aac", "m4a", "ogg", "opus" };
        string[] imageFormats = new[] { "png", "jpg", "jpeg", "webp" };

        if (videoFormats.Contains(sourceFormat))
        {
            return "mp4";
        }

        if (audioFormats.Contains(sourceFormat))
        {
            return "mp3";
        }

        if (imageFormats.Contains(sourceFormat))
        {
            return "jpg";
        }

        return "mp4";
    }

    private void TargetFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var job in Jobs.Where(job => job.Status == "Pending"))
        {
            job.TargetFormat = ResolveTargetFormatForPath(job.InputPath);
        }
    }

    private string GetSelectedPreset()
    {
        return (PresetBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "balanced";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }

    private static string TitleCase(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private void AppendLog(string message)
    {
        LogText.Text += $"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}";
    }

    private void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusInfo.Message = message;
        StatusInfo.Severity = severity;
        StatusInfo.IsOpen = true;
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
}
