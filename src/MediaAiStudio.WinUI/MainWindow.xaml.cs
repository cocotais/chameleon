using MediaAiStudio.WinUI.Services;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MediaAiStudio.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly PythonWorkerClient _worker = new();
    private string? _activeTaskId;

    public MainWindow()
    {
        InitializeComponent();
        _worker.NotificationReceived += OnWorkerNotification;
    }

    private async void InitializeWorker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _worker.StartAsync();
            var result = await _worker.InitializeAsync();
            AppendLog($"Initialized: {result}");
            StatusText.Text = "Worker initialized.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Initialize failed: {ex.Message}";
            AppendLog(ex.ToString());
        }
    }

    private async void RunSampleTask_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _worker.StartAsync();

            var sampleDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaAiStudio",
                "Samples");
            Directory.CreateDirectory(sampleDir);

            var inputPath = Path.Combine(sampleDir, "sample.txt");
            var outputDir = Path.Combine(sampleDir, "Output");
            await File.WriteAllTextAsync(inputPath, "sample media payload path");

            _activeTaskId = await _worker.RunTaskAsync(
                kind: "image.echo",
                inputPath: inputPath,
                outputDir: outputDir,
                provider: "local");

            StatusText.Text = $"Task started: {_activeTaskId}";
            AppendLog($"Started task {_activeTaskId}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Task failed to start: {ex.Message}";
            AppendLog(ex.ToString());
        }
    }

    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTaskId is null)
        {
            StatusText.Text = "No active task.";
            return;
        }

        await _worker.CancelTaskAsync(_activeTaskId);
        StatusText.Text = "Cancellation requested.";
    }

    private async void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        await _worker.ShutdownAsync();
        StatusText.Text = "Worker shutdown requested.";
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

            var state = e.Params.GetProperty("state").GetString();
            var message = e.Params.GetProperty("message").GetString();
            var progress = e.Params.GetProperty("progress").GetDouble();
            TaskProgress.Value = progress * 100;
            StatusText.Text = $"{state}: {message}";

            if (state == "completed" && e.Params.TryGetProperty("result", out var result))
            {
                ResultText.Text = result.ToString();
            }

            AppendLog(e.Params.ToString());
        });
    }

    private void AppendLog(string message)
    {
        LogText.Text += $"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}";
    }
}

