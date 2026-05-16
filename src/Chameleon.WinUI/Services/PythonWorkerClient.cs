using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chameleon.WinUI.Services;

public sealed class PythonWorkerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private CancellationTokenSource? _workerLifetime;
    private long _nextId;
    private Task? _readerTask;
    private Task? _errorTask;

    public event EventHandler<WorkerNotification>? NotificationReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _workerLifetime?.Dispose();
        _workerLifetime = new CancellationTokenSource();

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-m chameleon_worker",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            startInfo.Environment["PYTHONPATH"] = Path.Combine(repoRoot, "python");
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Python worker.");

        _readerTask = Task.Run(() => ReadLoopAsync(_process, _workerLifetime.Token));
        _errorTask = Task.Run(() => DrainErrorAsync(_process, _workerLifetime.Token));
        return Task.CompletedTask;
    }

    public async Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return await SendRequestAsync(
            "initialize",
            new { client = "Chameleon.WinUI" },
            cancellationToken);
    }

    public Task<JsonElement> ProbeMediaAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        return SendRequestAsync("probe_media", new { input_path = inputPath }, cancellationToken);
    }

    public async Task<string> RunConversionTaskAsync(
        string inputPath,
        string outputDir,
        string targetFormat,
        string preset,
        CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync(
            "run_task",
            new
            {
                kind = "media.convert",
                input_path = inputPath,
                output_dir = outputDir,
                target_format = targetFormat,
                preset,
                options = new { },
            },
            cancellationToken);

        return result.GetProperty("task_id").GetString()
            ?? throw new InvalidOperationException("Worker did not return a task id.");
    }

    public Task<JsonElement> CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return SendRequestAsync("cancel_task", new { task_id = taskId }, cancellationToken);
    }

    public Task<JsonElement> GetStatusAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return SendRequestAsync("get_status", new { task_id = taskId }, cancellationToken);
    }

    public Task<JsonElement> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        return SendRequestAsync("shutdown", new { }, cancellationToken);
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken);

        var process = _process ?? throw new InvalidOperationException("Worker is not running.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var payload = JsonSerializer.Serialize(
            new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            });

        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);

        await using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        return await completion.Task;
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();

            if (root.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out var id)
                && _pending.TryRemove(id, out var completion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException(error.ToString()));
                }
                else
                {
                    completion.TrySetResult(root.GetProperty("result"));
                }
                continue;
            }

            if (root.TryGetProperty("method", out var methodElement)
                && root.TryGetProperty("params", out var paramsElement))
            {
                NotificationReceived?.Invoke(
                    this,
                    new WorkerNotification(
                        methodElement.GetString() ?? "",
                        paramsElement.Clone()));
            }
        }
    }

    private static async Task DrainErrorAsync(Process process, CancellationToken cancellationToken)
    {
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            Debug.WriteLine($"python-worker: {line}");
        }
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                && Directory.Exists(Path.Combine(current.FullName, "python")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            try
            {
                await ShutdownAsync();
            }
            catch
            {
                // Worker may already be gone during app shutdown.
            }
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _workerLifetime?.Cancel();
        if (_readerTask is not null || _errorTask is not null)
        {
            try
            {
                await Task.WhenAll(
                    _readerTask ?? Task.CompletedTask,
                    _errorTask ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _process.Dispose();
        _workerLifetime?.Dispose();
    }
}

public sealed record WorkerNotification(string Method, JsonElement Params);
