using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Chameleon.WinUI.Models;

public sealed class ConversionJob : INotifyPropertyChanged
{
    private string _sourceFormat;
    private string _targetFormat;
    private string _status = "Pending";
    private string _message = "Waiting";
    private string _duration = "";
    private string _outputPath = "";
    private string _error = "";
    private double _progress;

    public ConversionJob(string inputPath, string targetFormat)
    {
        Id = Guid.NewGuid().ToString("N");
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
        _sourceFormat = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        _targetFormat = targetFormat;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string InputPath { get; }

    public string FileName { get; }

    public string? TaskId { get; set; }

    public string SourceFormat
    {
        get => _sourceFormat;
        set => SetField(ref _sourceFormat, value);
    }

    public string TargetFormat
    {
        get => _targetFormat;
        set => SetField(ref _targetFormat, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public string Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public string Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
