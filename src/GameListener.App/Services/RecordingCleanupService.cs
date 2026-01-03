using Microsoft.Extensions.Hosting;

namespace GameListener.App.Services;

public sealed class RecordingCleanupService : IHostedService
{
    private readonly RecordingManager _recordingManager;

    public RecordingCleanupService(RecordingManager recordingManager)
    {
        _recordingManager = recordingManager;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) =>
        _recordingManager.StopRecordingAsync("shutdown", cancellationToken);
}
