namespace GameListener.Transcription.Models;

public sealed class AudioTextResult
{
    public string Text { get; init; } = string.Empty;

    public string? Language { get; init; }
}
