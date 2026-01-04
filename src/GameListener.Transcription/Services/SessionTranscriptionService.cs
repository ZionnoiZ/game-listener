using System.Text.Json;
using System.Text.Json.Serialization;
using GameListener.Transcription.Models;
using GameListener.Transcription.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.Transcription.Services;

public sealed class SessionTranscriptionService
{
    private readonly ILogger<SessionTranscriptionService> _logger;
    private readonly IAudioTranscriptionClient _transcriptionClient;
    private readonly TranscriptionOptions _options;

    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public SessionTranscriptionService(
        ILogger<SessionTranscriptionService> logger,
        IAudioTranscriptionClient transcriptionClient,
        IOptions<TranscriptionOptions> options)
    {
        _logger = logger;
        _transcriptionClient = transcriptionClient;
        _options = options.Value;
    }

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.InputPath))
        {
            throw new InvalidOperationException("Transcription:InputPath is required.");
        }

        var inputFiles = ResolveInputFiles(_options.InputPath, _options.FileSearchPattern ?? "*.jsonl");
        _logger.LogInformation("Found {Count} session file(s) to process.", inputFiles.Length);

        Directory.CreateDirectory(_options.OutputDirectory);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(inputFiles, parallelOptions, async (filePath, token) =>
        {
            var outputFile = Path.Combine(_options.OutputDirectory, Path.GetFileName(filePath));
            if (File.Exists(outputFile))
            {
                if (_options.OverwriteExisting)
                {
                    _logger.LogInformation("Overwriting existing transcript {File}.", outputFile);
                }
                else if (_options.SkipCompleted)
                {
                    _logger.LogInformation("Skipping already processed file {File}.", outputFile);
                    return;
                }
            }

            await ProcessFileAsync(filePath, outputFile, token);
        });
    }

    private static string[] ResolveInputFiles(string inputPath, string searchPattern)
    {
        if (File.Exists(inputPath))
        {
            return new[] { inputPath };
        }

        if (Directory.Exists(inputPath))
        {
            return Directory.GetFiles(inputPath, searchPattern, SearchOption.AllDirectories);
        }

        throw new FileNotFoundException($"Input path not found: {inputPath}");
    }

    private async Task ProcessFileAsync(string inputFile, string outputFile, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing session file {File}.", inputFile);

        await using var input = File.OpenRead(inputFile);
        using var reader = new StreamReader(input);

        await using var output = new StreamWriter(File.Open(outputFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entry = JsonSerializer.Deserialize<SessionEntry>(line, _serializerOptions);
            if (entry is null)
                continue;

            if (!string.Equals(entry.Type, "audio", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.AudioBase64))
            {
                await output.WriteLineAsync(line);
                continue;
            }

            var audioBytes = Convert.FromBase64String(entry.AudioBase64);
            var request = new AudioTranscriptionRequest
            {
                AudioBytes = audioBytes
            };

            var transcription = await _transcriptionClient.TranscribeAsync(request, cancellationToken);
            entry.Text = transcription.Text;
            entry.Language = transcription.Language;
            entry.AudioBase64 = null;

            var updatedLine = JsonSerializer.Serialize(entry, _serializerOptions);
            await output.WriteLineAsync(updatedLine);
        }
    }
}
