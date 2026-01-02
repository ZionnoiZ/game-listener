using System.Text.Json;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using GameListener.App.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class RecordingManager
{
    private readonly ILogger<RecordingManager> _logger;
    private readonly IOptions<DiscordOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RecordingSession? _session;
    private DiscordChannel? _pendingChannel;

    public RecordingManager(ILogger<RecordingManager> logger, IOptions<DiscordOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public bool IsRecording => _session is not null || _pendingChannel is not null;
    public ulong? ActiveChannelId => _session?.ChannelId ?? _pendingChannel?.Id;
    public DiscordChannel? ActiveChannel => _session?.Channel ?? _pendingChannel;

    public async Task StartRecordingAsync(DiscordClient client, DiscordChannel channel, string requestedBy)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null || _pendingChannel is not null)
            {
                throw new InvalidOperationException("Recording is already active.");
            }

            var voiceNext = client.GetVoiceNext();
            if (voiceNext is null)
            {
                throw new InvalidOperationException("VoiceNext is not configured on the Discord client.");
            }

            _pendingChannel = channel;
            _logger.LogInformation("Connecting to voice channel {Channel}", channel.Name);
            var connectTask = voiceNext.ConnectAsync(channel);
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != connectTask)
            {
                throw new TimeoutException($"Timed out connecting to voice channel {channel.Name}.");
            }

            var connection = await connectTask;

            var outputPath = EnsureOutputDirectory();
            var session = new RecordingSession(connection, channel, outputPath, requestedBy, _logger, _options.Value);
            _session = session;
            _pendingChannel = null;

            try
            {
                await session.InitializeAsync();
            }
            catch
            {
                _session = null;
                await session.DisposeAsync();
                _pendingChannel = null;
                throw;
            }
        }
        finally
        {
            if (_session is null)
            {
                _pendingChannel = null;
            }
            _gate.Release();
        }
    }

    public async Task StopRecordingAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                return;
            }

            _logger.LogInformation("Stopping recording: {Reason}", reason);
            await _session.DisposeAsync();
            _session = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string EnsureOutputDirectory()
    {
        var directory = Path.IsPathRooted(_options.Value.OutputDirectory)
            ? _options.Value.OutputDirectory
            : Path.Combine(Directory.GetCurrentDirectory(), _options.Value.OutputDirectory);

        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingSession : IAsyncDisposable
    {
        private readonly VoiceNextConnection _connection;
        private readonly ILogger _logger;
        private readonly DiscordOptions _options;
        private readonly SessionWriter _writer;

        public DiscordChannel Channel { get; }
        public ulong ChannelId => Channel.Id;

        public RecordingSession(VoiceNextConnection connection, DiscordChannel channel, string directory, string requestedBy, ILogger logger, DiscordOptions options)
        {
            _connection = connection;
            Channel = channel;
            _logger = logger;
            _options = options;
            var filePath = BuildSessionFilePath(directory, channel, requestedBy);
            _writer = new SessionWriter(filePath, _logger);
        }

        public async Task InitializeAsync()
        {
            _connection.VoiceReceived += HandleVoiceReceived;
            _logger.LogInformation("Voice session started in {Channel}", Channel.Name);
            await _writer.WriteHeaderAsync(Channel);
        }

        private async Task HandleVoiceReceived(VoiceNextConnection connection, DSharpPlus.VoiceNext.EventArgs.VoiceReceiveEventArgs e)
        {
            try
            {
                await _writer.WriteEntryAsync(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write voice packet");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _connection.VoiceReceived -= HandleVoiceReceived;
            _connection.Disconnect();
            await _writer.DisposeAsync();
        }

        private string BuildSessionFilePath(string directory, DiscordChannel channel, string requestedBy)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeName = string.Join("_", channel.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var fileName = $"session_{timestamp}_{safeName}_by_{SanitizeName(requestedBy)}.jsonl";
            return Path.Combine(directory, fileName);
        }

        private static string SanitizeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
        }
    }

    private sealed class SessionWriter : IAsyncDisposable
    {
        private readonly string _filePath;
        private readonly StreamWriter _writer;
        private readonly ILogger _logger;
        private readonly JsonSerializerOptions _serializerOptions = new()
        {
            WriteIndented = false
        };

        public SessionWriter(string filePath, ILogger logger)
        {
            _filePath = filePath;
            _logger = logger;
            _writer = new StreamWriter(File.Open(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }

        public async Task WriteHeaderAsync(DiscordChannel channel)
        {
            var header = new
            {
                type = "session-start",
                startedAt = DateTimeOffset.UtcNow,
                guildId = channel.Guild.Id,
                guildName = channel.Guild.Name,
                channelId = channel.Id,
                channelName = channel.Name
            };

            await WriteJsonAsync(header);
        }

        public async Task WriteEntryAsync(DSharpPlus.VoiceNext.EventArgs.VoiceReceiveEventArgs e)
        {
            var payload = new
            {
                type = "audio",
                receivedAt = DateTimeOffset.UtcNow,
                userId = e.User?.Id,
                userName = e.User?.Username,
                // DSharpPlus exposes raw PCM data. Encode to base64 so it can be persisted quickly.
                audio = Convert.ToBase64String(e.PcmData.ToArray())
            };

            await WriteJsonAsync(payload);
        }

        private async Task WriteJsonAsync(object payload)
        {
            var line = JsonSerializer.Serialize(payload, _serializerOptions);
            await _writer.WriteLineAsync(line);
            await _writer.FlushAsync();
            _logger.LogDebug("Wrote packet to {File}", _filePath);
        }

        public async ValueTask DisposeAsync()
        {
            await WriteJsonAsync(new { type = "session-end", endedAt = DateTimeOffset.UtcNow });
            await _writer.DisposeAsync();
            _logger.LogInformation("Session written to {FilePath}", _filePath);
        }
    }
}
