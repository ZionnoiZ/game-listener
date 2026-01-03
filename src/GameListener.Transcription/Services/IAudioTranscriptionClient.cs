using GameListener.Transcription.Models;

namespace GameListener.Transcription.Services;

public interface IAudioTranscriptionClient
{
    Task<AudioTextResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken);
}
