namespace GameListener.Transcription.Options;

public sealed class TranscriptionOptions
{
    public string InputPath { get; set; } = string.Empty;

    public string OutputDirectory { get; set; } = "transcripts";

    public string? FileSearchPattern { get; set; } = "*.jsonl";

    public bool OverwriteExisting { get; set; }

    public bool SkipCompleted { get; set; } = true;

    /// <summary>
    /// Optional path to a local Whisper GGML model file. When provided, downloading is skipped.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// GGML model size to download when <see cref="ModelPath"/> is not set (e.g., \"base\", \"base.en\", \"small\", \"medium\").
    /// Defaults to \"base\" to support multilingual input.
    /// </summary>
    public string ModelSize { get; set; } = "base";

    /// <summary>
    /// Directory to cache the downloaded Whisper model file. Defaults to the current directory.
    /// </summary>
    public string? ModelDownloadDirectory { get; set; }
}
