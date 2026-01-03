using GameListener.Transcription.Models;
using GameListener.Transcription.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace GameListener.Transcription.Services;

public sealed class WhisperNetTranscriptionClient : IAudioTranscriptionClient, IAsyncDisposable
{
    private readonly ILogger<WhisperNetTranscriptionClient> _logger;
    private readonly TranscriptionOptions _options;
    private WhisperProcessor? _processor;

    public WhisperNetTranscriptionClient(
        ILogger<WhisperNetTranscriptionClient> logger,
        IOptions<TranscriptionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<AudioTextResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken)
    {
        await EnsureProcessorAsync(cancellationToken);

        using var result = await _processor!.ProcessAsync(request.AudioBytes, cancellationToken: cancellationToken);
        var text = await result.GetTextAsync();
        var language = result.Language;

        return new AudioTextResult
        {
            Text = text ?? string.Empty,
            Language = language
        };
    }

    private async Task EnsureProcessorAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
            return;

        var modelBytes = await LoadModelBytesAsync(cancellationToken);
        using var modelStream = new MemoryStream(modelBytes, writable: false);
        _processor = new WhisperFactory(modelStream).CreateBuilder()
            .WithLanguage("auto")
            .Build();

        _logger.LogInformation("Loaded Whisper model for transcription.");
    }

    private async Task<byte[]> LoadModelBytesAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            var resolvedPath = Path.GetFullPath(_options.ModelPath);
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Whisper model file not found at '{resolvedPath}'.");
            }

            _logger.LogInformation("Using local Whisper model: {Path}", resolvedPath);
            return await File.ReadAllBytesAsync(resolvedPath, cancellationToken);
        }

        var modelType = ParseModelSize(_options.ModelSize);
        var downloadDirectory = string.IsNullOrWhiteSpace(_options.ModelDownloadDirectory)
            ? Directory.GetCurrentDirectory()
            : _options.ModelDownloadDirectory;

        Directory.CreateDirectory(downloadDirectory);

        var modelPath = Path.Combine(downloadDirectory, GetModelFileName(modelType));
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Downloading Whisper model {Model} to {Path}...", modelType, modelPath);
            await using var source = await WhisperGgmlDownloader.GetGgmlModelAsync(modelType, cancellationToken: cancellationToken);
            await using var destination = File.Create(modelPath);
            await source.CopyToAsync(destination, cancellationToken);
            _logger.LogInformation("Downloaded Whisper model to {Path}", modelPath);
        }
        else
        {
            _logger.LogInformation("Using cached Whisper model at {Path}", modelPath);
        }

        return await File.ReadAllBytesAsync(modelPath, cancellationToken);
    }

    private static GgmlType ParseModelSize(string? value)
    {
        var key = (value ?? "base").Trim().ToLowerInvariant().Replace("-", "").Replace(".", "");
        return key switch
        {
            "tiny" => GgmlType.Tiny,
            "tinyen" => GgmlType.TinyEn,
            "base" => GgmlType.Base,
            "baseen" => GgmlType.BaseEn,
            "small" => GgmlType.Small,
            "smallen" => GgmlType.SmallEn,
            "medium" => GgmlType.Medium,
            "mediumen" => GgmlType.MediumEn,
            "largev1" or "largev1en" => GgmlType.LargeV1,
            "largev2" or "largev2en" => GgmlType.LargeV2,
            "largev3" or "largev3en" => GgmlType.LargeV3,
            "largev3turbo" or "largev3turboen" => GgmlType.LargeV3Turbo,
            "largev4" or "largev4en" => GgmlType.LargeV4,
            _ => GgmlType.BaseEn
        };
    }

    private static string GetModelFileName(GgmlType type) => type switch
    {
        GgmlType.Tiny => "ggml-tiny.bin",
        GgmlType.TinyEn => "ggml-tiny.en.bin",
        GgmlType.Base => "ggml-base.bin",
        GgmlType.BaseEn => "ggml-base.en.bin",
        GgmlType.Small => "ggml-small.bin",
        GgmlType.SmallEn => "ggml-small.en.bin",
        GgmlType.Medium => "ggml-medium.bin",
        GgmlType.MediumEn => "ggml-medium.en.bin",
        GgmlType.LargeV1 => "ggml-large-v1.bin",
        GgmlType.LargeV2 => "ggml-large-v2.bin",
        GgmlType.LargeV3 => "ggml-large-v3.bin",
        GgmlType.LargeV3Turbo => "ggml-large-v3-turbo.bin",
        GgmlType.LargeV4 => "ggml-large-v4.bin",
        _ => "ggml-base.en.bin"
    };

    public ValueTask DisposeAsync()
    {
        _processor?.Dispose();
        return ValueTask.CompletedTask;
    }
}
