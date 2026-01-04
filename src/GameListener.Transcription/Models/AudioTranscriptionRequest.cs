namespace GameListener.Transcription.Models;

public sealed class AudioTranscriptionRequest
{
    public byte[] AudioBytes { get; init; } = Array.Empty<byte>();
    public string FileName { get; set; }
}
